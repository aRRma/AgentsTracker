using AgentsTracker.Gateway.Infrastructure.Audit;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>Выбор модели: алиасы семейств кнопками, полное имя — командой /model.</summary>
public sealed class ModelScreen(SessionStore store, IAuditLog audit) : ISettingsScreen
{
    private static readonly string[] Aliases = ["opus", "sonnet", "haiku", "fable"];

    public string Key => "model";

    public string? Apply(string argument, long userId)
    {
        var model = argument.Equals("reset", StringComparison.OrdinalIgnoreCase) ? null : argument;
        var previous = store.Model;
        store.SetModel(model);
        audit.Changed(store, userId, "model", previous, model);
        return $"Модель: {model ?? "по умолчанию"}";
    }

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(CancellationToken ct) =>
        Task.FromResult(Render());

    private (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        var current = store.EffectiveModel;

        var html = $"""
            🧠 <b>Модель</b>

            Сейчас: <b>{E(current ?? "по умолчанию — как настроен Claude Code")}</b>

            <i>Кнопки задают алиас последней модели семейства. Полное имя
            (например <code>claude-sonnet-5</code>) можно задать командой <code>/model claude-sonnet-5</code>.</i>
            """;

        var buttons = Aliases
            .Select(alias => Button($"{Marker(alias == current)} {alias}", $"model:{alias}"))
            .Chunk(2)
            .ToList();

        buttons.Add([Button($"{Marker(current is null)} по умолчанию", "model:reset")]);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }
}
