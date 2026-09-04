using System.Text;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace AgentsTracker.Gateway.Features.Audit;

/// <summary>/audit [n] — последние n записей журнала действий, по одной строке на запись.</summary>
public sealed class AuditCommandHandler(ITelegramBotClient bot, IAuditLog audit) : ITelegramCommandHandler
{
    private const int DefaultCount = 20;
    private const int MaxCount = 100;

    /// <summary>Бюджет на одну строку: длинные Summary в чате только мешают, полный текст — в файле.</summary>
    private const int LineBudget = 160;

    public IReadOnlyCollection<string> Commands { get; } = ["/audit"];

    public async Task HandleAsync(TelegramCommandContext context, CancellationToken ct)
    {
        var count = int.TryParse(context.Argument, out var n) && n > 0 ? Math.Min(n, MaxCount) : DefaultCount;
        var entries = audit.Tail(count);

        if (entries.Count == 0)
        {
            await bot.SendMessage(context.ChatId, "Журнал пуст.", cancellationToken: ct);
            return;
        }

        var text = new StringBuilder();
        text.Append("📜 <b>Последние ").Append(entries.Count).Append(" записей</b>\n");

        foreach (var entry in entries)
        {
            var line = $"{Line(entry)}";
            if (text.Length + line.Length + 1 > TelegramFormatter.MaxMessageLength) break;
            text.Append('\n').Append(line);
        }

        await bot.SendMessage(context.ChatId, text.ToString(), ParseMode.Html, cancellationToken: ct);
    }

    /// <summary>«12:41 approval allow · Bash(git status) · 3813…»</summary>
    private static string Line(AuditEvent e)
    {
        var parts = new List<string> { e.Kind };
        if (e.Outcome is { Length: > 0 }) parts.Add(e.Outcome);

        var who = e.UserId is { } user ? $" · {ShortUser(user)}" : "";
        var where = e.Project is { Length: > 0 } ? $" · {e.Project}" : "";
        var local = e.At.ToLocalTime();
        var when = local.Date == DateTimeOffset.Now.Date ? local.ToString("HH:mm") : local.ToString("dd.MM HH:mm");

        return $"<code>{when}</code> {TelegramFormatter.EscapeCapped(string.Join(' ', parts), 40)} · "
             + TelegramFormatter.EscapeCapped(e.Summary, LineBudget)
             + TelegramFormatter.Escape(where + who);
    }

    /// <summary>Хвост id пользователя: в личном чате достаточно, чтобы отличить одного от другого.</summary>
    private static string ShortUser(long userId)
    {
        var s = userId.ToString();
        return s.Length <= 4 ? s : "…" + s[^4..];
    }
}
