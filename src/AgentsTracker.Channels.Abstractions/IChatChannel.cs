namespace AgentsTracker.Channels;

/// <summary>
/// Канал чата глазами хоста: один мессенджер, один бот. Порядок вызовов —
/// <see cref="ConnectAsync"/> → <see cref="PublishCommandsAsync"/> → стартовые сообщения →
/// <see cref="ListenAsync"/>, ответы уходят параллельно с прослушиванием. Отказ транспорта
/// всегда <see cref="ChannelRequestException"/>: типов конкретного мессенджера фичи не знают.
/// </summary>
public interface IChatChannel
{
    /// <summary>Ключ канала в конфиге и в <see cref="ChatId.Key"/>: «telegram».</summary>
    string Id { get; }

    /// <summary>Как назвать канал человеку: «Telegram».</summary>
    string DisplayName { get; }

    ChannelLimits Limits { get; }

    /// <summary>Кому разрешено управлять агентом — из настроек канала. Политику применяет хост.</summary>
    IReadOnlyCollection<UserId> AllowedUsers { get; }

    /// <summary>
    /// Личный чат с пользователем, если канал умеет вычислить адрес без переписки (в Telegram
    /// это тот же id). null — писать первым некуда. Адрес доставку не обещает: бот, которому
    /// ещё не писали, получит <see cref="ChannelFailure.CannotReach"/>.
    /// </summary>
    ChatId? DirectChat(UserId user);

    /// <summary>Адрес из <see cref="ChatId.Key"/> в state.json. null — ключ не этого канала.</summary>
    ChatId? ParseChat(string key);

    /// <summary>Ошибки настроек канала — хост печатает их и не стартует.</summary>
    IReadOnlyList<string> Validate();

    /// <summary>Проверка связи с мессенджером. Возвращает имя бота для лога.</summary>
    Task<string> ConnectAsync(CancellationToken ct);

    /// <summary>Подсказка команд в интерфейсе канала. Ошибку канал глотает — шлюз работает и без неё.</summary>
    Task PublishCommandsAsync(IReadOnlyList<ChatCommand> commands, CancellationToken ct);

    /// <summary>Принимает входящее до отмены токена, отдавая его в <paramref name="sink"/>.</summary>
    Task ListenAsync(IChatInbound sink, CancellationToken ct);

    Task<MessageRef> SendAsync(ChatId chat, OutgoingMessage message, CancellationToken ct);

    /// <summary>Текст файлом: то, что в сообщение не влезает (длинный код, полный ввод инструмента).</summary>
    Task<MessageRef> SendFileAsync(ChatId chat, string fileName, string text, CancellationToken ct);

    /// <summary>
    /// Файл как есть, документом. Поток, а не путь: файлы открывает хост, канал только
    /// передаёт. Размер и подпись — под <see cref="ChannelLimits"/>, проверяет вызывающий.
    /// </summary>
    Task<MessageRef> SendDocumentAsync(ChatId chat, string fileName, Stream content, string? caption, CancellationToken ct);

    /// <summary>Картинка фотографией: мессенджер покажет её в ленте, но может сжать.</summary>
    Task<MessageRef> SendPhotoAsync(ChatId chat, string fileName, Stream content, string? caption, CancellationToken ct);

    /// <summary>
    /// Тело вложения в переданный поток. Поток, а не путь, — как и у отправки: куда положить
    /// файл и сколько его хранить, решает хост. Размер сверх <see cref="ChannelLimits"/>
    /// вызывающий обязан отсечь сам: канал не знает, зачем файл нужен.
    /// </summary>
    Task DownloadAttachmentAsync(IncomingAttachment attachment, Stream destination, CancellationToken ct);

    Task EditAsync(MessageRef message, OutgoingMessage content, CancellationToken ct);

    Task DeleteAsync(MessageRef message, CancellationToken ct);

    /// <summary>Ответ на нажатие: снять «часики» с кнопки и, если есть, показать короткий текст.</summary>
    Task AcknowledgeAsync(ButtonPress press, string? toast, CancellationToken ct);

    /// <summary>Индикатор «печатает» — на несколько секунд, канал сам его гасит.</summary>
    Task IndicateTypingAsync(ChatId chat, CancellationToken ct);
}
