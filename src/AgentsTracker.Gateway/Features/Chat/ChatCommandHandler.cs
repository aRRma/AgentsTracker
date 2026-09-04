using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Claude;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>/new, /stop, /status — управление текущим запуском и сессией.</summary>
public sealed class ChatCommandHandler(
    ITelegramBotClient bot,
    ChatWorker worker,
    SessionStore store,
    ClaudeLimits limits,
    IAuditLog audit) : ITelegramCommandHandler
{
    public IReadOnlyCollection<string> Commands { get; } = ["/new", "/stop", "/status"];

    public async Task HandleAsync(TelegramCommandContext context, CancellationToken ct)
    {
        var reply = context.Command switch
        {
            "/new" => StartNew(context),
            "/stop" => Stop(context),
            _ => await BuildStatusAsync(ct),
        };

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

    private async Task<string> BuildStatusAsync(CancellationToken ct)
    {
        var state = store.Snapshot();

        // Остаток тарифа, а не потраченные доллары: подписку деньгами не мерить,
        // а запуск упирается именно в окно лимита.
        var plan = await limits.ShortSummaryAsync(store.EffectiveModel, ct);

        return $"""
            📁 {store.ProjectPath}
            🧠 {store.EffectiveModel ?? "модель по умолчанию"}
            🎚 Effort: {store.EffectiveEffort ?? "по умолчанию"}{(state.Effort is null ? " (из конфига)" : "")}
            🔐 Доступ: {store.EffectivePermissionMode}{(state.PermissionMode is null ? " (из конфига)" : "")}
            🧵 Сессия: {store.SessionId ?? "новая (ещё не создана)"}
            🚦 Осталось: {(plan.Length > 0 ? plan : "—")}
            ⚙️ {(worker.IsBusy ? "выполняется" : "простаивает")}, в очереди: {worker.QueueLength}
            🕔 Последняя активность: {state.LastActivityUtc?.ToLocalTime().ToString("g") ?? "—"}
            ♾ Правил «всегда»: {state.AlwaysAllow.Count}
            """;
    }
}
