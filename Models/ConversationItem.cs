using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace MyIRCClient.Models;

public class ConversationItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private bool _isChannel;
    private bool _hasUnread;
    private bool _isDisconnected;
    private int _unreadCount;

    public string Name
    {
        get => _name;
        set 
        { 
            _name = value; 
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public bool IsChannel
    {
        get => _isChannel;
        set 
        { 
            _isChannel = value; 
            OnPropertyChanged();
            OnPropertyChanged(nameof(Icon));
        }
    }

    public bool HasUnread
    {
        get => _hasUnread;
        set
        {
            if (_hasUnread != value)
            {
                _hasUnread = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(TextColor));
                OnPropertyChanged(nameof(FontWeight));
                OnPropertyChanged(nameof(BadgeVisibility));
            }
        }
    }

    public bool IsDisconnected
    {
        get => _isDisconnected;
        set
        {
            if (_isDisconnected != value)
            {
                _isDisconnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(TextColor));
                OnPropertyChanged(nameof(FontWeight));
            }
        }
    }

    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            _unreadCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(BadgeText));
        }
    }

    public StringBuilder Buffer { get; } = new();

    public string Icon => IsChannel ? "#" : "@";

    public string DisplayName => IsDisconnected ? $"{Name} (disconnected)" : Name;

    public string BadgeText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

    public string BadgeVisibility => HasUnread && UnreadCount > 0 ? "Visible" : "Collapsed";

    // Text color priority:
    // 1. Red (#F38BA8) when the user has disconnected
    // 2. Peach/Orange (#FAB387) when there are unread messages
    // 3. Normal clean slate (#CDD6F4)
    public string TextColor
    {
        get
        {
            if (IsDisconnected) return "#F38BA8"; // Red
            if (HasUnread) return "#FAB387";      // Peach / Orange for unread
            return "#CDD6F4";                     // Normal slate
        }
    }

    // Bold when unread or disconnected for prominent visual cue
    public string FontWeight => (HasUnread || IsDisconnected) ? "Bold" : "Normal";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}
