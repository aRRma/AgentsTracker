using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>/new, /stop — управление текущим запуском и сессией. /status живёт в меню: это экран.</summary>
public sealed class ChatCommandHandler(
    ITelegramBotClient bot,
    ChatWorker worker,
    SessionStore store,
    IAuditLog audit) : ITelegramCommandHandler
{
    public IReadOnlyCollection<string> Commands { get; } = ["/new", "/stop"];

    public async Task HandleAsync(TelegramCommandContext context, CancellationToken ct)
    {
        var reply = context.Command == "/new" ? StartNew(context) : Stop(context);

        await bot.SendMessage(context.ChatId, reply, cancellationToken: ct);
    }

    private string StartNew(TelegramCommandContext context)
    {
        var previous = store.SessionId;
        store.SetSessionId(null);
        audit.Write(AuditEvent.Now(AuditKinds.Settings, "session: новая", context.UserId, context.ChatId, store.ProjectPath, previous));
        return "🆕 Начата новая сессия — прошлый контекст забыт.";
    }

    private string Stop(TelegramCommandContext context)
    {
        var stopped = worker.Stop();
        audit.Write(AuditEvent.Now(AuditKinds.Gateway, "/stop", context.UserId, context.ChatId, store.ProjectPath, store.SessionId,
            stopped ? "stopped" : "idle"));
        return stopped ? "🛑 Остановлено." : "Сейчас ничего не выполняется.";
    }
}
