using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>Расход: запуски, токены, стоимость — всего, по дням и по моделям.</summary>
public sealed class UsageScreen(SessionStore store, IAuditLog audit) : ISettingsScreen
{
    private const int MaxDaysShown = 5;

    public string Key => "usage";

    public string? Apply(string argument, long userId)
    {
        if (argument != "reset") return null;

        store.ResetUsage();
        audit.Changed(store, userId, "usage", "статистика", "обнулена");
        return "Статистика обнулена";
    }

    public (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        var usage = store.Snapshot().Usage;
        var total = usage.Total;

        var days = usage.ByDay
            .OrderByDescending(pair => pair.Key, StringComparer.Ordinal)
            .Take(MaxDaysShown)
            .Select(pair => $"· {pair.Key} — {pair.Value.Runs} зап. · {pair.Value.CostUsd.Money}");

        var models = usage.ByModel
            .OrderByDescending(pair => pair.Value.CostUsd)
            .Select(pair => $"· {E(pair.Key)} — {pair.Value.CostUsd.Money}");

        var since = usage.SinceUtc is { } from ? from.ToLocalTime().ToString("d MMMM, HH:mm") : "—";

        var html = $"""
            📊 <b>Использование</b>
            <i>с {E(since)}</i>

            Запусков: <b>{total.Runs}</b> · ходов: <b>{total.Turns}</b>
            Стоимость: <b>{total.CostUsd.Money}</b>
            Время в CLI: <b>{E(TimeSpan.FromMilliseconds(total.DurationMs).Elapsed)}</b>

            Токены: ввод {E(total.InputTokens.Tokens)} · вывод {E(total.OutputTokens.Tokens)}
            Кэш: чтение {E(total.CacheReadTokens.Tokens)} · запись {E(total.CacheWriteTokens.Tokens)}

            <b>По дням</b>
            {(days.Any() ? string.Join("\n", days) : "<i>пусто</i>")}

            <b>По моделям</b>
            {(models.Any() ? string.Join("\n", models) : "<i>пусто</i>")}
            """;

        return (html, new InlineKeyboardMarkup([[Button("♻️ Сбросить", "usage:reset")], [BackButton]]));
    }
}
