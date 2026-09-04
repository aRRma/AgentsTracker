namespace AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;

/// <summary>
/// Обработчик обычного текста (не команды шлюза). Опрашиваются в порядке регистрации в DI:
/// первый, кто вернул true, забирает сообщение. Порядок принципиален — ожидающий свободного
/// ответа брокер подтверждений должен стоять раньше постановки в очередь.
/// </summary>
public interface ITelegramTextHandler
{
    Task<bool> TryHandleAsync(long chatId, long userId, string text, CancellationToken ct);
}
