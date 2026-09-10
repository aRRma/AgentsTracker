using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Расход тарифных окон и расход шлюза: запуски, ходы, токены — всего, по дням и по моделям.
/// Стоимость намеренно не показывается: на подписке она ни во что не превращается, а кредиты
/// шлюз не тратит — упереться можно только в окно лимита, его и показываем первым.
/// </summary>
public sealed class UsageScreen(SessionStore store, IAgentLimits limits, IAuditLog audit) : ISettingsScreen
{
    private const int MaxDaysShown = 5;

    public string Key => "usage";

    public string? Apply(string argument, UserId user, ChatId chat)
    {
        if (argument != "reset") return null;

        store.ResetUsage();
        audit.Changed(store, user, "usage", "статистика", "обнулена");
        return "Статистика обнулена";
    }

    public async Task<(string Html, Keyboard Keyboard)> RenderAsync(UserId user, CancellationToken ct)
    {
        var usage = store.Snapshot().Usage;
        var total = usage.Total;

        var plan = await limits.ViewAsync(store.EffectiveModel, ct);

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

            <b>Расход тарифа</b>
            {LimitBars.Render(plan, 1m)}

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

        return (html, new Keyboard([[Button("♻️ Сбросить", "usage:reset")], [BackButton]]));
    }
}
