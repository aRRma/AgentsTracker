using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Claude;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Остаток тарифных окон и расход шлюза: запуски, ходы, токены — всего, по дням и по моделям.
/// Стоимость намеренно не показывается: на подписке она ни во что не превращается, а кредиты
/// шлюз не тратит — упереться можно только в окно лимита, его и показываем первым.
/// </summary>
public sealed class UsageScreen(SessionStore store, ClaudeLimits limits, IAuditLog audit) : ISettingsScreen
{
    private const int MaxDaysShown = 5;

    public string Key => "usage";

    public string? Apply(string argument, long userId, long chatId)
    {
        if (argument != "reset") return null;

        store.ResetUsage();
        audit.Changed(store, userId, "usage", "статистика", "обнулена");
        return "Статистика обнулена";
    }

    public async Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(long userId, CancellationToken ct)
    {
        var usage = store.Snapshot().Usage;
        var total = usage.Total;

        var plan = await limits.RemainingLinesAsync(store.EffectiveModel, ct);

        var days = usage.ByDay
            .OrderByDescending(pair => pair.Key, StringComparer.Ordinal)
            .Take(MaxDaysShown)
            .Select(pair => $"· {pair.Key} — {pair.Value.Runs} зап. · {pair.Value.Turns} х");

        var models = usage.ByModel
            .OrderByDescending(pair => pair.Value.InputTokens + pair.Value.OutputTokens)
            .Select(pair => $"· {E(pair.Key)} — {pair.Value.Turns} х · "
                          + $"{E((pair.Value.InputTokens + pair.Value.OutputTokens).Tokens)} токенов");

        var since = usage.SinceUtc is { } from ? from.ToLocalTime().ToString("d MMMM, HH:mm") : "—";

        var html = $"""
            📊 <b>Использование</b>

            <b>Остаток тарифа</b>
            {(plan.Count > 0 ? E(string.Join(Environment.NewLine, plan)) : "<i>окон нет</i>")}

            <i>Расход шлюза с {E(since)}</i>
            Запусков: <b>{total.Runs}</b> · ходов: <b>{total.Turns}</b>
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
