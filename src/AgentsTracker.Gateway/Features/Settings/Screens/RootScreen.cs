using AgentsTracker.Gateway.Features.Chat;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Корневой экран: сводка и переходы к остальным. Кнопки — по частоте использования:
/// сначала то, что нужно в каждой сессии (статус, сессии), потом настройки агента и
/// скиллы, в конце проект и расход — их трогают раз в день.
/// </summary>
public sealed class RootScreen(SessionStore store, IAgentBackend agent, ChatWorker worker, IAgentLimits limits) : ISettingsScreen
{
    public string Key => "root";

    public string? Apply(string argument, UserId user, ChatId chat) => null;

    public async Task<(string Html, Keyboard Keyboard)> RenderAsync(UserId user, CancellationToken ct)
    {
        var project = store.ProjectPath;
        var session = ActiveSession();

        // Остаток тарифа, а не потраченные доллары: на подписке платить не за что,
        // а упереться можно только в окно лимита.
        var plan = await limits.ShortSummaryAsync(store.EffectiveModel, ct);

        // Строку effort показываем только агенту, который его понимает.
        var effort = agent.Capabilities.Effort is not null
            ? $"\n🎚 Effort: <b>{E(store.EffectiveEffort ?? "по умолчанию")}</b>"
            : "";

        var html = $"""
            ⚙️ <b>Настройки</b> — {E(agent.DisplayName)}

            📁 <b>{E(Path.GetFileName(project))}</b>
            <code>{E(project)}</code>
            🧠 Модель: <b>{E(store.EffectiveModel ?? "по умолчанию")}</b>{effort}
            🔐 Режим: <b>{E(store.EffectivePermissionMode)}</b>
            🧵 Сессия: {(session is null ? "<i>новая</i>" : $"<b>{E(session.Title)}</b>")}
            🚦 Осталось: <b>{E(plan.Length > 0 ? plan : "—")}</b>
            ⚙️ {(worker.IsBusy ? "выполняется" : "простаивает")}, в очереди: {worker.QueueLength}
            """;

        var keyboard = new Keyboard(
        [
            [Button("📟 Статус", "status"), Button("🧵 Сессии", "sess")],
            [Button("🤖 Агент", "agent")],
            [Button("🧩 Скиллы", "skills"), Button("📁 Проект", "proj")],
            [Button("📊 Статистика", "usage"), Button("✖️ Закрыть", "close")],
        ]);

        return (html, keyboard);
    }

    private SessionRecord? ActiveSession()
    {
        var active = store.SessionId;
        if (active is null) return null;

        return store.SessionsFor(store.ProjectPath)
            .FirstOrDefault(s => string.Equals(s.Id, active, StringComparison.Ordinal));
    }
}
