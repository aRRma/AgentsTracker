using AgentsTracker.Gateway.Infrastructure.Audit;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>Выбор модели: алиасы из <see cref="AgentCapabilities.Model"/> кнопками, полное имя — командой /model.</summary>
public sealed class ModelScreen(SessionStore store, IAgentBackend agent, IAuditLog audit) : ISettingsScreen
{
    public string Key => "model";

    public string? Apply(string argument, long userId, long chatId)
    {
        var reset = argument.Equals("reset", StringComparison.OrdinalIgnoreCase);
        var model = reset ? null : agent.Capabilities.Model.Resolve(argument);
        if (!reset && model is null) return "Не знаю такую модель";

        var previous = store.Model;
        store.SetModel(model);
        audit.Changed(store, userId, "model", previous, model);
        return $"Модель: {model ?? "по умолчанию"}";
    }

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(long userId, CancellationToken ct) =>
        Task.FromResult(Render());

    private (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        var current = store.EffectiveModel;

        var html = $"""
            🧠 <b>Модель</b>

            Сейчас: <b>{E(current ?? $"по умолчанию — как настроен {agent.DisplayName}")}</b>

            <i>Кнопки задают алиас последней модели семейства. Полное имя модели
            можно задать командой <code>/model &lt;имя&gt;</code>.</i>
            """;

        var buttons = agent.Capabilities.Model.Selectable
            .Select(alias => Button($"{Marker(alias == current)} {alias}", $"model:{alias}"))
            .Chunk(2)
            .ToList();

        buttons.Add([Button($"{Marker(current is null)} по умолчанию", "model:reset")]);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }
}
