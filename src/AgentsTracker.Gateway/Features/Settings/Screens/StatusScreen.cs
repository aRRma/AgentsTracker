using System.Runtime.CompilerServices;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Статус: где работаем, что происходит и сколько осталось тарифа — шкалами. Шкалы
/// заполняются за несколько кадров (<see cref="LimitBars"/>): так остаток виден с одного
/// взгляда, а движение показывает, что шлюз жив. «Обновить» перерисовывает тем же способом.
/// </summary>
public sealed class StatusScreen(
    SessionStore store, IAgentBackend agent, ChatWorker worker, IAgentLimits limits, IAuditLog audit) : ISettingsScreen
{
    public string Key => "status";

    public string? Apply(string argument, long userId, long chatId)
    {
        switch (argument)
        {
            case "stop":
                var stopped = worker.Stop();
                audit.Write(AuditEvent.Now(AuditKinds.Gateway, "/stop", userId, chatId, store.ProjectPath, store.SessionId,
                    stopped ? "stopped" : "idle"));
                return stopped ? "Остановлено" : "Сейчас ничего не выполняется";

            case "refresh":
                return "Обновлено";

            default:
                return null;
        }
    }

    public async Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(long userId, CancellationToken ct) =>
        Render(await limits.ViewAsync(store.EffectiveModel, ct), 1.0);

    public async IAsyncEnumerable<(string Html, InlineKeyboardMarkup Keyboard)> RenderFramesAsync(
        long userId, [EnumeratorCancellation] CancellationToken ct)
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

    private (string Html, InlineKeyboardMarkup Keyboard) Render(LimitsView view, double progress)
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

            🚦 <b>Остаток тарифа</b>
            {LimitBars.Render(view, progress)}
            """;

        var keyboard = new InlineKeyboardMarkup(
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
