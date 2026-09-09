using System.Runtime.CompilerServices;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Статус: где работаем, что происходит и сколько тарифа уже израсходовано — шкалами. Шкалы
/// заполняются за несколько кадров (<see cref="LimitBars"/>): так расход виден с одного
/// взгляда, а движение показывает, что шлюз жив. «Обновить» перерисовывает тем же способом.
/// </summary>
public sealed class StatusScreen(
    SessionStore store, IAgentBackend agent, ChatWorker worker, IAgentLimits limits, IAuditLog audit) : ISettingsScreen
{
    public string Key => "status";

    public string? Apply(string argument, UserId user, ChatId chat)
    {
        switch (argument)
        {
            case "stop":
                var stopped = worker.Stop();
                audit.Write(AuditEvent.Now(AuditKinds.Gateway, "/stop", user, chat, store.ProjectPath, store.SessionId,
                    stopped ? "stopped" : "idle"));
                return stopped ? "Остановлено" : "Сейчас ничего не выполняется";

            case "refresh":
                return "Обновлено";

            default:
                return null;
        }
    }

    public async Task<(string Html, Keyboard Keyboard)> RenderAsync(UserId user, CancellationToken ct) =>
        Render(await limits.ViewAsync(store.EffectiveModel, ct), 1.0);

    public async IAsyncEnumerable<(string Html, Keyboard Keyboard)> RenderFramesAsync(
        UserId user, [EnumeratorCancellation] CancellationToken ct)
    {
        var view = await limits.ViewAsync(store.EffectiveModel, ct);

        // Без окон анимировать нечего — один кадр, иначе сообщение дёргалось бы впустую.
        var frames = view.Windows.Count == 0 ? 1 : LimitBars.Frames;

        for (var frame = 0; frame < frames; frame++)
        {
            if (frame > 0) await Task.Delay(LimitBars.FrameDelay, ct);
            yield return Render(view, frames == 1 ? 1.0 : LimitBars.Progress(frame));
        }
    }

    private (string Html, Keyboard Keyboard) Render(LimitsView view, double progress)
    {
        var state = store.Snapshot();
        var project = store.ProjectPath;
        var session = store.SessionId is { } id
            ? store.SessionsFor(project).FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal))
            : null;

        var busy = worker.IsBusy;

        var effort = agent.Capabilities.Effort is null
            ? ""
            : $" · 🎚 {E(store.EffectiveEffort ?? "по умолчанию")}";

        var html = $"""
            📟 <b>Статус</b> — {E(agent.DisplayName)}

            {(busy ? "🟢 <b>Выполняется</b>" : "⚪ <b>Простаивает</b>")}, в очереди: {worker.QueueLength}
            🕔 Последняя активность: {E(state.LastActivityUtc?.Ago ?? "—")}

            📁 <b>{E(Path.GetFileName(project))}</b>
            <code>{E(project)}</code>
            🧵 Сессия: {(session is null ? "<i>новая</i>" : $"<b>{E(session.Title)}</b> · {session.Turns} х")}
            🧠 {E(store.EffectiveModel ?? "модель по умолчанию")}{effort} · 🔐 {E(store.EffectivePermissionMode)}
            ♾ Правил «всегда» в проекте: {store.AlwaysAllowRules().Count}

            🚦 <b>Расход тарифа</b>
            {LimitBars.Render(view, progress)}
            """;

        var keyboard = new Keyboard(
        [
            busy
                ? [Button("🔄 Обновить", "status:refresh"), Button("🛑 Остановить", "status:stop")]
                : [Button("🔄 Обновить", "status:refresh")],
            [Button("🧵 Сессии", "sess"), Button("🤖 Агент", "agent")],
            [BackButton],
        ]);

        return (html, keyboard);
    }
}
