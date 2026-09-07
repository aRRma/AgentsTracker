using AgentsTracker.Gateway.Features.Settings;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Нажатия на карточках подтверждений: callback_data вида «hex-id:ключ». Единственный
/// чужой префикс в этом потоке — у меню настроек, его и исключаем.
/// </summary>
public sealed class ApprovalCallbackHandler(ApprovalBroker broker) : IChatButtonHandler
{
    public bool CanHandle(string data) =>
        !data.StartsWith(SettingsMenuCoordinator.CallbackPrefix, StringComparison.Ordinal);

    public Task HandleAsync(ButtonPress press, CancellationToken ct) => broker.HandlePressAsync(press, ct);
}
