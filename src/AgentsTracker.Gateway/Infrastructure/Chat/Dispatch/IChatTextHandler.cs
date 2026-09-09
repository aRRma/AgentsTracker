namespace AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

/// <summary>
/// Обработчик обычного сообщения (не команды шлюза). Опрашиваются в порядке регистрации в DI:
/// первый, кто вернул true, забирает сообщение. Порядок принципиален — ожидающий свободного
/// ответа брокер подтверждений должен стоять раньше постановки в очередь.
/// Сообщение передаётся целиком: кроме текста в нём бывают вложения.
/// </summary>
public interface IChatTextHandler
{
    Task<bool> TryHandleAsync(IncomingMessage message, CancellationToken ct);
}
