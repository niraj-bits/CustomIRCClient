using System.Windows;
using MyIRCClient.Network;

namespace MyIRCClient;

public partial class ConnectDialog : Window
{
    public string Server { get; private set; } = string.Empty;
    public int Port { get; private set; } = 6697;
    public bool UseSsl { get; private set; } = true;
    public string Room { get; private set; } = string.Empty;
    public string Nickname { get; private set; } = string.Empty;

    public ConnectDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => ServerTextBox.Focus();
    }

    private void SslCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (PortTextBox == null) return;

        bool isSsl = SslCheckBox.IsChecked == true;
        // Auto-switch between standard ports if current port is 6667 or 6697
        if (isSsl && PortTextBox.Text == "6667")
        {
            PortTextBox.Text = "6697";
        }
        else if (!isSsl && PortTextBox.Text == "6697")
        {
            PortTextBox.Text = "6667";
        }
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        string server = ServerTextBox.Text.Trim();
        string portText = PortTextBox.Text.Trim();
        string room = RoomTextBox.Text.Trim();
        string nick = NicknameTextBox.Text.Trim();
        bool useSsl = SslCheckBox.IsChecked == true;

        if (string.IsNullOrWhiteSpace(server))
        {
            MessageBox.Show("Please enter a valid server name or IP address.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            ServerTextBox.Focus();
            return;
        }

        if (!int.TryParse(portText, out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Please enter a valid port number (1 - 65535).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            PortTextBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(room))
        {
            MessageBox.Show("Please enter a room name (e.g. #allindiachat.com).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            RoomTextBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(nick))
        {
            MessageBox.Show("Please enter a nickname.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            NicknameTextBox.Focus();
            return;
        }

        if (!room.StartsWith('#') && !room.StartsWith('&'))
        {
            room = "#" + room;
        }

        Server = server;
        Port = port;
        UseSsl = useSsl;
        Room = room;
        Nickname = nick;

        DialogResult = true;
        Close();
    }
}
