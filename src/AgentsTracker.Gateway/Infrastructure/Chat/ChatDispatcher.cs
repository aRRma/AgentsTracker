using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Infrastructure.Chat;

/// <summary>
/// Единственная точка входа сообщений от пользователя. Сам ничего не делает — проверяет,
/// кто пишет, и раздаёт входящее обработчикам фич. Канал доставки здесь не важен.
/// </summary>
public sealed class ChatDispatcher(
    IChatChannel channel,
    IEnumerable<IChatCommandHandler> commandHandlers,
    IEnumerable<IChatButtonHandler> buttonHandlers,
    IEnumerable<IChatTextHandler> textHandlers,
    SessionStore store,
    IAuditLog audit,
    ILogger<ChatDispatcher> logger) : IChatInbound
{
    /// <summary>Команда → обработчик. Дубликат команды у двух фич — ошибка конфигурации, падаем на старте.</summary>
    private readonly Dictionary<string, IChatCommandHandler> _commands = commandHandlers
        .SelectMany(h => h.Commands.Select(c => (Command: c, Handler: h)))
        .ToDictionary(p => p.Command, p => p.Handler, StringComparer.Ordinal);

    private readonly IChatButtonHandler[] _buttons = [.. buttonHandlers];
    private readonly IChatTextHandler[] _texts = [.. textHandlers];

    /// <summary>Кому разрешено: сравниваем по адресу целиком, а не по числу — каналов может быть несколько.</summary>
    private readonly HashSet<UserId> _allowed = [.. channel.AllowedUsers];

    public async Task OnMessageAsync(IncomingMessage message, CancellationToken ct)
    {
        if (!IsAllowed(message.User, message.Chat, message.Kind)) return;

        var (command, argument) = ParseCommand(message.Text);

        // Команды шлюза разбираем до текстовых обработчиков: иначе /stop уйдёт в ожидающий
        // свободный ответ и прервать зависший запуск будет нечем.
        if (command is not null && _commands.TryGetValue(command, out var handler))
        {
            audit.Write(AuditEvent.Now(
                AuditKinds.Message, argument.Length > 0 ? $"{command} {Text.Preview(argument)}" : command,
                message.User, message.Chat, store.ProjectPath, store.SessionId));
            await handler.HandleAsync(new ChatCommandContext(message.Chat, message.User, command, argument), ct);
            return;
        }

        foreach (var textHandler in _texts)
        {
            if (await textHandler.TryHandleAsync(message.Chat, message.User, message.Text, ct)) return;
        }

        logger.LogWarning("Сообщение из чата {Chat} никто не обработал", message.Chat.Key);
    }

    /// <summary>
    /// Меню и карточки подтверждений делят один поток нажатий: каждый обработчик узнаёт свои
    /// по префиксу данных кнопки.
    /// </summary>
    public async Task OnButtonAsync(ButtonPress press, CancellationToken ct)
    {
        if (!IsAllowed(press.User, press.Chat, press.Kind)) return;

        var handler = _buttons.FirstOrDefault(h => h.CanHandle(press.Data));

        if (handler is null)
        {
            logger.LogWarning("Нет обработчика для кнопки «{Data}»", press.Data);
            return;
        }

        await handler.HandleAsync(press, ct);
    }

    /// <summary>Разбирает «/команда@бот аргумент». Возвращает (null, "") для обычного текста.</summary>
    private static (string? Command, string Argument) ParseCommand(string text)
    {
        if (!text.StartsWith('/')) return (null, "");

        var space = text.IndexOf(' ');
        var command = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var argument = space < 0 ? "" : text[(space + 1)..].Trim();

        // /command@botname
        var at = command.IndexOf('@');
        if (at > 0) command = command[..at];

        return (command, argument);
    }

    /// <summary>
    /// Пускает только разрешённого пользователя и только из личного чата: в группе ответы
    /// агента (код, содержимое файлов) и кнопки подтверждений увидели бы все участники.
    /// </summary>
    private bool IsAllowed(UserId user, ChatId chat, ChatKind kind)
    {
        if (!_allowed.Contains(user))
        {
            logger.LogWarning(
                "Отклонено сообщение от постороннего пользователя. Пользователь: {User}, чат: {Chat}. " +
                "Если это вы — добавьте id в настройки канала.",
                user.Key, chat.Key);
            audit.Write(AuditEvent.Now(AuditKinds.AccessRejected, "не в списке разрешённых", user, chat, outcome: "rejected"));
            return false;
        }

        // Неизвестный тип чата (у Telegram — нажатие на сообщение, которого канал не увидел)
        // отклоняем вместе с групповыми: иначе кнопку из группы нажали бы в обход проверки,
        // которую сообщения проходят.
        if (kind is not ChatKind.Direct)
        {
            var reason = kind is ChatKind.Unknown ? "чат неизвестен" : "групповой чат";
            logger.LogWarning("Отклонено обновление: {Reason}, чат {Chat}. Шлюз работает только в личке.", reason, chat.Key);
            audit.Write(AuditEvent.Now(AuditKinds.AccessRejected, reason, user, chat, outcome: "rejected"));
            return false;
        }

        return true;
    }
}
