using AgentsTracker.Gateway.Infrastructure.Audit;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>Сколько модели думать — уровень для <c>--effort</c>.</summary>
public sealed class EffortScreen(SessionStore store, IAuditLog audit) : ISettingsScreen
{
    public string Key => "effort";

    public string? Apply(string argument, long userId, long chatId)
    {
        var reset = argument.Equals("reset", StringComparison.OrdinalIgnoreCase);
        var effort = reset ? null : EffortLevels.Resolve(argument);
        if (!reset && effort is null) return "Не знаю такой уровень";

        var previous = store.Effort;
        store.SetEffort(effort);
        audit.Changed(store, userId, "effort", previous, effort);
        return $"Effort: {effort ?? "по умолчанию"}";
    }

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(CancellationToken ct) =>
        Task.FromResult(Render());

    private (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        var current = store.EffectiveEffort;

        var html = $"""
            🎚 <b>Effort</b> — сколько модели думать

            {string.Join("\n", EffortLevels.All.Select(l => $"{Marker(l == current)} {E(EffortLevels.Describe(l))}"))}

            <i>Выше уровень — дольше и дороже ответ, но лучше на сложных задачах.
            «По умолчанию» отдаёт выбор самому Claude Code.</i>
            """;

        var buttons = EffortLevels.All
            .Select(level => Button($"{Marker(level == current)} {level}", $"effort:{level}"))
            .Chunk(3)
            .ToList();

        buttons.Add([Button($"{Marker(current is null)} по умолчанию", "effort:reset")]);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }
}
