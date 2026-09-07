using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Нажатия в меню настроек: callback_data с префиксом «cfg:».</summary>
public sealed class SettingsCallbackHandler(SettingsMenuCoordinator menu) : IChatButtonHandler
{
    public bool CanHandle(string data) =>
        data.StartsWith(SettingsMenuCoordinator.CallbackPrefix, StringComparison.Ordinal);

    public Task HandleAsync(ButtonPress press, CancellationToken ct) => menu.HandlePressAsync(press, ct);
}
