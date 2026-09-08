using System.Text;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Audit;

/// <summary>/audit [n] — последние n записей журнала действий, по одной строке на запись.</summary>
public sealed class AuditCommandHandler(IChatChannel channel, IAuditLog audit) : IChatCommandHandler
{
    private const int DefaultCount = 20;
    private const int MaxCount = 100;

    /// <summary>Бюджет на одну строку: длинные Summary в чате только мешают, полный текст — в файле.</summary>
    private const int LineBudget = 160;

    /// <summary>Запас под заголовок и строку «…и ещё N», которые дописываются после набора списка.</summary>
    private const int HeaderBudget = 120;

    public IReadOnlyCollection<string> Commands { get; } = ["/audit"];

    public async Task HandleAsync(ChatCommandContext context, CancellationToken ct)
    {
        var count = int.TryParse(context.Argument, out var n) && n > 0 ? Math.Min(n, MaxCount) : DefaultCount;
        var entries = audit.Tail(count);

        if (entries.Count == 0)
        {
            await channel.SendAsync(context.Chat, new OutgoingMessage("Журнал пуст.", Rich: false), ct);
            return;
        }

        // Сначала набираем строки по бюджету, и только потом пишем заголовок: иначе он обещал бы
        // столько записей, сколько нашлось, а показывалось бы столько, сколько влезло.
        var body = new StringBuilder();
        var shown = 0;

        // Идём от свежих к старым: обрезать нужно самое старое, а не последнее.
        foreach (var entry in entries.Reverse())
        {
            var line = Line(entry);
            if (HeaderBudget + body.Length + line.Length + 1 > channel.Limits.MessageLength) break;

            body.Insert(0, '\n').Insert(1, line);
            shown++;
        }

        var tail = shown < entries.Count ? $"\n<i>…и ещё {entries.Count - shown} — смотрите файл журнала.</i>" : "";
        // Перевод строки после заголовка не нужен: каждая строка списка уже начинается с него.
        var text = $"📜 <b>Последние {shown} записей</b>{body}{tail}";

        await channel.SendAsync(context.Chat, new OutgoingMessage(text), ct);
    }

    /// <summary>«12:41 approval allow · Bash(git status) · 3813…»</summary>
    private static string Line(AuditEvent e)
    {
        var parts = new List<string> { e.Kind };
        if (e.Outcome is { Length: > 0 }) parts.Add(e.Outcome);

        var who = e.UserKey is { Length: > 0 } user ? $" · {ShortUser(user)}" : "";
        var where = e.Project is { Length: > 0 } ? $" · {e.Project}" : "";
        var local = e.At.ToLocalTime();
        var when = local.Date == DateTimeOffset.Now.Date ? local.ToString("HH:mm") : local.ToString("dd.MM HH:mm");

        return $"<code>{when}</code> {ChatHtml.EscapeCapped(string.Join(' ', parts), 40)} · "
             + ChatHtml.EscapeCapped(e.Summary, LineBudget)
             + ChatHtml.Escape(where + who);
    }

    /// <summary>Хвост адреса пользователя: в личном чате достаточно, чтобы отличить одного от другого.</summary>
    private static string ShortUser(string userKey)
    {
        var colon = userKey.LastIndexOf(':');
        var id = colon < 0 ? userKey : userKey[(colon + 1)..];
        return id.Length <= 4 ? id : "…" + id[^4..];
    }
}
