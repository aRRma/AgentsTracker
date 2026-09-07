namespace AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

/// <summary>
/// Обработчик обычного текста (не команды шлюза). Опрашиваются в порядке регистрации в DI:
/// первый, кто вернул true, забирает сообщение. Порядок принципиален — ожидающий свободного
/// ответа брокер подтверждений должен стоять раньше постановки в очередь.
/// </summary>
public interface IChatTextHandler
{
    Task<bool> TryHandleAsync(ChatId chat, UserId user, string text, CancellationToken ct);
}
