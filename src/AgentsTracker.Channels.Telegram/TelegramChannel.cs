using System.Globalization;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentsTracker.Channels.Telegram;

/// <summary>
/// Telegram за контрактом <see cref="IChatChannel"/>: long polling, инлайн-кнопки, HTML.
/// Здесь же всё, что знает только Telegram: лимиты, retry_after, «message is not modified»,
/// отказ разметки — наружу уходит <see cref="ChannelRequestException"/> или ничего.
/// </summary>
public sealed class TelegramChannel(
    Lazy<ITelegramBotClient> bot,
    IOptions<TelegramOptions> options,
    ILogger<TelegramChannel> logger) : IChatChannel
{
    public const string ChannelId = "telegram";

    /// <summary>Сообщение до 4096 символов, подпись кнопки до 64 символов, callback_data до 64 байт.</summary>
    private static readonly ChannelLimits TelegramLimits = new(MessageLength: 3800, ButtonLabelLength: 64, ButtonDataBytes: 64);

    /// <summary>
    /// Кадры шкал идут чаще, чем Telegram позволяет править сообщение. Подождав не дольше
    /// этого, повторяем один раз — иначе шкала застынет на промежуточном кадре.
    /// </summary>
    private static readonly TimeSpan EditRetryCeiling = TimeSpan.FromSeconds(5);

    private readonly TelegramOptions _options = options.Value;

    /// <summary>
    /// Клиент создаётся при первом обращении: его конструктор бросает
    /// <see cref="ArgumentException"/> на пустом токене, а канал хост создаёт раньше, чем
    /// печатает ошибки настроек — вместо «BotToken не задан» вышел бы стектрейс.
    /// </summary>
    private ITelegramBotClient Bot => bot.Value;

    public string Id => ChannelId;

    public string DisplayName => "Telegram";

    public ChannelLimits Limits => TelegramLimits;

    public IReadOnlyCollection<UserId> AllowedUsers { get; } =
        [.. options.Value.AllowedUserIds.Distinct().Select(id => new TelegramUserId(id))];

    // В личном чате id чата равен id пользователя.
    public ChatId? DirectChat(UserId user) => user is TelegramUserId { Value: var id } ? new TelegramChatId(id) : null;

    public ChatId? ParseChat(string key) => TelegramChatId.TryParse(key, out var chat) ? chat : null;

    public IReadOnlyList<string> Validate() => _options.Validate();

    public async Task<string> ConnectAsync(CancellationToken ct)
    {
        try
        {
            var me = await Bot.GetMe(ct);
            return "@" + me.Username;
        }
        catch (RequestException ex)
        {
            throw Translate(ex);
        }
    }

    // Эмодзи уходит в описание: иконок у команд Bot API нет, а имя — только латиница.
    public async Task PublishCommandsAsync(IReadOnlyList<ChatCommand> commands, CancellationToken ct)
    {
        try
        {
            await Bot.SetMyCommands(
                commands.Select(c => new BotCommand { Command = c.Name, Description = c.Description }),
                cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Не удалось опубликовать список команд");
        }
    }

    public Task ListenAsync(IChatInbound sink, CancellationToken ct)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
            DropPendingUpdates = true,
        };

        return Bot.ReceiveAsync(
            (_, update, token) => HandleUpdateAsync(sink, update, token),
            (_, exception, _) =>
            {
                logger.LogError(exception, "Ошибка Telegram polling");
                return Task.CompletedTask;
            },
            receiverOptions,
            ct);
    }

    private async Task HandleUpdateAsync(IChatInbound sink, Update update, CancellationToken ct)
    {
        try
        {
            switch (update)
            {
                case { CallbackQuery: { } callback }:
                    await sink.OnButtonAsync(ToPress(callback), ct);
                    break;

                case { Message: { Text: { Length: > 0 } text, From: { } from } message }:
                    await sink.OnMessageAsync(
                        new IncomingMessage(new TelegramChatId(message.Chat.Id), new TelegramUserId(from.Id), text.Trim(), Kind(message.Chat)),
                        ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ошибка обработки обновления");
        }
    }

    /// <summary>
    /// У callback-а сообщения может не быть (старое или недоступно боту), и тогда тип чата
    /// неизвестен. Отдаём <see cref="ChatKind.Unknown"/>, а не «личный»: иначе кнопка
    /// из группы прошла бы в обход проверки.
    /// </summary>
    private static ButtonPress ToPress(CallbackQuery callback)
    {
        var chat = callback.Message?.Chat;
        var chatId = new TelegramChatId(chat?.Id ?? callback.From.Id);
        var message = callback.Message is { } m ? new MessageRef(chatId, MessageIdOf(m.MessageId)) : null;

        return new ButtonPress(callback.Id, chatId, new TelegramUserId(callback.From.Id), callback.Data ?? "", message, Kind(chat));
    }

    private static ChatKind Kind(Chat? chat) => chat switch
    {
        null => ChatKind.Unknown,
        { Type: ChatType.Private } => ChatKind.Direct,
        _ => ChatKind.Group,
    };

    public async Task<MessageRef> SendAsync(ChatId chat, OutgoingMessage message, CancellationToken ct)
    {
        var id = ChatIdOf(chat);
        var markup = Markup(message.Keyboard);

        try
        {
            var sent = await Bot.SendMessage(id, message.Text, ParseMode(message), replyMarkup: markup, cancellationToken: ct);
            return new MessageRef(chat, MessageIdOf(sent.MessageId));
        }
        catch (ApiRequestException ex) when (message.Rich && RetryWithoutMarkup(ex))
        {
            // Разметка могла не пережить конвертацию — лучше отправить как есть, чем ничего.
            logger.LogWarning(ex, "Telegram отверг сообщение с разметкой, отправляю без неё");
        }
        catch (RequestException ex)
        {
            // Остальные отказы уходят только как ChannelRequestException: иначе хост
            // не узнает ни про retry_after у 429, ни про «писать первым некуда».
            throw Translate(ex);
        }

        try
        {
            var sent = await Bot.SendMessage(id, ChatHtml.StripTags(message.Text), replyMarkup: markup, cancellationToken: ct);
            return new MessageRef(chat, MessageIdOf(sent.MessageId));
        }
        catch (RequestException ex)
        {
            throw Translate(ex);
        }
    }

    public async Task<MessageRef> SendFileAsync(ChatId chat, string fileName, string text, CancellationToken ct)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        try
        {
            var sent = await Bot.SendDocument(ChatIdOf(chat), InputFile.FromStream(stream, fileName), cancellationToken: ct);
            return new MessageRef(chat, MessageIdOf(sent.MessageId));
        }
        catch (RequestException ex)
        {
            throw Translate(ex);
        }
    }

    public async Task EditAsync(MessageRef message, OutgoingMessage content, CancellationToken ct)
    {
        try
        {
            await EditOnceAsync(message, content, ct);
            return;
        }
        catch (ApiRequestException ex) when (ex.Parameters?.RetryAfter is { } seconds && TimeSpan.FromSeconds(seconds) <= EditRetryCeiling)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        }
        catch (RequestException ex)
        {
            throw Translate(ex);
        }

        try
        {
            await EditOnceAsync(message, content, ct);
        }
        catch (RequestException ex)
        {
            throw Translate(ex);
        }
    }

    private async Task EditOnceAsync(MessageRef message, OutgoingMessage content, CancellationToken ct)
    {
        try
        {
            await Bot.EditMessageText(
                ChatIdOf(message.Chat), MessageId(message), content.Text, ParseMode(content),
                replyMarkup: Markup(content.Keyboard), cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Штатный исход: пользователь нажал ту же кнопку, текст и кнопки не изменились.
        }
    }

    public async Task DeleteAsync(MessageRef message, CancellationToken ct)
    {
        try
        {
            await Bot.DeleteMessage(ChatIdOf(message.Chat), MessageId(message), ct);
        }
        catch (RequestException ex)
        {
            throw Translate(ex);
        }
    }

    public async Task AcknowledgeAsync(ButtonPress press, string? toast, CancellationToken ct)
    {
        try
        {
            await Bot.AnswerCallbackQuery(press.PressId, toast, cancellationToken: ct);
        }
        catch (RequestException ex)
        {
            throw Translate(ex);
        }
    }

    public async Task IndicateTypingAsync(ChatId chat, CancellationToken ct)
    {
        try
        {
            await Bot.SendChatAction(ChatIdOf(chat), ChatAction.Typing, cancellationToken: ct);
        }
        catch (RequestException ex)
        {
            throw Translate(ex);
        }
    }

    private static ParseMode ParseMode(OutgoingMessage message) =>
        message.Rich ? global::Telegram.Bot.Types.Enums.ParseMode.Html : global::Telegram.Bot.Types.Enums.ParseMode.None;

    private static InlineKeyboardMarkup? Markup(Keyboard? keyboard) =>
        keyboard is { Rows.Count: > 0 }
            ? new InlineKeyboardMarkup(keyboard.Rows.Select(row => row.Select(Button)))
            : null;

    /// <summary>
    /// Длинный callback_data Telegram отвергает вместе со всем сообщением. Это ошибка экрана,
    /// а не транспорта, поэтому в логе называем кнопку.
    /// </summary>
    private static InlineKeyboardButton Button(KeyboardButton button)
    {
        var bytes = Encoding.UTF8.GetByteCount(button.Data);
        if (bytes > TelegramLimits.ButtonDataBytes)
            throw new ArgumentException($"Данные кнопки «{button.Label}» занимают {bytes} байт, предел Telegram — {TelegramLimits.ButtonDataBytes}.", nameof(button));

        return InlineKeyboardButton.WithCallbackData(button.Label, button.Data);
    }

    /// <summary>Адрес чужого канала сюда попасть не должен: это ошибка хоста, а не транспорта.</summary>
    private static long ChatIdOf(ChatId chat) => chat is TelegramChatId { Value: var id }
        ? id
        : throw new ArgumentException($"Адрес {chat.Key} не из Telegram.", nameof(chat));

    private static string MessageIdOf(int messageId) => messageId.ToString(CultureInfo.InvariantCulture);

    private static int MessageId(MessageRef message) => int.Parse(message.Id, CultureInfo.InvariantCulture);

    /// <summary>Единственный отказ со своим именем в <see cref="ChannelFailure"/>.</summary>
    private static bool IsMarkupRejected(ApiRequestException ex) =>
        ex.ErrorCode == 400 && ex.Message.Contains("parse", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Без разметки повторяем любой Bad Request, не только «can't parse»: неподдерживаемый
    /// тег и «message is too long» после экранирования — тоже 400, и без повтора ответ агента
    /// пропал бы. Кроме «chat not found»: туда не дойдёт и голый текст. 429 и 403 исключены
    /// намеренно — первый ждёт retry_after у вызывающего, второй повтором не лечится.
    /// </summary>
    private static bool RetryWithoutMarkup(ApiRequestException ex) =>
        ex.ErrorCode == 400 && !IsUnreachable(ex);

    private static bool IsUnreachable(ApiRequestException ex) =>
        ex.ErrorCode == 403 || ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase);

    private static ChannelRequestException Translate(RequestException ex)
    {
        if (ex is not ApiRequestException api)
            return new ChannelRequestException(ex.Message, ChannelFailure.Unknown, inner: ex);

        if (api.Parameters?.RetryAfter is { } seconds)
            return new ChannelRequestException(api.Message, ChannelFailure.RateLimited, TimeSpan.FromSeconds(seconds), api);

        if (IsMarkupRejected(api))
            return new ChannelRequestException(api.Message, ChannelFailure.MarkupRejected, inner: api);

        // 403 — бот заблокирован или не может писать первым; «chat not found» о том же.
        if (IsUnreachable(api))
            return new ChannelRequestException(api.Message, ChannelFailure.CannotReach, inner: api);

        return new ChannelRequestException(api.Message, ChannelFailure.Unknown, inner: api);
    }
}
