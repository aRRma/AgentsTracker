using System.Globalization;

namespace AgentsTracker.Channels.Telegram;

/// <summary>Чат Telegram: числовой id из Bot API. В личном чате совпадает с id пользователя.</summary>
public sealed record TelegramChatId(long Value) : ChatId
{
    public override string Key => TelegramIds.Key(Value);

    public static bool TryParse(string key, out TelegramChatId? chat)
    {
        chat = TelegramIds.TryParse(key, out var value) ? new TelegramChatId(value) : null;
        return chat is not null;
    }
}

/// <summary>Пользователь Telegram: числовой id из Bot API.</summary>
public sealed record TelegramUserId(long Value) : UserId
{
    public override string Key => TelegramIds.Key(Value);
}

/// <summary>Строковая форма адресов: «telegram:123456789». Одна на чат и пользователя — в личке это одно число.</summary>
internal static class TelegramIds
{
    private const string Prefix = TelegramChannel.ChannelId + ":";

    public static string Key(long value) => Prefix + value.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string key, out long value)
    {
        value = 0;
        return key.StartsWith(Prefix, StringComparison.Ordinal)
            && long.TryParse(key.AsSpan(Prefix.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
