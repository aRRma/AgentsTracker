using AgentsTracker.Gateway.Infrastructure.Audit;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Сколько модели думать — уровни из <see cref="AgentCapabilities.Effort"/>. У агента без
/// этой настройки экран отвечает отказом, а кнопку к нему корневой экран не показывает.
/// </summary>
public sealed class EffortScreen(SessionStore store, IAgentBackend agent, IAuditLog audit) : ISettingsScreen
{
    public string Key => "effort";

    public string? Apply(string argument, long userId, long chatId)
    {
        if (agent.Capabilities.Effort is not { } setting) return $"{agent.DisplayName} не поддерживает уровень усилий";

        var reset = argument.Equals("reset", StringComparison.OrdinalIgnoreCase);
        var effort = reset ? null : setting.Resolve(argument);
        if (!reset && effort is null) return "Не знаю такой уровень";

        var previous = store.Effort;
        store.SetEffort(effort);
        audit.Changed(store, userId, "effort", previous, effort);
        return $"Effort: {effort ?? "по умолчанию"}";
    }

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(long userId, CancellationToken ct) =>
        Task.FromResult(Render());

    private (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        if (agent.Capabilities.Effort is not { } setting)
        {
            return ($"🎚 <b>Effort</b>\n\n<i>{E(agent.DisplayName)} не поддерживает уровень усилий.</i>",
                new InlineKeyboardMarkup([[BackButton]]));
        }

        var current = store.EffectiveEffort;

        var html = $"""
            🎚 <b>Effort</b> — сколько модели думать

            {string.Join("\n", setting.Values.Select(l => $"{Marker(l == current)} {E(setting.Describe(l))}"))}

            <i>Выше уровень — дольше и дороже ответ, но лучше на сложных задачах.
            «По умолчанию» отдаёт выбор самому агенту.</i>
            """;

        var buttons = setting.Selectable
            .Select(level => Button($"{Marker(level == current)} {level}", $"effort:{level}"))
            .Chunk(3)
            .ToList();

        buttons.Add([Button($"{Marker(current is null)} по умолчанию", "effort:reset")]);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }
}
