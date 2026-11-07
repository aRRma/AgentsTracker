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

        await channel.SayAsync(context.Chat, reply, ct);
    }

    private string StartNew(ChatCommandContext context)
    {
        var scope = store.For(context.Chat);
        var previous = scope.SessionId;
        scope.SetSessionId(null);

        // Прошлый контекст забыт вместе с картинками, которые обсуждались в нём.
        inbox.ClearSession(context.Chat, scope.ProjectPath);

        audit.Write(AuditEvent.Now(AuditKinds.Settings, "session: новая", context.User, context.Chat, scope.ProjectPath, previous));
        return "🆕 Начата новая сессия — прошлый контекст забыт.";
    }

    private string Stop(ChatCommandContext context) =>
        worker.StopBy(context.Chat, context.User) ? "🛑 Остановлено." : "Сейчас ничего не выполняется.";
}
