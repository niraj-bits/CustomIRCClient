using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyIRCClient.Models;
using MyIRCClient.Network;

namespace MyIRCClient;

public partial class MainWindow : Window
{
    private readonly IrcClient _ircClient;
    private readonly ObservableCollection<ConversationItem> _conversations = new();
    private readonly List<IrcUser> _allRoomUsers = new();
    private readonly ObservableCollection<IrcUser> _filteredUsers = new();
    private ConversationItem? _activeConversation;

    public MainWindow()
    {
        InitializeComponent();

        ConversationsListBox.ItemsSource = _conversations;
        UsersListBox.ItemsSource = _filteredUsers;

        _ircClient = new IrcClient();
        _ircClient.OnLog += HandleLog;
        _ircClient.OnError += HandleError;
        _ircClient.OnStatusChanged += HandleStatusChanged;
        _ircClient.OnJoinedRoom += HandleJoinedRoom;
        _ircClient.OnPublicMessage += HandlePublicMessage;
        _ircClient.OnPrivateMessage += HandlePrivateMessage;
        _ircClient.OnUserListReceived += HandleUserListReceived;
        _ircClient.OnUserJoined += HandleUserJoined;
        _ircClient.OnUserParted += HandleUserParted;
        _ircClient.OnUserQuit += HandleUserQuit;
        _ircClient.OnUserKicked += HandleUserKicked;
        _ircClient.OnUserNickChanged += HandleUserNickChanged;
        _ircClient.OnDisconnected += HandleDisconnected;
    }

    private void ConnectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectDialog
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            Title = $"MyIRCClient - {dialog.Server} ({dialog.Nickname})";

            // Reset conversation and user state for fresh connection
            _conversations.Clear();
            _allRoomUsers.Clear();
            _filteredUsers.Clear();
            _activeConversation = null;
            UserListHeaderTextBlock.Text = "Users (0)";

            // Create initial channel conversation placeholder
            var channelConvo = GetOrCreateConversation(dialog.Room, isChannel: true, makeActive: true);
            string proto = dialog.UseSsl ? "TLS" : "Plaintext";
            channelConvo.Buffer.AppendLine($"Connecting to {dialog.Server}:{dialog.Port} ({proto}) as {dialog.Nickname}...");
            UpdateActiveChatDisplay();

