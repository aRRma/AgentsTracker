using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>Корневой экран: сводка и переходы к остальным.</summary>
public sealed class RootScreen(SessionStore store, ChatWorker worker) : ISettingsScreen
{
    public string Key => "root";

    public string? Apply(string argument, long userId) => null;

    public (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        var project = store.ProjectPath;
        var session = ActiveSession();

        // Стоимость на подписке — оценка CLI, а не счёт: кредиты шлюз не тратит.
        var spent = store.SpentToday();
        var today = store.DailyBudgetUsd is { } cap ? $"{spent.Money} из {cap.Money}" : spent.Money;

        var html = $"""
            ⚙️ <b>Настройки</b>

            📁 <b>{E(Path.GetFileName(project))}</b>
            <code>{E(project)}</code>
            🧠 Модель: <b>{E(store.EffectiveModel ?? "по умолчанию")}</b>
            🎚 Effort: <b>{E(store.Effort ?? "по умолчанию")}</b>
            🔐 Доступ: <b>{E(store.EffectivePermissionMode)}</b>
            🧵 Сессия: {(session is null ? "<i>новая</i>" : $"<b>{E(session.Title)}</b>")}
            📈 Сегодня (оценка): <b>{E(today)}</b>
            ⚙️ {(worker.IsBusy ? "выполняется" : "простаивает")}, в очереди: {worker.QueueLength}
            """;

        var keyboard = new InlineKeyboardMarkup(
        [
            [Button("📁 Репозиторий", "proj"), Button("🧠 Модель", "model")],
            [Button("🎚 Effort", "effort"), Button("🔐 Доступ", "mode")],
            [Button("🧵 Сессии", "sess"), Button("📊 Статистика", "usage")],
            [Button("✖️ Закрыть", "close")],
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
