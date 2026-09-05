using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Уровень доступа агента к машине (<c>--permission-mode</c>). Из чата переключаются только
/// <see cref="PermissionModes.Selectable"/>: снять подтверждения полностью можно лишь конфигом.
/// </summary>
public sealed class ModeScreen(
    SessionStore store, ChatWorker worker, IAuditLog audit, IOptions<GatewayOptions> options) : ISettingsScreen
{
    public string Key => "mode";

    public string? Apply(string argument, long userId, long chatId)
    {
        var previous = store.EffectivePermissionMode;

        if (argument.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            store.SetPermissionMode(null);
            audit.Changed(store, userId, "mode", previous, options.Value.PermissionMode);
            return $"Доступ: {options.Value.PermissionMode} (из конфига)";
        }

        var mode = PermissionModes.Resolve(argument);
        if (mode is null || !PermissionModes.Selectable.Contains(mode, StringComparer.Ordinal))
            return "Этот уровень из чата не переключается";

        if (mode == previous) return $"Уже {mode}";

        store.SetPermissionMode(mode);
        audit.Changed(store, userId, "mode", previous, mode);
        return worker.IsBusy
            ? $"Доступ: {mode} — со следующего запуска"
            : $"Доступ: {mode}";
    }

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(long userId, CancellationToken ct) =>
        Task.FromResult(Render());

    private (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        var current = store.EffectivePermissionMode;

        var html = $"""
            🔐 <b>Доступ агента к машине</b>

            {string.Join("\n", PermissionModes.Selectable.Select(m => $"{Marker(m == current)} {E(PermissionModes.Describe(m))}"))}

            <i>Меняется со следующего запуска. Полностью снять подтверждения из чата нельзя —
            только правкой <code>appsettings.Local.json</code> на самой машине.</i>
            """;

        var buttons = PermissionModes.Selectable
            .Select(mode => Button($"{Marker(mode == current)} {mode}", $"mode:{mode}"))
            .Chunk(2)
            .ToList();

        buttons.Add([Button($"из конфига ({options.Value.PermissionMode})", "mode:reset")]);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }
}
