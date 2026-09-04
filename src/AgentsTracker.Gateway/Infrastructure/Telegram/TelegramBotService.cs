using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentsTracker.Gateway.Infrastructure.Telegram;

/// <summary>
/// Long polling: единственная точка входа сообщений от пользователя. Сам ничего не делает —
/// проверяет, кто пишет, и раздаёт обновления обработчикам фич.
/// </summary>
public sealed class TelegramBotService(
    ITelegramBotClient bot,
    IEnumerable<ITelegramCommandHandler> commandHandlers,
    IEnumerable<ITelegramCallbackHandler> callbackHandlers,
    IEnumerable<ITelegramTextHandler> textHandlers,
    BotCommandsCatalog commands,
    SessionStore store,
    IAuditLog audit,
    IHostApplicationLifetime lifetime,
    IOptions<GatewayOptions> options,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private readonly GatewayOptions _options = options.Value;

    /// <summary>Команда → обработчик. Дубликат команды у двух фич — ошибка конфигурации, падаем на старте.</summary>
    private readonly Dictionary<string, ITelegramCommandHandler> _commands = commandHandlers
        .SelectMany(h => h.Commands.Select(c => (Command: c, Handler: h)))
        .ToDictionary(p => p.Command, p => p.Handler, StringComparer.Ordinal);

    private readonly ITelegramCallbackHandler[] _callbacks = [.. callbackHandlers];
    private readonly ITelegramTextHandler[] _texts = [.. textHandlers];

    /// <summary>
    /// Сколько раз пробовать достучаться до Telegram при старте. При автозапуске на вход в
    /// систему сеть часто поднимается позже шлюза, поэтому первая неудача — не приговор.
    /// </summary>
    private const int ConnectAttempts = 6;
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await ConnectAsync(stoppingToken))
        {
            // Молча жить без Telegram нельзя: хост выглядел бы работающим, а чат — мёртвым.
            // Ненулевой код выхода даёт Планировщику повод перезапустить задачу.
            Environment.ExitCode = 1;
            lifetime.StopApplication();
            return;
        }

        audit.Write(AuditEvent.Now(AuditKinds.Gateway, "старт", project: store.ProjectPath));

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
            DropPendingUpdates = true,
        };

        try
        {
            await bot.ReceiveAsync(HandleUpdateAsync, HandleErrorAsync, receiverOptions, stoppingToken);
        }
        finally
        {
            audit.Write(AuditEvent.Now(AuditKinds.Gateway, "стоп"));
        }
    }

    private async Task<bool> ConnectAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var me = await bot.GetMe(stoppingToken);
                logger.LogInformation("Бот @{Username} готов. Проект: {Project}", me.Username, store.ProjectPath);
                await commands.PublishAsync(stoppingToken);
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                if (attempt >= ConnectAttempts)
                {
                    logger.LogCritical(ex,
                        "Не удалось подключиться к Telegram API за {Attempts} попыток. " +
                        "Проверьте токен и доступность api.telegram.org (Gateway:Proxy).", attempt);
                    return false;
                }

                logger.LogWarning("Telegram API недоступен ({Message}), попытка {Attempt} из {Attempts} через {Delay} с",
                    ex.Message, attempt, ConnectAttempts, ConnectRetryDelay.TotalSeconds);
            }

            try { await Task.Delay(ConnectRetryDelay, stoppingToken); }
            catch (OperationCanceledException) { return false; }
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken ct)
    {
        try
        {
            switch (update)
            {
                case { CallbackQuery: { } callback } when IsAllowed(callback.From.Id, callback.Message?.Chat):
                    await HandleCallbackAsync(callback, ct);
                    break;

                case { Message: { Text: { Length: > 0 } text, From: { } from } message }
                    when IsAllowed(from.Id, message.Chat):
                    await HandleTextAsync(message.Chat.Id, from.Id, text.Trim(), ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка обработки обновления");
        }
    }

    /// <summary>
    /// Меню и карточки подтверждений делят один поток callback-ов: каждый обработчик
    /// узнаёт свои по префиксу callback_data.
    /// </summary>
    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var data = callback.Data ?? "";
        var handler = _callbacks.FirstOrDefault(h => h.CanHandle(data));

        if (handler is null)
        {
            logger.LogWarning("Нет обработчика для callback «{Data}»", data);
            return;
        }

        await handler.HandleAsync(callback, ct);
    }

    private async Task HandleTextAsync(long chatId, long userId, string text, CancellationToken ct)
    {
        var (command, argument) = ParseCommand(text);

        // Команды шлюза разбираем до текстовых обработчиков: иначе /stop уйдёт в ожидающий
        // свободный ответ и прервать зависший запуск будет нечем.
        if (command is not null && _commands.TryGetValue(command, out var handler))
        {
            audit.Write(AuditEvent.Now(
                AuditKinds.Message, argument.Length > 0 ? $"{command} {argument}" : command,
                userId, chatId, store.ProjectPath, store.SessionId));
            await handler.HandleAsync(new TelegramCommandContext(chatId, userId, command, argument), ct);
            return;
        }

        foreach (var textHandler in _texts)
        {
            if (await textHandler.TryHandleAsync(chatId, userId, text, ct)) return;
        }

        logger.LogWarning("Сообщение из чата {ChatId} никто не обработал", chatId);
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
    private bool IsAllowed(long userId, Chat? chat)
    {
        if (!_options.AllowedUserIds.Contains(userId))
        {
            logger.LogWarning(
                "Отклонено сообщение от постороннего пользователя. user id: {UserId}, chat id: {ChatId}. " +
                "Если это вы — добавьте id в Gateway:AllowedUserIds.",
                userId, chat?.Id);
            audit.Write(AuditEvent.Now(AuditKinds.AccessRejected, "не в AllowedUserIds", userId, chat?.Id, outcome: "rejected"));
            return false;
        }

        // Проверка «не личный чат» перевёрнута в «не является личным»: у callback-а Telegram
        // может не прислать сообщение (оно старое или недоступно боту), и тогда тип чата
        // неизвестен. Пропускать такое обновление нельзя — иначе кнопку из группы нажали бы
        // в обход проверки, которую сообщения проходят.
        if (chat is not { Type: ChatType.Private })
        {
            var type = chat is null ? "чат неизвестен" : $"групповой чат ({chat.Type})";
            logger.LogWarning("Отклонено обновление: {Reason}, чат {ChatId}. Шлюз работает только в личке.",
                type, chat?.Id);
            audit.Write(AuditEvent.Now(AuditKinds.AccessRejected, type, userId, chat?.Id, outcome: "rejected"));
            return false;
        }

        return true;
    }

    private Task HandleErrorAsync(ITelegramBotClient _, Exception exception, CancellationToken ct)
    {
        logger.LogError(exception, "Ошибка Telegram polling");
        return Task.CompletedTask;
    }
}
