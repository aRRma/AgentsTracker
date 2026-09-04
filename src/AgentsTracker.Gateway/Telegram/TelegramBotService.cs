using System.Text;
using AgentsTracker.Gateway.Approvals;
using AgentsTracker.Gateway.Configuration;
using AgentsTracker.Gateway.State;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentsTracker.Gateway.Telegram;

/// <summary>Long polling: единственная точка входа сообщений от пользователя.</summary>
public sealed class TelegramBotService(
    ITelegramBotClient bot,
    ChatWorker worker,
    ApprovalBroker broker,
    SessionStore store,
    SettingsMenu menu,
    IOptions<GatewayOptions> options,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private readonly GatewayOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var me = await bot.GetMe(stoppingToken);
            logger.LogInformation("Бот @{Username} готов. Проект: {Project}", me.Username, store.ProjectPath);
            await PublishCommandsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Не удалось подключиться к Telegram API. Проверьте токен и доступность api.telegram.org (Gateway:Proxy).");
            return;
        }

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
            DropPendingUpdates = true,
        };

        await bot.ReceiveAsync(HandleUpdateAsync, HandleErrorAsync, receiverOptions, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken ct)
    {
        try
        {
            switch (update)
            {
                // Меню и карточки подтверждений делят один поток callback-ов, поэтому
                // разводим их по префиксу: у меню он "cfg:", у карточек — hex-id запроса.
                case { CallbackQuery: { } callback } when IsAllowed(callback.From.Id, callback.Message?.Chat.Id):
                    if (callback.Data?.StartsWith(SettingsMenu.CallbackPrefix, StringComparison.Ordinal) == true)
                        await menu.HandleCallbackAsync(callback, ct);
                    else
                        await broker.HandleCallbackAsync(callback, ct);
                    break;

                case { Message: { Text: { Length: > 0 } text, From: { } from } message }
                    when IsAllowed(from.Id, message.Chat.Id):
                    await HandleTextAsync(message.Chat.Id, text.Trim(), ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка обработки обновления");
        }
    }

    /// <summary>Команды самого шлюза — в отличие от слэш-команд Claude Code.</summary>
    private static readonly HashSet<string> GatewayCommands = new(StringComparer.Ordinal)
    {
        "/start", "/help", "/new", "/stop", "/status", "/model", "/mode", "/rules",
        "/menu", "/settings", "/effort", "/usage", "/project", "/sessions",
    };

    private async Task HandleTextAsync(long chatId, string text, CancellationToken ct)
    {
        var (command, argument) = ParseCommand(text);

        // Команды шлюза разбираем до TryConsumeText: иначе /stop уйдёт в ожидающий
        // свободный ответ и прервать зависший запуск будет нечем.
        if (command is not null && GatewayCommands.Contains(command))
        {
            await HandleCommandAsync(chatId, command, argument, ct);
            return;
        }

        // Если агент попросил свободный текст (причина отказа, свой вариант ответа) — отдаём туда.
        if (broker.TryConsumeText(text)) return;

        // Проверяем занятость до постановки в очередь, иначе первое же сообщение
        // может увидеть уже начавшуюся собственную обработку.
        var wasBusy = worker.IsBusy;

        // ActiveChatId выставляет ChatWorker перед самым запуском: сделать это здесь значило бы
        // увести карточки уже идущего запуска в чат другого пользователя.
        worker.Enqueue(chatId, text);

        if (wasBusy)
            await bot.SendMessage(chatId, "📥 Добавлено в очередь — отвечу, как освобожусь.", cancellationToken: ct);
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

    private async Task HandleCommandAsync(long chatId, string command, string argument, CancellationToken ct)
    {
        switch (command)
        {
            case "/start":
            case "/help":
                await bot.SendMessage(chatId, Help, cancellationToken: ct);
                break;

            case "/new":
                store.SetSessionId(null);
                await bot.SendMessage(chatId, "🆕 Начата новая сессия — прошлый контекст забыт.", cancellationToken: ct);
                break;

            case "/stop":
                await bot.SendMessage(
                    chatId,
                    worker.Stop() ? "🛑 Остановлено." : "Сейчас ничего не выполняется.",
                    cancellationToken: ct);
                break;

            case "/status":
                await bot.SendMessage(chatId, BuildStatus(), cancellationToken: ct);
                break;

            case "/mode":
                await bot.SendMessage(chatId, ChangeMode(argument), cancellationToken: ct);
                break;

            case "/rules":
                await bot.SendMessage(chatId, ManageRules(argument), cancellationToken: ct);
                break;

            case "/menu":
            case "/settings":
                await menu.OpenAsync(chatId, ct);
                break;

            case "/project":
                await menu.OpenAsync(chatId, ct, "proj");
                break;

            case "/sessions":
                await menu.OpenAsync(chatId, ct, "sess");
                break;

            case "/usage":
                await menu.OpenAsync(chatId, ct, "usage");
                break;

            case "/effort":
                if (argument.Length == 0)
                {
                    await menu.OpenAsync(chatId, ct, "effort");
                    break;
                }

                await bot.SendMessage(chatId, ChangeEffort(argument), cancellationToken: ct);
                break;

            case "/model":
                if (argument.Length == 0)
                {
                    await bot.SendMessage(
                        chatId,
                        $"Текущая модель: {store.Model ?? _options.Model ?? "по умолчанию"}\nЗадать: /model sonnet | opus | haiku | reset",
                        cancellationToken: ct);
                    break;
                }

                var model = argument.Equals("reset", StringComparison.OrdinalIgnoreCase) ? null : argument;
                store.SetModel(model);
                await bot.SendMessage(chatId, $"Модель: {model ?? "по умолчанию"}", cancellationToken: ct);
                break;
        }
    }

    /// <summary>Меняет уровень усилий модели. Применяется со следующего запуска.</summary>
    private string ChangeEffort(string argument)
    {
        if (argument.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            store.SetEffort(null);
            return $"🎚 Effort: {_options.Effort ?? "по умолчанию"} — как в конфиге.";
        }

        if (EffortLevels.Resolve(argument) is not { } level)
            return $"Не знаю уровень «{argument}». Доступно: {string.Join(", ", EffortLevels.All)}, reset.";

        store.SetEffort(level);
        return $"🎚 {EffortLevels.Describe(level)}. Применится со следующего запуска.";
    }

    /// <summary>
    /// Список команд для кнопки «Меню» в Telegram: без него бот выглядит как окно без подсказок.
    /// Ошибку глотаем — сам шлюз работает и без опубликованного списка.
    /// </summary>
    private async Task PublishCommandsAsync(CancellationToken ct)
    {
        BotCommand[] commands =
        [
            new() { Command = "menu", Description = "настройки: репозиторий, модель, сессии, статистика" },
            new() { Command = "status", Description = "что происходит прямо сейчас" },
            new() { Command = "new", Description = "новая сессия, контекст сбрасывается" },
            new() { Command = "stop", Description = "прервать текущий запуск" },
            new() { Command = "sessions", Description = "переключиться между сессиями" },
            new() { Command = "usage", Description = "расход: запуски, токены, стоимость" },
            new() { Command = "project", Description = "сменить репозиторий" },
            new() { Command = "model", Description = "сменить модель" },
            new() { Command = "effort", Description = "сколько модели думать" },
            new() { Command = "mode", Description = "уровень доступа к машине" },
            new() { Command = "rules", Description = "разрешения, выданные кнопкой «Всегда»" },
            new() { Command = "help", Description = "справка" },
        ];

        try
        {
            await bot.SetMyCommands(commands, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось опубликовать список команд");
        }
    }

    private string BuildStatus()
    {
        var state = store.Snapshot();

        return $"""
            📁 {store.ProjectPath}
            🧠 {store.Model ?? _options.Model ?? "модель по умолчанию"}
            🎚 Effort: {store.Effort ?? "по умолчанию"}
            🔐 Доступ: {CurrentMode}{(state.PermissionMode is null ? " (из конфига)" : "")}
            🧵 Сессия: {store.SessionId ?? "новая (ещё не создана)"}
            ⚙️ {(worker.IsBusy ? "выполняется" : "простаивает")}, в очереди: {worker.QueueLength}
            🕔 Последняя активность: {state.LastActivityUtc?.ToLocalTime().ToString("g") ?? "—"}
            ♾ Правил «всегда»: {state.AlwaysAllow.Count}
            """;
    }

    private string CurrentMode => store.PermissionMode ?? _options.PermissionMode;

    /// <summary>
    /// Показывает или меняет уровень доступа агента к машине. Новый режим ложится в state.json
    /// и переживает перезапуск; текущий запуск доигрывает со старым.
    /// </summary>
    private string ChangeMode(string argument)
    {
        if (argument.Length == 0)
        {
            var list = string.Join(
                '\n',
                PermissionModes.Selectable.Select(m =>
                    $"{(m == CurrentMode ? "▶" : "·")} {PermissionModes.Describe(m)}"));

            return $"""
                Уровень доступа: {CurrentMode}

                {list}

                Сменить: /mode plan|default|acceptEdits|auto
                Вернуть значение из конфига ({_options.PermissionMode}): /mode reset
                """;
        }

        if (argument.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            store.SetPermissionMode(null);
            return $"Уровень доступа: {_options.PermissionMode} — как в конфиге. Применится со следующего запуска.";
        }

        var mode = PermissionModes.Resolve(argument);

        if (mode is null || !PermissionModes.Selectable.Contains(mode, StringComparer.Ordinal))
        {
            // dontAsk и bypassPermissions сюда не попадают намеренно: полное снятие
            // подтверждений остаётся правкой конфига на самой машине.
            return $"""
                Не знаю уровень «{argument}». Доступно: {string.Join(", ", PermissionModes.Selectable)}, reset.
                Снять подтверждения полностью можно только в appsettings.Local.json на самой машине.
                """;
        }

        if (mode == CurrentMode) return $"Уже {PermissionModes.Describe(mode)}.";

        store.SetPermissionMode(mode);

        var note = worker.IsBusy ? "\nТекущий запуск доигрывает со старым уровнем." : "";
        return $"🔐 {PermissionModes.Describe(mode)}.\nПрименится со следующего запуска.{note}";
    }

    /// <summary>Показывает и снимает разрешения, выданные кнопкой «Всегда».</summary>
    private string ManageRules(string argument)
    {
        var rules = store.Snapshot().AlwaysAllow;

        if (argument.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            var removed = store.ClearAlwaysAllow();
            return removed == 0
                ? "Правил «всегда» и не было."
                : $"♾ Снято правил: {removed}. Теперь всё снова спрашивается кнопками.";
        }

        if (argument.StartsWith("del", StringComparison.OrdinalIgnoreCase))
        {
            var number = argument[3..].Trim();

            if (!int.TryParse(number, out var index) || index < 1 || index > rules.Count)
                return $"Нужен номер правила из списка /rules (1..{rules.Count}).";

            var signature = rules[index - 1];
            store.RemoveAlwaysAllow(signature);
            return $"♾ Снято: {Shorten(signature, RuleDisplayLimit)}";
        }

        if (rules.Count == 0)
            return "Правил «всегда» нет — каждое действие спрашивается кнопками.";

        // Правил может накопиться сколько угодно, а сообщение Telegram ограничено:
        // набираем список по бюджету, остаток показываем числом.
        var list = new StringBuilder();
        var shown = 0;

        foreach (var rule in rules)
        {
            var line = $"{shown + 1}. {Shorten(rule, RuleDisplayLimit)}";
            if (list.Length + line.Length + 1 > RuleListBudget) break;

            if (shown > 0) list.Append('\n');
            list.Append(line);
            shown++;
        }

        var tail = shown < rules.Count ? $"\n…и ещё {rules.Count - shown}." : "";

        return $"""
            Разрешено без вопросов ({rules.Count}):
            {list}{tail}

            Снять: /rules del <номер> | /rules clear
            """;
    }

    private static string Shorten(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";

    private bool IsAllowed(long userId, long? chatId)
    {
        if (_options.AllowedUserIds.Contains(userId)) return true;

        logger.LogWarning(
            "Отклонено сообщение от постороннего пользователя. user id: {UserId}, chat id: {ChatId}. " +
            "Если это вы — добавьте id в Gateway:AllowedUserIds.",
            userId, chatId);

        return false;
    }

    private Task HandleErrorAsync(ITelegramBotClient _, Exception exception, CancellationToken ct)
    {
        logger.LogError(exception, "Ошибка Telegram polling");
        return Task.CompletedTask;
    }

    /// <summary>Бюджет списка правил в символах и предел длины одного правила.</summary>
    private const int RuleListBudget = 3500;
    private const int RuleDisplayLimit = 200;

    private const string Help = """
        Шлюз к Claude Code. Пишите задачу обычным сообщением.

        /menu — настройки кнопками: репозиторий, модель, effort, сессии, статистика

        /new — начать новую сессию (сбросить контекст)
        /stop — прервать текущий запуск
        /status — где работаем и что происходит
        /sessions — список сессий проекта и переключение между ними
        /project — сменить репозиторий
        /usage — расход: запуски, токены, стоимость
        /model sonnet|opus|haiku|reset — сменить модель
        /effort low|medium|high|xhigh|max|reset — сколько модели думать
        /mode plan|default|acceptEdits|auto|reset — уровень доступа к машине
        /rules — что разрешено без вопросов; /rules del <n>, /rules clear

        Когда агенту нужно разрешение, придёт карточка с кнопками.
        Слэш-команды самого Claude Code (например /review) передаются агенту как есть.
        """;
}
