using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>Корневой экран: сводка и переходы к остальным.</summary>
public sealed class RootScreen(SessionStore store, IAgentBackend agent, ChatWorker worker, IAgentLimits limits) : ISettingsScreen
{
    public string Key => "root";

    public string? Apply(string argument, long userId, long chatId) => null;

    public async Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(long userId, CancellationToken ct)
    {
        var project = store.ProjectPath;
        var session = ActiveSession();

        // Остаток тарифа, а не потраченные доллары: на подписке платить не за что,
        // а упереться можно только в окно лимита.
        var plan = await limits.ShortSummaryAsync(store.EffectiveModel, ct);

        // Строку и кнопку effort показываем только агенту, который его понимает.
        var hasEffort = agent.Capabilities.Effort is not null;
        var effort = hasEffort ? $"\n🎚 Effort: <b>{E(store.EffectiveEffort ?? "по умолчанию")}</b>" : "";

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

        var keyboard = new InlineKeyboardMarkup(
        [
            [Button("📁 Репозиторий", "proj"), Button("🧠 Модель", "model")],
            hasEffort ? [Button("🎚 Effort", "effort"), Button("🔐 Режим", "mode")] : [Button("🔐 Режим", "mode")],
            [Button("🧵 Сессии", "sess"), Button("📊 Статистика", "usage")],
            [Button("🧩 Скиллы", "skills"), Button("✖️ Закрыть", "close")],
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
