using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot.Types;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Нажатия в меню настроек: callback_data с префиксом «cfg:».</summary>
public sealed class SettingsCallbackHandler(SettingsMenuCoordinator menu) : ITelegramCallbackHandler
{
    public bool CanHandle(string callbackData) =>
        callbackData.StartsWith(SettingsMenuCoordinator.CallbackPrefix, StringComparison.Ordinal);

    public Task HandleAsync(CallbackQuery query, CancellationToken ct) => menu.HandleCallbackAsync(query, ct);
}
