using Telegram.Bot.Types;

namespace AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;

/// <summary>
/// Обработчик нажатий на инлайн-кнопки. Все callback-и приходят одним потоком, поэтому
/// обработчики различают свои по префиксу callback_data.
/// </summary>
public interface ITelegramCallbackHandler
{
    bool CanHandle(string callbackData);

    Task HandleAsync(CallbackQuery query, CancellationToken ct);
}
