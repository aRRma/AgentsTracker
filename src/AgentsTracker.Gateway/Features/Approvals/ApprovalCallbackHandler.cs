using AgentsTracker.Gateway.Features.Settings;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot.Types;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Нажатия на карточках подтверждений: callback_data вида «hex-id:ключ». Единственный
/// чужой префикс в этом потоке — у меню настроек, его и исключаем.
/// </summary>
public sealed class ApprovalCallbackHandler(ApprovalBroker broker) : ITelegramCallbackHandler
{
    public bool CanHandle(string callbackData) =>
        !callbackData.StartsWith(SettingsMenuCoordinator.CallbackPrefix, StringComparison.Ordinal);

    public Task HandleAsync(CallbackQuery query, CancellationToken ct) => broker.HandleCallbackAsync(query, ct);
}
