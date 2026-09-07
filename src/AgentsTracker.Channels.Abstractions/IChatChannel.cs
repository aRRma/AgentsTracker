namespace AgentsTracker.Channels;

/// <summary>
/// Канал чата глазами хоста: один мессенджер, один бот. Хост зовёт методы в таком порядке:
/// <see cref="ConnectAsync"/> → <see cref="PublishCommandsAsync"/> → стартовые сообщения →
/// <see cref="ListenAsync"/>, и параллельно с прослушиванием шлёт ответы.
/// Все методы при отказе транспорта бросают <see cref="ChannelRequestException"/> — фичи
/// не знают ни одного типа конкретного мессенджера.
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

    /// <summary>Личный чат с пользователем, если канал умеет его вычислить; null — писать первым нельзя.</summary>
    ChatId? DirectChat(UserId user);

    /// <summary>Обратно из <see cref="ChatId.Key"/>: адрес, сохранённый в state.json прошлым запуском.</summary>
    bool TryParseChat(string key, out ChatId? chat);

    /// <summary>Ошибки настроек канала — хост печатает их и не стартует.</summary>
    IReadOnlyList<string> Validate();

    /// <summary>Проверка связи с мессенджером. Возвращает имя бота для лога.</summary>
    Task<string> ConnectAsync(CancellationToken ct);

    /// <summary>Подсказка команд в интерфейсе канала. Ошибку канал глотает: шлюз работает и без неё.</summary>
    Task PublishCommandsAsync(IReadOnlyList<ChatCommand> commands, CancellationToken ct);

    /// <summary>Принимает входящее до отмены токена, отдавая его в <paramref name="sink"/>.</summary>
    Task ListenAsync(IChatInbound sink, CancellationToken ct);

    Task<MessageRef> SendAsync(ChatId chat, OutgoingMessage message, CancellationToken ct);

    /// <summary>Текст файлом: то, что в сообщение не влезает (длинный код, полный ввод инструмента).</summary>
    Task<MessageRef> SendFileAsync(ChatId chat, string fileName, string text, CancellationToken ct);

    Task EditAsync(MessageRef message, OutgoingMessage content, CancellationToken ct);

    Task DeleteAsync(MessageRef message, CancellationToken ct);

    /// <summary>Ответ на нажатие: снять «часики» с кнопки и, если есть, показать короткий текст.</summary>
    Task AcknowledgeAsync(ButtonPress press, string? toast, CancellationToken ct);

    /// <summary>Индикатор «печатает» — на несколько секунд, канал сам его гасит.</summary>
    Task IndicateTypingAsync(ChatId chat, CancellationToken ct);
}
