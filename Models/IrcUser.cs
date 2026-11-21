using System;

namespace MyIRCClient.Models;

public class IrcUser : IComparable<IrcUser>
{
    public string Nickname { get; set; }
    public string Prefix { get; set; }
    public bool IsOp => Prefix.Contains('@') || Prefix.Contains('~') || Prefix.Contains('&');
    public bool IsVoiced => Prefix.Contains('+') || Prefix.Contains('%');

    public string DisplayName => string.IsNullOrEmpty(Prefix) ? Nickname : $"{Prefix}{Nickname}";

    public IrcUser(string rawNick)
    {
        if (string.IsNullOrWhiteSpace(rawNick))
        {
            Nickname = string.Empty;
            Prefix = string.Empty;
            return;
        }

        char first = rawNick[0];
        if (first == '@' || first == '+' || first == '%' || first == '~' || first == '&' || first == '!')
        {
            Prefix = first.ToString();
            Nickname = rawNick.Substring(1);
        }
        else
        {
            Prefix = string.Empty;
            Nickname = rawNick;
        }
    }

    public int CompareTo(IrcUser? other)
    {
        if (other == null) return 1;

        // Rank order: Ops (~, &, @) -> Half-ops (%) -> Voiced (+) -> Regular
        int thisRank = GetRank(this);
        int otherRank = GetRank(other);

        if (thisRank != otherRank)
        {
            return otherRank.CompareTo(thisRank); // higher rank first
        }

        return string.Compare(Nickname, other.Nickname, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetRank(IrcUser user)
    {
        if (user.Prefix.Contains('~') || user.Prefix.Contains('&')) return 4;
        if (user.Prefix.Contains('@')) return 3;
        if (user.Prefix.Contains('%')) return 2;
        if (user.Prefix.Contains('+')) return 1;
        return 0;
    }

    public override string ToString() => DisplayName;
}
