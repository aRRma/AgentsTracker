using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Claude;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>Корневой экран: сводка и переходы к остальным.</summary>
public sealed class RootScreen(SessionStore store, ChatWorker worker, ClaudeLimits limits) : ISettingsScreen
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

        var html = $"""
            ⚙️ <b>Настройки</b>

            📁 <b>{E(Path.GetFileName(project))}</b>
            <code>{E(project)}</code>
            🧠 Модель: <b>{E(store.EffectiveModel ?? "по умолчанию")}</b>
            🎚 Effort: <b>{E(store.EffectiveEffort ?? "по умолчанию")}</b>
            🔐 Режим: <b>{E(store.EffectivePermissionMode)}</b>
            🧵 Сессия: {(session is null ? "<i>новая</i>" : $"<b>{E(session.Title)}</b>")}
            🚦 Осталось: <b>{E(plan.Length > 0 ? plan : "—")}</b>
            ⚙️ {(worker.IsBusy ? "выполняется" : "простаивает")}, в очереди: {worker.QueueLength}
            """;

        var keyboard = new InlineKeyboardMarkup(
        [
            [Button("📁 Репозиторий", "proj"), Button("🧠 Модель", "model")],
            [Button("🎚 Effort", "effort"), Button("🔐 Режим", "mode")],
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
