using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentsTracker.Gateway.Configuration;
using AgentsTracker.Gateway.State;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentsTracker.Gateway.Telegram;

/// <summary>
/// Меню настроек: одно сообщение, которое перерисовывается кнопками. Отсюда меняются
/// репозиторий, модель, effort, уровень доступа и сессия, отсюда же видно расход.
/// </summary>
public sealed class SettingsMenu(
    ITelegramBotClient bot,
    SessionStore store,
    ProjectCatalog catalog,
    ChatWorker worker,
    IOptions<GatewayOptions> options,
    ILogger<SettingsMenu> logger)
{
    /// <summary>
    /// Префикс callback_data меню. Карточки подтверждений используют «id:ключ» с восьмизначным
    /// hex-id, так что перепутать их нельзя — но разбирает их всё равно другой обработчик.
    /// </summary>
    public const string CallbackPrefix = "cfg:";

    /// <summary>Сколько строк показывать в списках, чтобы сообщение и клавиатура остались читаемыми.</summary>
    private const int MaxProjectsShown = 12;
    private const int MaxSessionsShown = 8;
    private const int MaxDaysShown = 5;

    private static readonly string[] ModelAliases = ["opus", "sonnet", "haiku", "fable"];

    private readonly GatewayOptions _options = options.Value;

    /// <summary>Показывает меню новым сообщением. screen — экран, с которого начать.</summary>
    public async Task OpenAsync(long chatId, CancellationToken ct, string screen = "root")
    {
        var (html, keyboard) = Render(screen);
        await bot.SendMessage(chatId, html, ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
    }

    public async Task HandleCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        var data = query.Data![CallbackPrefix.Length..];
        var separator = data.IndexOf(':');
        var screen = separator < 0 ? data : data[..separator];
        var argument = separator < 0 ? "" : data[(separator + 1)..];

        if (screen == "close")
        {
            await AnswerAsync(query.Id, null, ct);
            if (query.Message is { } closing) await DeleteQuietlyAsync(closing, ct);
            return;
        }

        // Применяем выбор до отрисовки: экран должен показать уже новое состояние.
        var toast = argument.Length > 0 ? Apply(screen, argument) : null;
        await AnswerAsync(query.Id, toast, ct);

        if (query.Message is not { } message) return;

        var (html, keyboard) = Render(screen);
        await EditQuietlyAsync(message, html, keyboard, ct);
    }

    /// <summary>Применяет нажатие. Возвращает короткий текст для всплывающего уведомления.</summary>
    private string? Apply(string screen, string argument) => screen switch
    {
        "proj" => ApplyProject(argument),
        "model" => ApplyModel(argument),
        "effort" => ApplyEffort(argument),
        "mode" => ApplyMode(argument),
        "sess" => ApplySession(argument),
        "usage" => ApplyUsage(argument),
        _ => null,
    };

    private string? ApplyProject(string argument)
    {
        // Ищем по ключу пути, а не по номеру в списке: между отрисовкой и нажатием список
        // мог измениться (появилась папка-сосед), и номер указал бы на другой проект.
        var project = catalog.List(store.ProjectPath).FirstOrDefault(p => ProjectKey(p) == argument);
        if (project is null) return "Папки уже нет в списке";

        if (ProjectCatalog.Same(project, store.ProjectPath)) return null;

        store.SetProjectPath(project);
        logger.LogInformation("Рабочая папка переключена на {Project}", project);

        // Сессии живут в папке, где созданы: у нового проекта своя активная сессия
        // или ни одной — предупреждаем, чтобы смена контекста не выглядела потерей истории.
        return store.SessionId is null
            ? $"{Path.GetFileName(project)} — сессия начнётся заново"
            : $"{Path.GetFileName(project)} — вернулись к её сессии";
    }

    private string? ApplyModel(string argument)
    {
        var model = argument == "reset" ? null : argument;
        store.SetModel(model);
        return $"Модель: {model ?? "по умолчанию"}";
    }

    private string? ApplyEffort(string argument)
    {
        var effort = argument == "reset" ? null : EffortLevels.Resolve(argument);
        if (argument != "reset" && effort is null) return "Не знаю такой уровень";

        store.SetEffort(effort);
        return $"Effort: {effort ?? "по умолчанию"}";
    }

    private string? ApplyMode(string argument)
    {
        if (argument == "reset")
        {
            store.SetPermissionMode(null);
            return $"Доступ: {_options.PermissionMode} (из конфига)";
        }

        var mode = PermissionModes.Resolve(argument);
        if (mode is null || !PermissionModes.Selectable.Contains(mode, StringComparer.Ordinal))
            return "Этот уровень из чата не переключается";

        store.SetPermissionMode(mode);
        return worker.IsBusy
            ? $"Доступ: {mode} — со следующего запуска"
            : $"Доступ: {mode}";
    }

    private string? ApplySession(string argument)
    {
        var project = store.ProjectPath;

        if (argument == "new")
        {
            store.SetSessionId(null);
            return "Следующее сообщение начнёт новую сессию";
        }

        if (argument == "clear")
        {
            var removed = store.ForgetSessions(project);
            return removed == 0 ? "Список и так пуст" : $"Забыто сессий: {removed}";
        }

        // По началу id, а не по номеру: список упорядочен по активности, и завершившийся
        // между отрисовкой и нажатием запуск сдвинул бы номера на соседнюю сессию.
        var session = store.SessionsFor(project).FirstOrDefault(s => SessionKey(s.Id) == argument);
        if (session is null) return "Сессии уже нет в списке";

        store.SetSessionId(session.Id);
        return worker.IsBusy ? "Сессия сменится со следующего запуска" : "Сессия выбрана";
    }

    private string? ApplyUsage(string argument)
    {
        if (argument != "reset") return null;

        store.ResetUsage();
        return "Статистика обнулена";
    }

    private (string Html, InlineKeyboardMarkup Keyboard) Render(string screen) => screen switch
    {
        "proj" => RenderProjects(),
        "model" => RenderModel(),
        "effort" => RenderEffort(),
        "mode" => RenderMode(),
        "sess" => RenderSessions(),
        "usage" => RenderUsage(),
        _ => RenderRoot(),
    };

    private (string, InlineKeyboardMarkup) RenderRoot()
    {
        var project = store.ProjectPath;
        var session = ActiveSession();

        // Стоимость на подписке — оценка CLI, а не счёт: кредиты шлюз не тратит.
        var budget = store.DailyBudgetUsd;
        var spent = store.SpentToday();
        var today = budget is { } cap
            ? $"{Money(spent)} из {Money(cap)}"
            : Money(spent);

        var html = $"""
            ⚙️ <b>Настройки</b>

            📁 <b>{E(Path.GetFileName(project))}</b>
            <code>{E(project)}</code>
            🧠 Модель: <b>{E(store.Model ?? _options.Model ?? "по умолчанию")}</b>
            🎚 Effort: <b>{E(store.Effort ?? "по умолчанию")}</b>
            🔐 Доступ: <b>{E(CurrentMode)}</b>
            🧵 Сессия: {(session is null ? "<i>новая</i>" : $"<b>{E(session.Title)}</b>")}
            📈 Сегодня (оценка): <b>{E(today)}</b>
            ⚙️ {(worker.IsBusy ? "выполняется" : "простаивает")}, в очереди: {worker.QueueLength}
            """;

        var keyboard = new InlineKeyboardMarkup(
        [
            [Button("📁 Репозиторий", "proj"), Button("🧠 Модель", "model")],
            [Button("🎚 Effort", "effort"), Button("🔐 Доступ", "mode")],
            [Button("🧵 Сессии", "sess"), Button("📊 Статистика", "usage")],
            [Button("✖️ Закрыть", "close")],
        ]);

        return (html, keyboard);
    }

    private (string, InlineKeyboardMarkup) RenderProjects()
    {
        var current = store.ProjectPath;
        var projects = catalog.List(current).Take(MaxProjectsShown).ToArray();

        var lines = projects.Select((path, i) =>
            $"{Marker(ProjectCatalog.Same(path, current))} <b>{E(Path.GetFileName(path))}</b>\n   <code>{E(path)}</code>");

        var source = _options.Projects.Length > 0
            ? "Список задан ключом <code>Gateway:Projects</code>."
            : "Список собран из соседних папок с <code>.git</code> или решением. Задать явно — ключ <code>Gateway:Projects</code>.";

        var html = $"""
            📁 <b>Репозиторий</b>

            {string.Join("\n", lines)}

            <i>У каждой папки своя сессия: переключение не смешивает контексты. {source}</i>
            """;

        var buttons = projects
            .Select(path => Button(
                $"{(ProjectCatalog.Same(path, current) ? "▶ " : "")}{Path.GetFileName(path)}", $"proj:{ProjectKey(path)}"))
            .Chunk(2)
            .ToList();

        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    private (string, InlineKeyboardMarkup) RenderModel()
    {
        var current = store.Model ?? _options.Model;

        var html = $"""
            🧠 <b>Модель</b>

            Сейчас: <b>{E(current ?? "по умолчанию — как настроен Claude Code")}</b>

            <i>Кнопки задают алиас последней модели семейства. Полное имя
            (например <code>claude-sonnet-5</code>) можно задать командой <code>/model claude-sonnet-5</code>.</i>
            """;

        var buttons = ModelAliases
            .Select(alias => Button($"{Marker(alias == current)} {alias}", $"model:{alias}"))
            .Chunk(2)
            .ToList();

        buttons.Add([Button($"{Marker(current is null)} по умолчанию", "model:reset")]);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    private (string, InlineKeyboardMarkup) RenderEffort()
    {
        var current = store.Effort;

        var html = $"""
            🎚 <b>Effort</b> — сколько модели думать

            {string.Join("\n", EffortLevels.All.Select(l => $"{Marker(l == current)} {E(EffortLevels.Describe(l))}"))}

            <i>Выше уровень — дольше и дороже ответ, но лучше на сложных задачах.
            «По умолчанию» отдаёт выбор самому Claude Code.</i>
            """;

        var buttons = EffortLevels.All
            .Select(level => Button($"{Marker(level == current)} {level}", $"effort:{level}"))
            .Chunk(3)
            .ToList();

        buttons.Add([Button($"{Marker(current is null)} по умолчанию", "effort:reset")]);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    private (string, InlineKeyboardMarkup) RenderMode()
    {
        var current = CurrentMode;

        var html = $"""
            🔐 <b>Доступ агента к машине</b>

            {string.Join("\n", PermissionModes.Selectable.Select(m => $"{Marker(m == current)} {E(PermissionModes.Describe(m))}"))}

            <i>Меняется со следующего запуска. Полностью снять подтверждения из чата нельзя —
            только правкой <code>appsettings.Local.json</code> на самой машине.</i>
            """;

        var buttons = PermissionModes.Selectable
            .Select(mode => Button($"{Marker(mode == current)} {mode}", $"mode:{mode}"))
            .Chunk(2)
            .ToList();

        buttons.Add([Button($"из конфига ({_options.PermissionMode})", "mode:reset")]);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    private (string, InlineKeyboardMarkup) RenderSessions()
    {
        var project = store.ProjectPath;
        var active = store.SessionId;
        var sessions = store.SessionsFor(project).Take(MaxSessionsShown).ToArray();

        var body = sessions.Length == 0
            ? "<i>Сессий ещё нет — первое сообщение создаст первую.</i>"
            : string.Join("\n", sessions.Select((s, i) =>
                $"{Marker(s.Id == active)} <b>{i + 1}.</b> {E(s.Title)}\n" +
                $"   {s.Turns} х · {Money(s.CostUsd)} · {E(Ago(s.LastActivityUtc))}"));

        var html = $"""
            🧵 <b>Сессии</b> — {E(Path.GetFileName(project))}

            {body}

            <i>Переключение подставляет сессию в <code>--resume</code> со следующего сообщения.
            «Очистить» убирает записи только из списка шлюза — сами сессии остаются в Claude Code.</i>
            """;

        var buttons = sessions
            .Select((s, i) => Button($"{(s.Id == active ? "▶ " : "")}{i + 1}", $"sess:{SessionKey(s.Id)}"))
            .Chunk(4)
            .ToList();

        buttons.Add([Button("🆕 Новая", "sess:new"), Button("🗑 Очистить", "sess:clear")]);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    private (string, InlineKeyboardMarkup) RenderUsage()
    {
        var usage = store.Snapshot().Usage;
        var total = usage.Total;

        var days = usage.ByDay
            .OrderByDescending(pair => pair.Key, StringComparer.Ordinal)
            .Take(MaxDaysShown)
            .Select(pair => $"· {pair.Key} — {pair.Value.Runs} зап. · {Money(pair.Value.CostUsd)}");

        var models = usage.ByModel
            .OrderByDescending(pair => pair.Value.CostUsd)
            .Select(pair => $"· {E(pair.Key)} — {Money(pair.Value.CostUsd)}");

        var since = usage.SinceUtc is { } from ? from.ToLocalTime().ToString("d MMMM, HH:mm") : "—";

        var html = $"""
            📊 <b>Использование</b>
            <i>с {E(since)}</i>

            Запусков: <b>{total.Runs}</b> · ходов: <b>{total.Turns}</b>
            Стоимость: <b>{Money(total.CostUsd)}</b>
            Время в CLI: <b>{E(Duration(total.DurationMs))}</b>

            Токены: ввод {E(Tokens(total.InputTokens))} · вывод {E(Tokens(total.OutputTokens))}
            Кэш: чтение {E(Tokens(total.CacheReadTokens))} · запись {E(Tokens(total.CacheWriteTokens))}

            <b>По дням</b>
            {(days.Any() ? string.Join("\n", days) : "<i>пусто</i>")}

            <b>По моделям</b>
            {(models.Any() ? string.Join("\n", models) : "<i>пусто</i>")}
            """;

        return (html, new InlineKeyboardMarkup([[Button("♻️ Сбросить", "usage:reset")], [BackButton]]));
    }

    private SessionRecord? ActiveSession()
    {
        var active = store.SessionId;
        if (active is null) return null;

        return store.SessionsFor(store.ProjectPath)
            .FirstOrDefault(s => string.Equals(s.Id, active, StringComparison.Ordinal));
    }

    private string CurrentMode => store.PermissionMode ?? _options.PermissionMode;

    private static InlineKeyboardButton Button(string label, string data) =>
        InlineKeyboardButton.WithCallbackData(label, CallbackPrefix + data);

    private static InlineKeyboardButton BackButton => Button("◀️ Назад", "root");

    private static string Marker(bool selected) => selected ? "▶" : "·";

    /// <summary>Начало id сессии — его хватает, чтобы отличить сессии одного проекта.</summary>
    private static string SessionKey(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];

    /// <summary>
    /// Короткий ключ пути для callback_data (лимит 64 байта, полный путь не влезает).
    /// Регистр не учитываем — как и ProjectCatalog при сравнении путей.
    /// </summary>
    private static string ProjectKey(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(ProjectCatalog.Normalize(path).ToLowerInvariant());
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
    }

    private static string E(string text) => TelegramFormatter.Escape(text);

    /// <summary>Суммы всегда в инвариантной культуре: иначе на русской локали получается «$0,08».</summary>
    private static string Money(decimal value) => value switch
    {
        0m => "$0",
        > 0m and < 0.01m => "<$0.01",
        _ => "$" + value.ToString("0.00", CultureInfo.InvariantCulture),
    };

    private static string Tokens(long count) => count switch
    {
        < 1_000 => count.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => (count / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => (count / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M",
    };

    private static string Duration(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);

        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} ч {span.Minutes} мин";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} мин {span.Seconds} с";
        return $"{(int)span.TotalSeconds} с";
    }

    private static string Ago(DateTimeOffset moment)
    {
        var local = moment.ToLocalTime();
        var today = DateTimeOffset.Now.Date;

        if (local.Date == today) return "сегодня " + local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return "вчера " + local.ToString("HH:mm");
        return local.ToString("d MMM, HH:mm");
    }

    private async Task EditQuietlyAsync(
        Message message, string html, InlineKeyboardMarkup keyboard, CancellationToken ct)
    {
        try
        {
            await bot.EditMessageText(
                message.Chat.Id, message.MessageId, html, ParseMode.Html,
                replyMarkup: keyboard, cancellationToken: ct);
        }
        catch (ApiRequestException ex)
        {
            // «message is not modified» — нормальный исход: пользователь нажал ту же кнопку.
            logger.LogDebug(ex, "Не удалось перерисовать меню");
        }
    }

    private async Task DeleteQuietlyAsync(Message message, CancellationToken ct)
    {
        try
        {
            await bot.DeleteMessage(message.Chat.Id, message.MessageId, ct);
        }
        catch (ApiRequestException ex)
        {
            logger.LogDebug(ex, "Не удалось закрыть меню");
        }
    }

    private async Task AnswerAsync(string callbackQueryId, string? text, CancellationToken ct)
    {
        try
        {
            await bot.AnswerCallbackQuery(callbackQueryId, text, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось ответить на callback меню");
        }
    }
}
