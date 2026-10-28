using System.Globalization;

namespace AgentsTracker.Channels.Telegram;

/// <summary>
/// Чат Telegram: числовой id из Bot API. В личном чате совпадает с id пользователя.
/// <paramref name="Thread"/> — тема лички: у каждой своя лента, и ответ должен уйти туда же,
/// откуда пришло сообщение. null — лента без темы.
/// </summary>
public sealed record TelegramChatId(long Value, int? Thread = null) : ChatId
{
    public override string Key => TelegramIds.Key(Value)
        + (Thread is { } thread ? "/" + thread.ToString(CultureInfo.InvariantCulture) : "");

    public override bool IsTopic => Thread is not null;

    /// <summary>«telegram:123» или «telegram:123/42». Ключи без темы — это и записи прошлых версий.</summary>
    public static bool TryParse(string key, out TelegramChatId? chat)
    {
        chat = null;
        var slash = key.IndexOf('/');
        if (!TelegramIds.TryParse(slash < 0 ? key : key[..slash], out var value)) return false;

        int? thread = null;
        if (slash >= 0)
        {
            if (!int.TryParse(key.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
            thread = parsed;
        }

        chat = new TelegramChatId(value, thread);
        return true;
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
