using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>/new, /stop — управление текущим запуском и сессией. /status живёт в меню: это экран.</summary>
public sealed class ChatCommandHandler(
    IChatChannel channel,
    ChatWorker worker,
    AttachmentInbox inbox,
    SessionStore store,
    IAuditLog audit) : IChatCommandHandler
{
    public IReadOnlyCollection<string> Commands { get; } = ["/new", "/stop"];

    public async Task HandleAsync(ChatCommandContext context, CancellationToken ct)
    {
        var reply = context.Command == "/new" ? StartNew(context) : Stop(context);

        await channel.SendAsync(context.Chat, new OutgoingMessage(reply, Rich: false), ct);
    }

    private string StartNew(ChatCommandContext context)
    {
        var previous = store.SessionId;
        store.SetSessionId(null);

        // Прошлый контекст забыт вместе с картинками, которые обсуждались в нём.
        inbox.ClearSession(context.Chat, store.ProjectPath);

        audit.Write(AuditEvent.Now(AuditKinds.Settings, "session: новая", context.User, context.Chat, store.ProjectPath, previous));
        return "🆕 Начата новая сессия — прошлый контекст забыт.";
    }

    private string Stop(ChatCommandContext context)
    {
        var stopped = worker.Stop();
        audit.Write(AuditEvent.Now(AuditKinds.Gateway, "/stop", context.User, context.Chat, store.ProjectPath, store.SessionId,
            stopped ? "stopped" : "idle"));
        return stopped ? "🛑 Остановлено." : "Сейчас ничего не выполняется.";
    }
}