            // Connect using separate thread with configured timeout and SSL/TLS option
            _ircClient.Connect(dialog.Server, dialog.Port, dialog.UseSsl, dialog.Nickname, dialog.Room);
        }
    }

    private void DisconnectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _ircClient.Disconnect();
    }

    private ConversationItem GetOrCreateConversation(string name, bool isChannel, bool makeActive = false)
    {
        var existing = _conversations.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (makeActive)
            {
                ConversationsListBox.SelectedItem = existing;
            }
            return existing;
        }

        var newConvo = new ConversationItem
        {
            Name = name,
            IsChannel = isChannel,
            HasUnread = false,
            IsDisconnected = false,
            UnreadCount = 0
        };

        _conversations.Add(newConvo);

        if (makeActive)
        {
            ConversationsListBox.SelectedItem = newConvo;
        }

        return newConvo;
    }

    private void SetActiveConversation(ConversationItem convo)
    {
        _activeConversation = convo;

        // Clear unread alerts when the user opens the conversation
        convo.HasUnread = false;
        convo.UnreadCount = 0;

        if (convo.IsChannel)
        {
            ChatHeaderTextBlock.Text = $"Room: {convo.Name} on {_ircClient.Server ?? "IRC"}";
        }
        else
        {
            string status = convo.IsDisconnected ? " [Disconnected]" : "";
            ChatHeaderTextBlock.Text = $"Direct Message with @{convo.Name}{status}";
        }

        UpdateActiveChatDisplay();
        MessageInputTextBox.Focus();
    }

    private void UpdateActiveChatDisplay()
    {
        if (_activeConversation != null)
        {
            IInputElement? currentFocus = Keyboard.FocusedElement;

            ChatMessagesTextBox.Text = _activeConversation.Buffer.ToString();
            ChatScrollViewer.ScrollToEnd();

            // Prevent focus theft: ensure focus remains on whichever control currently has it
            if (currentFocus != null && !currentFocus.IsKeyboardFocused)
            {
                currentFocus.Focus();
            }
        }
    }

    private bool MatchesFilter(string nick)
    {
        string filter = UserFilterTextBox.Text.Trim();
        return string.IsNullOrWhiteSpace(filter) || nick.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void AddUserToList(string nick)
    {
        if (_allRoomUsers.Any(u => string.Equals(u.Nickname, nick, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var newUser = new IrcUser(nick);
        _allRoomUsers.Add(newUser);
        _allRoomUsers.Sort();

        if (MatchesFilter(nick))
        {
            // Insert in sorted position without clearing the collection to preserve selections & focus
            int insertIndex = 0;
            while (insertIndex < _filteredUsers.Count && _filteredUsers[insertIndex].CompareTo(newUser) < 0)
            {
                insertIndex++;
            }
            _filteredUsers.Insert(insertIndex, newUser);
        }

        UserListHeaderTextBlock.Text = $"Users ({_allRoomUsers.Count})";
    }

    private void RemoveUserFromList(string nick)
    {
        _allRoomUsers.RemoveAll(u => string.Equals(u.Nickname, nick, StringComparison.OrdinalIgnoreCase));

        var existingInFiltered = _filteredUsers.FirstOrDefault(u => string.Equals(u.Nickname, nick, StringComparison.OrdinalIgnoreCase));
        if (existingInFiltered != null)
        {
            _filteredUsers.Remove(existingInFiltered);
        }

        UserListHeaderTextBlock.Text = $"Users ({_allRoomUsers.Count})";
    }

    private void ConversationsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationsListBox.SelectedItem is ConversationItem selectedConvo)
        {
            SetActiveConversation(selectedConvo);
        }
    }

    private void HandleLog(string log)
    {
        Dispatcher.InvokeAsync(() =>
        {
            LogTextBox.AppendText(log + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }

    private void HandleError(string error)
    {
        Dispatcher.InvokeAsync(() =>
        {
            LogTextBox.AppendText("[ERROR] " + error + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }

    private void HandleStatusChanged(string status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            StatusTextBlock.Text = status;
        });
    }

    private void HandleJoinedRoom(string channel)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var convo = GetOrCreateConversation(channel, isChannel: true, makeActive: true);
            string time = DateTime.Now.ToString("HH:mm");
            convo.Buffer.AppendLine($"[{time}] *** You have joined {channel} ***");

            if (_activeConversation == convo)
            {
                UpdateActiveChatDisplay();
            }

            MessageInputTextBox.IsEnabled = true;
            SendButton.IsEnabled = true;
            MessageInputTextBox.Focus();
        });
    }

    private void HandlePublicMessage(string channel, string sender, string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var convo = GetOrCreateConversation(channel, isChannel: true);
            string time = DateTime.Now.ToString("HH:mm");
            convo.Buffer.AppendLine($"[{time}] <{sender}> {message}");

            if (_activeConversation == convo)
            {
                UpdateActiveChatDisplay();
            }
            else
            {
                // Mark unread if not currently viewing this room
                convo.HasUnread = true;
                convo.UnreadCount++;
            }
        });
    }

    private void HandlePrivateMessage(string sender, string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // Direct message from a user: get or create their separate conversation pane
            var convo = GetOrCreateConversation(sender, isChannel: false);
            convo.IsDisconnected = false; // Sender is active

            string time = DateTime.Now.ToString("HH:mm");
            convo.Buffer.AppendLine($"[{time}] <{sender}> {message}");

            if (_activeConversation == convo)
            {
                UpdateActiveChatDisplay();
            }
            else
            {
                // User nick text changes color & becomes bold in the left pane!
                convo.HasUnread = true;
                convo.UnreadCount++;
            }
        });
    }

    private void HandleUserListReceived(string channel, List<string> rawNicks)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _allRoomUsers.Clear();
            foreach (string raw in rawNicks)
            {
                _allRoomUsers.Add(new IrcUser(raw));
            }

            _allRoomUsers.Sort();
            RefreshUserListUI();

            var channelConvo = _conversations.FirstOrDefault(c => string.Equals(c.Name, channel, StringComparison.OrdinalIgnoreCase));
            if (channelConvo != null)
            {
                string time = DateTime.Now.ToString("HH:mm");
                channelConvo.Buffer.AppendLine($"[{time}] *** User list loaded ({_allRoomUsers.Count} users online) ***");
                if (_activeConversation == channelConvo)
                {
                    UpdateActiveChatDisplay();
                }
            }
        });
    }

    private void HandleUserJoined(string channel, string nick)
    {
        Dispatcher.InvokeAsync(() =>
        {
            AddUserToList(nick);

            // If we have an open DM conversation with this user, reset disconnected status
            var dmConvo = _conversations.FirstOrDefault(c => !c.IsChannel && string.Equals(c.Name, nick, StringComparison.OrdinalIgnoreCase));
            if (dmConvo != null)
            {
                dmConvo.IsDisconnected = false;
            }

            var channelConvo = _conversations.FirstOrDefault(c => string.Equals(c.Name, channel, StringComparison.OrdinalIgnoreCase));
            if (channelConvo != null)
            {
                string time = DateTime.Now.ToString("HH:mm");
                channelConvo.Buffer.AppendLine($"[{time}] --> {nick} has joined {channel}");
                if (_activeConversation == channelConvo)
                {
                    UpdateActiveChatDisplay();
                }
            }
        });
    }

    private void HandleUserParted(string channel, string nick, string? reason)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RemoveUserFromList(nick);

            string time = DateTime.Now.ToString("HH:mm");
            string reasonText = string.IsNullOrEmpty(reason) ? "" : $" ({reason})";

            // If we have an open DM conversation with this user, flag as disconnected (turns RED in left pane)
            var dmConvo = _conversations.FirstOrDefault(c => !c.IsChannel && string.Equals(c.Name, nick, StringComparison.OrdinalIgnoreCase));
            if (dmConvo != null)
            {
                dmConvo.IsDisconnected = true;
                dmConvo.Buffer.AppendLine($"[{time}] *** {nick} has left {channel}{reasonText} ***");
            }

            var channelConvo = _conversations.FirstOrDefault(c => string.Equals(c.Name, channel, StringComparison.OrdinalIgnoreCase));
            if (channelConvo != null)
            {
                channelConvo.Buffer.AppendLine($"[{time}] <-- {nick} has left {channel}{reasonText}");
                if (_activeConversation == channelConvo)
                {
                    UpdateActiveChatDisplay();
                }
            }
        });
    }

    private void HandleUserQuit(string nick, string? reason)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RemoveUserFromList(nick);

            string time = DateTime.Now.ToString("HH:mm");
            string reasonText = string.IsNullOrEmpty(reason) ? "" : $" ({reason})";

            // If we are talking one-to-one with this person, change their text color to RED
            var dmConvo = _conversations.FirstOrDefault(c => !c.IsChannel && string.Equals(c.Name, nick, StringComparison.OrdinalIgnoreCase));
            if (dmConvo != null)
            {
                dmConvo.IsDisconnected = true;
                dmConvo.Buffer.AppendLine($"[{time}] *** {nick} has disconnected{reasonText} ***");
            }

            foreach (var convo in _conversations)
            {
                if (convo.IsChannel)
                {
                    convo.Buffer.AppendLine($"[{time}] <-- {nick} has quit{reasonText}");
                }
            }

            UpdateActiveChatDisplay();
        });
    }

    private void HandleUserKicked(string channel, string nick, string kicker, string? reason)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RemoveUserFromList(nick);

            var channelConvo = _conversations.FirstOrDefault(c => string.Equals(c.Name, channel, StringComparison.OrdinalIgnoreCase));
            if (channelConvo != null)
            {
                string time = DateTime.Now.ToString("HH:mm");
                string reasonText = string.IsNullOrEmpty(reason) ? "" : $" ({reason})";
                channelConvo.Buffer.AppendLine($"[{time}] <-- {nick} was kicked by {kicker}{reasonText}");
                if (_activeConversation == channelConvo)
                {
                    UpdateActiveChatDisplay();
                }
            }
        });
    }

    private void HandleUserNickChanged(string oldNick, string newNick)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RemoveUserFromList(oldNick);
            AddUserToList(newNick);

            // Update DM conversation if user changed their nick
            var dmConvo = _conversations.FirstOrDefault(c => !c.IsChannel && string.Equals(c.Name, oldNick, StringComparison.OrdinalIgnoreCase));
            if (dmConvo != null)
            {
                dmConvo.Name = newNick;
            }

            string time = DateTime.Now.ToString("HH:mm");
            foreach (var convo in _conversations)
            {
                convo.Buffer.AppendLine($"[{time}] -- {oldNick} is now known as {newNick}");
            }

            UpdateActiveChatDisplay();
        });
    }

    private void HandleDisconnected()
    {
        Dispatcher.InvokeAsync(() =>
        {
            MessageInputTextBox.IsEnabled = false;
            SendButton.IsEnabled = false;
            _allRoomUsers.Clear();
            _filteredUsers.Clear();
            UserListHeaderTextBlock.Text = "Users (0)";

            if (_activeConversation != null)
            {
                string time = DateTime.Now.ToString("HH:mm");
                _activeConversation.Buffer.AppendLine($"[{time}] *** Disconnected from server ***");
                UpdateActiveChatDisplay();
            }
        });
    }

    private void RefreshUserListUI()
    {
        string filter = UserFilterTextBox.Text.Trim();
        _filteredUsers.Clear();

        var query = string.IsNullOrWhiteSpace(filter)
            ? _allRoomUsers
            : _allRoomUsers.Where(u => u.Nickname.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var user in query)
        {
            _filteredUsers.Add(user);
        }

        UserListHeaderTextBlock.Text = $"Users ({_allRoomUsers.Count})";
    }

    private void UserFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshUserListUI();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        SendMessage();
    }

    private void MessageInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendMessage();
            e.Handled = true;
        }
    }

    private void SendMessage()
    {
        string text = MessageInputTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Support manual /msg command
        if (text.StartsWith("/msg ", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = text.Substring(5).Split(' ', 2);
            if (parts.Length == 2)
            {
                string targetNick = parts[0];
                string msg = parts[1];

                _ircClient.SendChatMessage(targetNick, msg);

                // Add to that user's conversation buffer
                var dmConvo = GetOrCreateConversation(targetNick, isChannel: false);
                dmConvo.IsDisconnected = false;
                string time = DateTime.Now.ToString("HH:mm");
                dmConvo.Buffer.AppendLine($"[{time}] <{_ircClient.Nickname ?? "You"}> {msg}");

                if (_activeConversation == dmConvo)
                {
                    UpdateActiveChatDisplay();
                }

                MessageInputTextBox.Clear();
                return;
            }
        }
        else if (text.StartsWith("/whois ", StringComparison.OrdinalIgnoreCase))
        {
            string targetNick = text.Substring(7).Trim();
            _ircClient.SendRaw($"WHOIS {targetNick}");
            MessageInputTextBox.Clear();
            return;
        }

        if (_activeConversation == null)
        {
            return;
        }

        // Send to active conversation target (either room or user)
        _ircClient.SendChatMessage(_activeConversation.Name, text);

        string sendTime = DateTime.Now.ToString("HH:mm");
        _activeConversation.Buffer.AppendLine($"[{sendTime}] <{_ircClient.Nickname ?? "You"}> {text}");
        UpdateActiveChatDisplay();

        MessageInputTextBox.Clear();
        MessageInputTextBox.Focus();
    }

    #region User List Interactions (Direct Chat & Multi-Selection)

    private void UsersListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source != null && source is not ListBoxItem)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is ListBoxItem clickedItem && clickedItem.IsSelected)
        {
            // Preserve the shift/ctrl multi-selection on right click
            e.Handled = true;

            if (UsersListBox.ContextMenu != null)
            {
                UsersListBox.ContextMenu.PlacementTarget = UsersListBox;
                UsersListBox.ContextMenu.IsOpen = true;
            }
        }
    }

    private void UsersListBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        int count = UsersListBox.SelectedItems.Count;
        if (UsersListBox.ContextMenu != null)
        {
            foreach (var item in UsersListBox.ContextMenu.Items)
            {
                if (item is MenuItem menuItem && menuItem.Name == "SendMultiMessageMenuItem")
                {
                    menuItem.Header = count > 1 
                        ? $"_Send Message to Selected ({count} users)..." 
                        : "_Send Message to Selected...";
                }
            }
        }
    }

    private void SendMultiMessageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var selectedNicks = UsersListBox.SelectedItems
            .OfType<IrcUser>()
            .Select(u => u.Nickname)
            .Where(n => !string.Equals(n, _ircClient.Nickname, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedNicks.Count == 0)
        {
            MessageBox.Show("Please select at least one user from the list.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new MultiUserMessageDialog(selectedNicks)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            string message = dialog.MessageText;
            string time = DateTime.Now.ToString("HH:mm");

            ConversationItem? firstConvo = null;
            var queueBatch = new List<(string target, string message)>();

            foreach (string nick in selectedNicks)
            {
                // Open individual one-to-one conversation in the left pane
                var convo = GetOrCreateConversation(nick, isChannel: false);
                convo.IsDisconnected = false;
                convo.Buffer.AppendLine($"[{time}] <{_ircClient.Nickname ?? "You"}> {message}");

                firstConvo ??= convo;
                queueBatch.Add((nick, message));
            }

            // Switch view to the first recipient
            if (firstConvo != null)
            {
                ConversationsListBox.SelectedItem = firstConvo;
            }

            UpdateActiveChatDisplay();

            // Send messages sequentially via the rate-limited FIFO queue
            _ircClient.EnqueueChatMessages(queueBatch);

            StatusTextBlock.Text = $"Enqueued message to {selectedNicks.Count} user(s) (dispatching sequentially)...";
        }
    }

    private void UsersListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (UsersListBox.SelectedItem is IrcUser selectedUser)
        {
            StartSeparateChatWithUser(selectedUser.Nickname);
        }
    }

    private void UserDirectMessage_Click(object sender, RoutedEventArgs e)
    {
        if (UsersListBox.SelectedItem is IrcUser selectedUser)
        {
            StartSeparateChatWithUser(selectedUser.Nickname);
        }
    }

    private void UserWhois_Click(object sender, RoutedEventArgs e)
    {
        if (UsersListBox.SelectedItem is IrcUser selectedUser)
        {
            _ircClient.SendRaw($"WHOIS {selectedUser.Nickname}");
        }
    }

    private void UserCopyNick_Click(object sender, RoutedEventArgs e)
    {
        var selectedNicks = UsersListBox.SelectedItems
            .OfType<IrcUser>()
            .Select(u => u.Nickname)
            .ToList();

        if (selectedNicks.Count > 0)
        {
            Clipboard.SetText(string.Join(", ", selectedNicks));
        }
    }

    public void StartSeparateChatWithUser(string targetNickname)
    {
        if (string.Equals(targetNickname, _ircClient.Nickname, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Cannot start a private chat with yourself.", "Direct Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Opens or activates conversation in left pane and switches chat view
        var dmConvo = GetOrCreateConversation(targetNickname, isChannel: false, makeActive: true);
        dmConvo.IsDisconnected = false;
        if (dmConvo.Buffer.Length == 0)
        {
            string time = DateTime.Now.ToString("HH:mm");
            dmConvo.Buffer.AppendLine($"[{time}] *** Started direct conversation with {targetNickname} ***");
            UpdateActiveChatDisplay();
        }
    }

    private void CloseConversation_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationsListBox.SelectedItem is ConversationItem selectedConvo)
        {
            if (selectedConvo.IsChannel)
            {
                MessageBox.Show("Cannot close the main channel conversation. Disconnect to leave.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _conversations.Remove(selectedConvo);

            // Switch to the first remaining conversation (usually channel)
            var next = _conversations.FirstOrDefault();
            if (next != null)
            {
                ConversationsListBox.SelectedItem = next;
            }
            else
            {
                _activeConversation = null;
                ChatHeaderTextBlock.Text = "No conversation selected";
                ChatMessagesTextBox.Clear();
            }
        }
    }

    #endregion

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _ircClient.Dispose();
    }
}
