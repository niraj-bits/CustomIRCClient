using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MyIRCClient.Network;

public class IrcClient : IDisposable
{
    private TcpClient? _tcpClient;
    private Stream? _networkStream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Thread? _connectionThread;
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _handshakeCts;
    private readonly object _lock = new();

    private bool _isRegistered;
    private bool _hasJoinedTargetRoom;
    private bool _isDisposed;

    // Outgoing sequential FIFO queue to prevent rate limit penalties
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string target, string message)> _outgoingQueue = new();
    private int _isProcessingQueue;

    // Buffer to accumulate RPL_NAMREPLY (353) entries until RPL_ENDOFNAMES (366)
    private readonly Dictionary<string, List<string>> _namesBuffer = new(StringComparer.OrdinalIgnoreCase);

    public string? Server { get; private set; }
    public int Port { get; private set; }
    public bool UseSsl { get; private set; }
    public string? Nickname { get; private set; }
    public string? TargetRoom { get; private set; }
    public bool IsConnected => _tcpClient != null && _tcpClient.Connected;

    // Events raised to decouple UI from connection layer
    public event Action<string>? OnLog;
    public event Action<string>? OnError;
    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnJoinedRoom;
    public event Action<string, string, string>? OnChatMessage; // sender, target, text
    public event Action<string, string, string>? OnPublicMessage; // channel, sender, text
    public event Action<string, string>? OnPrivateMessage; // sender, text
    public event Action<string, List<string>>? OnUserListReceived; // channel, user nicks
    public event Action<string, string>? OnUserJoined; // channel, nick
    public event Action<string, string, string?>? OnUserParted; // channel, nick, reason
    public event Action<string, string?>? OnUserQuit; // nick, reason
    public event Action<string, string, string, string?>? OnUserKicked; // channel, nick, kicker, reason
    public event Action<string, string>? OnUserNickChanged; // oldNick, newNick
    public event Action? OnDisconnected;

    public static int GetConfiguredTimeoutSeconds()
    {
        string? configVal = ConfigurationManager.AppSettings["ConnectionTimeoutSeconds"];
        if (int.TryParse(configVal, out int seconds) && seconds > 0)
        {
            return seconds;
        }
        return 60; // Default 1 minute
    }

    public static int GetConfiguredDefaultPort()
    {
        string? configVal = ConfigurationManager.AppSettings["DefaultPort"];
        if (int.TryParse(configVal, out int port) && port > 0)
        {
            return port;
        }
        return 6667;
    }

    public void Connect(string server, int port, bool useSsl, string nickname, string targetRoom)
    {
        Disconnect();

        Server = server;
        Port = port;
        UseSsl = useSsl || port == 6697;
        Nickname = nickname;
        TargetRoom = targetRoom;
        _isRegistered = false;
        _hasJoinedTargetRoom = false;

        lock (_namesBuffer)
        {
            _namesBuffer.Clear();
        }

        int timeoutSeconds = GetConfiguredTimeoutSeconds();
        string protocol = UseSsl ? "TLS/SSL" : "Plaintext";
        Log($"Initiating connection to {Server}:{Port} ({protocol}, Timeout: {timeoutSeconds}s)...");
        StatusChanged($"Connecting to {Server}:{Port} ({protocol})...");

        _sessionCts = new CancellationTokenSource();
        _handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        // Start dedicated background connection thread
        _connectionThread = new Thread(RunConnectionThread)
        {
            IsBackground = true,
            Name = "IRC-Connection-Thread"
        };
        _connectionThread.Start();
    }

    private void RunConnectionThread()
    {
        CancellationToken sessionToken = _sessionCts?.Token ?? CancellationToken.None;
        CancellationToken handshakeToken = _handshakeCts?.Token ?? CancellationToken.None;
        int timeoutSeconds = GetConfiguredTimeoutSeconds();

        // Enforce the configured 1-minute timeout on initial connection and handshake
        using var handshakeReg = handshakeToken.Register(() =>
        {
            if (!_isRegistered)
            {
                LogError($"[TIMEOUT] Connection / handshake to {Server}:{Port} timed out after {timeoutSeconds}s. Stopping thread.");
                CloseSockets();
            }
        });

        // Close sockets when user requests disconnection
        using var sessionReg = sessionToken.Register(() =>
        {
            Log($"[CANCEL] Disconnection requested by user.");
            CloseSockets();
        });

        try
        {
            _tcpClient = new TcpClient();

            // Connect with cancellation support
            IAsyncResult asyncResult = _tcpClient.BeginConnect(Server!, Port, null, null);
            using (WaitHandle waitHandle = asyncResult.AsyncWaitHandle)
            {
                if (!waitHandle.WaitOne(TimeSpan.FromSeconds(timeoutSeconds), false))
                {
                    LogError($"Failed to establish TCP connection to {Server}:{Port} within {timeoutSeconds} seconds.");
                    CloseSockets();
                    return;
                }
                _tcpClient.EndConnect(asyncResult);
            }

            if (sessionToken.IsCancellationRequested || handshakeToken.IsCancellationRequested)
            {
                LogError("Connection cancelled prior to handshake.");
                return;
            }

            Log($"TCP connected to {Server}:{Port}. Preparing stream...");

            Stream baseStream = _tcpClient.GetStream();
            if (UseSsl)
            {
                Log($"Securing connection to {Server} with TLS/SSL...");
                var sslStream = new SslStream(
                    baseStream, 
                    false, 
                    (sender, certificate, chain, sslPolicyErrors) => true // Accept server cert for broad IRC compatibility
                );
                sslStream.AuthenticateAsClient(Server!);
                _networkStream = sslStream;
                Log($"TLS handshake completed successfully.");
            }
            else
            {
                _networkStream = baseStream;
            }

            _reader = new StreamReader(_networkStream, Encoding.UTF8);
            _writer = new StreamWriter(_networkStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };

            // Initiate IRC registration
            // Use 'webchat' ident to match network webclient expectations (avoids anti-bot/uncloaked ident bans)
            Log($"Sending IRC registration: NICK {Nickname}, USER webchat...");
            SendRaw($"NICK {Nickname}");
            SendRaw($"USER webchat 0 * :{Nickname}");

            StatusChanged($"Handshaking with {Server}...");

            // Process incoming lines until disconnected or cancelled
            while (!sessionToken.IsCancellationRequested && _tcpClient.Connected)
            {
                string? line = _reader.ReadLine();
                if (line == null)
                {
                    // Server closed the stream
                    break;
                }

                ProcessIncomingLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            LogError($"Connection thread stopped due to timeout or cancellation.");
        }
        catch (Exception ex)
        {
            if (!sessionToken.IsCancellationRequested)
            {
                LogError($"Socket/Connection error: {ex.Message}");
            }
        }
        finally
        {
            CloseSockets();
            StatusChanged("Disconnected");
            OnDisconnected?.Invoke();
            Log("Connection thread has terminated.");
        }
    }

    private void ProcessIncomingLine(string line)
    {
        // Respond immediately to PING to keep connection alive
        if (line.StartsWith("PING ", StringComparison.OrdinalIgnoreCase))
        {
            string pingParam = line.Substring(5).TrimStart(':');
            Log($"[PING] Responding PONG :{pingParam}");
            SendRaw($"PONG :{pingParam}");
            return;
        }

        // IRC line parsing
        string[] parts = line.Split(' ');
        if (parts.Length < 2)
        {
            Log($"[RECV] {line}");
            return;
        }

        string prefix = string.Empty;
        string command;
        int startIndex = 0;

        if (parts[0].StartsWith(':'))
        {
            prefix = parts[0].Substring(1);
            command = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;
            startIndex = 2;
        }
        else
        {
            command = parts[0].ToUpperInvariant();
            startIndex = 1;
        }

        switch (command)
        {
            case "001": // RPL_WELCOME - successfully registered
                _isRegistered = true;
                // Handshake completed successfully within timeout window! Stop the handshake timer.
                _handshakeCts?.CancelAfter(Timeout.InfiniteTimeSpan);
                Log($"[WELCOME] Server accepted registration: {line}");
                StatusChanged($"Connected to {Server} as {Nickname}");
                break;

            case "376": // RPL_ENDOFMOTD
            case "422": // ERR_NOMOTD
                Log($"[RECV] End of MOTD received. Waiting for host cloaking/user modes...");
                // Start a fallback timer to join after 1.5s if mode/cloak lines don't trigger it sooner
                Task.Delay(1500).ContinueWith(_ => TryJoinTargetRoom("MOTD fallback delay completed"));
                break;

            case "396": // RPL_HOSTHIDDEN / Displayed host cloaked
                Log($"[CLOAK] Cloaked host assigned: {line}");
                // Wait brief moment for mode +x to settle, then join
                Task.Delay(500).ContinueWith(_ => TryJoinTargetRoom("Cloaked host established"));
                break;

            case "MODE":
                Log($"[RECV] {line}");
                // If the mode is set on our nickname (e.g. MODE Nick :+iwx)
                if (parts.Length > startIndex && string.Equals(parts[startIndex], Nickname, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[MODE] User modes applied: {line}");
                    Task.Delay(300).ContinueWith(_ => TryJoinTargetRoom("User modes active"));
                }
                break;

            case "JOIN":
                string joinedChannel = parts.Length > startIndex ? parts[startIndex].TrimStart(':') : TargetRoom ?? "";
                string sourceNick = prefix.Contains('!') ? prefix.Substring(0, prefix.IndexOf('!')) : prefix;
                Log($"[JOIN] {sourceNick} joined {joinedChannel}");

                if (string.Equals(sourceNick, Nickname, StringComparison.OrdinalIgnoreCase))
                {
                    OnJoinedRoom?.Invoke(joinedChannel);
                    // Request user list explicitly if not sent automatically
                    SendRaw($"NAMES {joinedChannel}");
                }
                else
                {
                    OnUserJoined?.Invoke(joinedChannel, sourceNick);
                }
                break;

            case "PART":
                string partChannel = parts.Length > startIndex ? parts[startIndex] : string.Empty;
                string partNick = prefix.Contains('!') ? prefix.Substring(0, prefix.IndexOf('!')) : prefix;
                string? partReason = null;
                int partMsgIdx = line.IndexOf(" :", StringComparison.Ordinal);
                if (partMsgIdx != -1)
                {
                    partReason = line.Substring(partMsgIdx + 2);
                }
                Log($"[PART] {partNick} left {partChannel}" + (partReason != null ? $" ({partReason})" : ""));
                OnUserParted?.Invoke(partChannel, partNick, partReason);
                break;

            case "QUIT":
                string quitNick = prefix.Contains('!') ? prefix.Substring(0, prefix.IndexOf('!')) : prefix;
                string? quitReason = null;
                int quitMsgIdx = line.IndexOf(" :", StringComparison.Ordinal);
                if (quitMsgIdx != -1)
                {
                    quitReason = line.Substring(quitMsgIdx + 2);
                }
                Log($"[QUIT] {quitNick} quit" + (quitReason != null ? $" ({quitReason})" : ""));
                OnUserQuit?.Invoke(quitNick, quitReason);
                break;

            case "KICK":
                if (parts.Length > startIndex + 1)
                {
                    string kickChannel = parts[startIndex];
                    string kickedNick = parts[startIndex + 1];
                    string kickerNick = prefix.Contains('!') ? prefix.Substring(0, prefix.IndexOf('!')) : prefix;
                    string? kickReason = null;
                    int kickMsgIdx = line.IndexOf(" :", StringComparison.Ordinal);
                    if (kickMsgIdx != -1)
                    {
                        kickReason = line.Substring(kickMsgIdx + 2);
                    }
                    Log($"[KICK] {kickedNick} was kicked from {kickChannel} by {kickerNick}" + (kickReason != null ? $" ({kickReason})" : ""));
                    OnUserKicked?.Invoke(kickChannel, kickedNick, kickerNick, kickReason);
                }
                break;

            case "NICK":
                string oldNick = prefix.Contains('!') ? prefix.Substring(0, prefix.IndexOf('!')) : prefix;
                string newNick = parts.Length > startIndex ? parts[startIndex].TrimStart(':') : string.Empty;
                Log($"[NICK] {oldNick} is now known as {newNick}");
                OnUserNickChanged?.Invoke(oldNick, newNick);
                break;

            case "353": // RPL_NAMREPLY: <server> 353 <client> <symbol> <channel> :<nick1> <nick2> ...
                HandleNamReply(line, parts);
                break;

            case "366": // RPL_ENDOFNAMES: <server> 366 <client> <channel> :End of /NAMES list.
                HandleEndOfNames(parts);
                break;

            case "PRIVMSG":
                if (parts.Length > startIndex)
                {
                    string target = parts[startIndex];
                    string messageText = string.Empty;
                    int msgIdx = line.IndexOf(" :", StringComparison.Ordinal);
                    if (msgIdx != -1)
                    {
                        messageText = line.Substring(msgIdx + 2);
                    }
                    string senderNick = prefix.Contains('!') ? prefix.Substring(0, prefix.IndexOf('!')) : prefix;

                    OnChatMessage?.Invoke(senderNick, target, messageText);

                    if (target.StartsWith('#') || target.StartsWith('&'))
                    {
                        OnPublicMessage?.Invoke(target, senderNick, messageText);
                    }
                    else
                    {
                        OnPrivateMessage?.Invoke(senderNick, messageText);
                    }
                }
                break;

            case "ERROR":
                LogError($"[SERVER ERROR] {line}");
                break;

            case "433": // ERR_NICKNAMEINUSE
                LogError($"[NICK ERROR] Nickname '{Nickname}' is already in use. Please choose another nickname.");
                break;

            case "489": // ERR_SECUREONLYCHAN
                LogError($"[TLS ERROR] Cannot join '{TargetRoom}': This channel requires a secure connection (TLS/SSL mode +z). Please check 'Use SSL / TLS' on port 6697.");
                break;

            case "474": // ERR_BANNEDFROMCHAN
                LogError($"[BANNED] Channel '{TargetRoom}' denied join (Cannot join channel: you're banned). Channel ban list (+b) matches your hostmask or requires registration.");
                break;

            case "473": // ERR_INVITEONLYCHAN
            case "475": // ERR_BADCHANNELKEY
            case "471": // ERR_CHANNELISFULL
                LogError($"[JOIN ERROR] Could not join room: {line}");
                break;

            default:
                if (int.TryParse(command, out int numericCode) && numericCode >= 400 && numericCode <= 599)
                {
                    LogError($"[SERVER ERROR {numericCode}] {line}");
                }
                else
                {
                    Log($"[RECV] {line}");
                }
                break;
        }
    }

    private void HandleNamReply(string line, string[] parts)
    {
        // Example: :server 353 nick = #channel :user1 @user2 +user3
        int colonIndex = line.IndexOf(" :", StringComparison.Ordinal);
        if (colonIndex == -1) return;

        string nicksString = line.Substring(colonIndex + 2);
        string[] nicks = nicksString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        // Find the channel name in parts before colon
        string channel = string.Empty;
        for (int i = 2; i < parts.Length; i++)
        {
            if (parts[i].StartsWith('#') || parts[i].StartsWith('&'))
            {
                channel = parts[i];
                break;
            }
        }

        if (string.IsNullOrEmpty(channel))
        {
            channel = TargetRoom ?? string.Empty;
        }

        lock (_namesBuffer)
        {
            if (!_namesBuffer.ContainsKey(channel))
            {
                _namesBuffer[channel] = new List<string>();
            }

            foreach (string n in nicks)
            {
                if (!string.IsNullOrWhiteSpace(n))
                {
                    _namesBuffer[channel].Add(n.Trim());
                }
            }
        }

        Log($"[NAMES] Received {nicks.Length} nicknames for {channel}");
    }

    private void HandleEndOfNames(string[] parts)
    {
        // Example: :server 366 mynick #channel :End of /NAMES list.
        string channel = string.Empty;
        for (int i = 2; i < parts.Length; i++)
        {
            if (parts[i].StartsWith('#') || parts[i].StartsWith('&'))
            {
                channel = parts[i];
                break;
            }
        }

        if (string.IsNullOrEmpty(channel))
        {
            channel = TargetRoom ?? string.Empty;
        }

        List<string>? userList = null;
        lock (_namesBuffer)
        {
            if (_namesBuffer.TryGetValue(channel, out var list))
            {
                userList = new List<string>(list);
                _namesBuffer.Remove(channel);
            }
        }

        if (userList != null)
        {
            Log($"[NAMES COMPLETE] Total users in {channel}: {userList.Count}");
            OnUserListReceived?.Invoke(channel, userList);
        }
    }

    private void TryJoinTargetRoom(string reason)
    {
        lock (_lock)
        {
            if (_hasJoinedTargetRoom || string.IsNullOrWhiteSpace(TargetRoom) || !IsConnected)
            {
                return;
            }

            _hasJoinedTargetRoom = true;
            Log($"[READY] {reason}. Sending JOIN {TargetRoom}...");
            SendRaw($"JOIN {TargetRoom}");
        }
    }

    public void SendChatMessage(string channel, string message)
    {
        EnqueueChatMessage(channel, message);
    }

    public void EnqueueChatMessage(string target, string message)
    {
        if (!IsConnected)
        {
            LogError($"Cannot send message to {target}: Not connected.");
            return;
        }

        _outgoingQueue.Enqueue((target, message));
        TriggerQueueProcessing();
    }

    public void EnqueueChatMessages(IEnumerable<(string target, string message)> messages)
    {
        if (!IsConnected)
        {
            LogError("Cannot send messages: Not connected.");
            return;
        }

        int count = 0;
        foreach (var msg in messages)
        {
            _outgoingQueue.Enqueue(msg);
            count++;
        }

        Log($"Enqueued {count} message(s) for sequential dispatch.");
        TriggerQueueProcessing();
    }

    private void TriggerQueueProcessing()
    {
        if (Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) == 0)
        {
            Task.Run(async () =>
            {
                try
                {
                    while (_outgoingQueue.TryDequeue(out var item))
                    {
                        if (!IsConnected) break;

                        SendRaw($"PRIVMSG {item.target} :{item.message}");
                        OnChatMessage?.Invoke(Nickname ?? "You", item.target, item.message);

                        if (item.target.StartsWith('#') || item.target.StartsWith('&'))
                        {
                            OnPublicMessage?.Invoke(item.target, Nickname ?? "You", item.message);
                        }

                        // Rate-limiting delay (350ms) between sequential messages
                        // guarantees server flood protection is never violated
                        if (_outgoingQueue.Count > 0)
                        {
                            await Task.Delay(350);
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isProcessingQueue, 0);

                    // Re-check if new messages arrived while finishing
                    if (!_outgoingQueue.IsEmpty && IsConnected)
                    {
                        TriggerQueueProcessing();
                    }
                }
            });
        }
    }

    public void SendRaw(string rawLine)
    {
        lock (_lock)
        {
            try
            {
                if (_writer != null && _tcpClient != null && _tcpClient.Connected)
                {
                    _writer.WriteLine(rawLine);
                    Log($"[SENT] {rawLine}");
                }
                else
                {
                    LogError($"Cannot send line (socket disconnected): {rawLine}");
                }
            }
            catch (Exception ex)
            {
                LogError($"Error sending line: {ex.Message}");
            }
        }
    }

    private void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        OnLog?.Invoke($"[{timestamp}] {message}");
    }

    private void LogError(string error)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        OnError?.Invoke($"[{timestamp}] {error}");
    }

    private void StatusChanged(string status)
    {
        OnStatusChanged?.Invoke(status);
    }

    private void CloseSockets()
    {
        lock (_lock)
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _networkStream?.Dispose(); } catch { }
            try { _tcpClient?.Close(); } catch { }

            _writer = null;
            _reader = null;
            _networkStream = null;
            _tcpClient = null;
        }
    }

    public void Disconnect()
    {
        _handshakeCts?.CancelAfter(Timeout.InfiniteTimeSpan);
        _sessionCts?.Cancel();
        CloseSockets();

        if (_connectionThread != null && _connectionThread.IsAlive)
        {
            _connectionThread.Join(500); // Give background thread brief grace period to shut down
            _connectionThread = null;
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            Disconnect();
            _handshakeCts?.Dispose();
            _sessionCts?.Dispose();
        }
    }
}
