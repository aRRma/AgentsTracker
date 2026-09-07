namespace AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

/// <summary>
/// Обработчик нажатий на кнопки. Все нажатия приходят одним потоком, поэтому обработчики
/// различают свои по префиксу данных кнопки.
/// </summary>
public interface IChatButtonHandler
{
    bool CanHandle(string data);

    Task HandleAsync(ButtonPress press, CancellationToken ct);
}
