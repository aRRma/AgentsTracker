namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Меню настроек: одно сообщение, которое перерисовывают кнопки. Экраны — это
/// <see cref="ISettingsScreen"/>, координатор только открывает, применяет и перерисовывает.
/// </summary>
public sealed class SettingsMenuCoordinator(
    IChatChannel channel,
    IEnumerable<ISettingsScreen> screens,
    ILogger<SettingsMenuCoordinator> logger)
{
    public const string CallbackPrefix = SettingsKeyboard.CallbackPrefix;

    private readonly Dictionary<string, ISettingsScreen> _screens =
        screens.ToDictionary(s => s.Key, StringComparer.Ordinal);

    /// <summary>Экран по ключу; неизвестный ключ ведёт на корневой.</summary>
    public ISettingsScreen Screen(string key) => _screens.GetValueOrDefault(key) ?? _screens["root"];

    /// <summary>Показывает меню новым сообщением. screen — экран, с которого начать.</summary>
    public async Task OpenAsync(ChatId chat, UserId user, CancellationToken ct, string screen = "root")
    {
        var target = Screen(screen);
        target.Open(user);

        MessageRef? message = null;
        await foreach (var (html, keyboard) in target.RenderFramesAsync(user, ct))
        {
            if (message is null)
                message = await channel.SendAsync(chat, new OutgoingMessage(html, Keyboard: keyboard), ct);
            else
                await EditQuietlyAsync(message, html, keyboard, ct);
        }
    }

    public async Task HandlePressAsync(ButtonPress press, CancellationToken ct)
    {
        var data = press.Data[CallbackPrefix.Length..];
        var separator = data.IndexOf(':');
        var screen = separator < 0 ? data : data[..separator];
        var argument = separator < 0 ? "" : data[(separator + 1)..];

        if (screen == "close")
        {
            await AnswerAsync(press, null, ct);
            if (press.Message is { } closing) await DeleteQuietlyAsync(closing, ct);
            return;
        }

        var target = Screen(screen);

        // Применяем выбор до отрисовки: экран должен показать новое состояние. Нажатие
        // без аргумента — переход из корня, экран открывается с начала.
        string? toast = null;
        if (argument.Length > 0) toast = target.Apply(argument, press.User, press.Chat);
        else target.Open(press.User);

        await AnswerAsync(press, toast, ct);

        // Сообщение с кнопками канал мог не отдать (старое или недоступное): перерисовывать
        // нечего, но выбор уже применён.
        if (press.Message is not { } message) return;

        await foreach (var (html, keyboard) in target.RenderFramesAsync(press.User, ct))
            await EditQuietlyAsync(message, html, keyboard, ct);
    }

    /// <summary>
    /// Перерисовка «в никуда»: неудачная правка не должна ронять обработку нажатия. Ожидание
    /// по просьбе канала и «текст не изменился» канал разбирает сам, здесь только лог.
    /// </summary>
    private async Task EditQuietlyAsync(MessageRef message, string html, Keyboard keyboard, CancellationToken ct)
    {
        try
        {
            await channel.EditAsync(message, new OutgoingMessage(html, Keyboard: keyboard), ct);
        }
        catch (ChannelRequestException ex)
        {
            logger.LogDebug(ex, "Не удалось перерисовать меню");
        }
    }

    private async Task DeleteQuietlyAsync(MessageRef message, CancellationToken ct)
    {
        try
        {
            await channel.DeleteAsync(message, ct);
        }
        catch (ChannelRequestException ex)
        {
            logger.LogDebug(ex, "Не удалось закрыть меню");
        }
    }

    private async Task AnswerAsync(ButtonPress press, string? toast, CancellationToken ct)
    {
        try
        {
            await channel.AcknowledgeAsync(press, toast, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось ответить на нажатие в меню");
        }
    }
}
