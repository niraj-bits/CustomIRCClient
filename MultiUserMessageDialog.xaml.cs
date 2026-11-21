using System.Collections.Generic;
using System.Windows;

namespace MyIRCClient;

public partial class MultiUserMessageDialog : Window
{
    public string MessageText { get; private set; } = string.Empty;

    public MultiUserMessageDialog(IReadOnlyList<string> recipientNicks)
    {
        InitializeComponent();

        RecipientsHeaderTextBlock.Text = $"Send message to {recipientNicks.Count} selected user(s):";
        RecipientsListTextBlock.Text = string.Join(", ", recipientNicks);

        Loaded += (_, _) => MessageInputTextBox.Focus();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        string text = MessageInputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("Please enter a message to send.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            MessageInputTextBox.Focus();
            return;
        }

        MessageText = text;
        DialogResult = true;
        Close();
    }
}
