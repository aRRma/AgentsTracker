namespace AgentsTracker.Gateway.Infrastructure.Configuration;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Токен бота от @BotFather.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Telegram user id, которым разрешено управлять агентом. Пусто = запрещено всем.</summary>
    public long[] AllowedUserIds { get; set; } = [];

    /// <summary>Рабочая папка по умолчанию. Из чата её меняет меню «Репозиторий».</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>
    /// Папки, между которыми можно переключаться из чата. Пусто — список собирается обходом
    /// <see cref="ProjectsRoot"/>, а если и он пуст — из соседей <see cref="ProjectPath"/>
    /// (см. ProjectCatalog).
    /// </summary>
    public string[] Projects { get; set; } = [];

    /// <summary>
    /// Корень, под которым искать репозитории для меню: обход идёт вглубь, пока не встретится
    /// папка, похожая на проект. Нужен, когда репозитории лежат не одной кучей, а по группам
    /// (source/repos/ГруппаА/Репозиторий): соседей <see cref="ProjectPath"/> тут мало.
    /// null — искать по-старому, среди соседей.
    /// </summary>
    public string? ProjectsRoot { get; set; }

    /// <summary>На сколько уровней вглубь <see cref="ProjectsRoot"/> спускаться.</summary>
    public int ProjectsRootDepth { get; set; } = 3;

    /// <summary>
    /// Встроенные скиллы Claude Code для экрана «Скиллы»: они вшиты в claude.exe, на диске их
    /// нет и перечислить их CLI не умеет. Формат строки — «/команда | описание | подсказка
    /// аргументов» (третья часть необязательна). Список по умолчанию соответствует CLI 2.1.x;
    /// после обновления CLI его можно переопределить здесь, не пересобирая шлюз.
    /// </summary>
    public string[] BuiltInSkills { get; set; } =
    [
        "/code-review | Ревью текущего диффа или PR: баги, упрощения, эффективность. Уровень low/medium — меньше находок, но точнее; high…max — шире. --fix применяет правки после ревью, --comment пишет замечания в PR. | [low|medium|high|xhigh|max] [номер PR|ветка|путь] [--fix] [--comment]",
        "/simplify | Упростить изменённый код: повторное использование, лишние абстракции, эффективность. Правки применяются сразу.",
        "/security-review | Проверка безопасности изменений в текущей ветке.",
        "/init | Создать CLAUDE.md с описанием кодовой базы.",
        "/fewer-permission-prompts | Разобрать историю и разрешить частые безопасные команды в .claude/settings.json, чтобы меньше спрашивать.",
    ];

    /// <summary>Путь к claude.exe. null = автопоиск.</summary>
    public string? ClaudeExecutable { get; set; }

    /// <summary>Алиас модели (sonnet / opus / haiku) или null для модели по умолчанию.</summary>
    public string? Model { get; set; }

    /// <summary>Уровень усилий по умолчанию (low…max) или null — как решит CLI. Из чата меняется меню.</summary>
    public string? Effort { get; set; }

    /// <summary>
    /// Дневной бюджет шлюза в долларах: когда стоимость запусков за сутки его превысит,
    /// новые задачи отклоняются. null — без ограничения. Из чата не меняется: деньгами
    /// шлюз не распоряжается.
    /// </summary>
    public decimal? DailyBudgetUsd { get; set; }

    /// <summary>Предел стоимости одного запуска (уходит в --max-budget-usd). null — без предела.</summary>
    public decimal? RunBudgetUsd { get; set; }

    /// <summary>
    /// Режим разрешений по умолчанию, с которым запускается CLI; из чата его меняет /mode.
    /// Задаётся явно, потому что иначе действует
    /// defaultMode из ~/.claude/settings.json: при "auto" решения принимает классификатор,
    /// кнопки в чате не появляются вовсе. "default" — спрашивать всё, что не разрешено правилами.
    /// </summary>
    public string PermissionMode { get; set; } = "default";

    /// <summary>Порт локального MCP-сервера подтверждений (слушает только 127.0.0.1).</summary>
    public int McpPort { get; set; } = 5099;

    /// <summary>
    /// Порт веб-монитора (страница состояния и статистики, без авторизации, слушает только
    /// 127.0.0.1). 0 — монитор выключен.
    /// </summary>
    public int MonitorPort { get; set; } = 5100;

    /// <summary>Сколько ждать нажатия кнопки, прежде чем автоматически отклонить.</summary>
    public int ApprovalTimeoutMinutes { get; set; } = 15;

    /// <summary>Предельная длительность одного запуска claude.</summary>
    public int RunTimeoutMinutes { get; set; } = 60;

    /// <summary>HTTP-прокси для Telegram API, например "http://127.0.0.1:2080". null = без прокси.</summary>
    public string? Proxy { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(BotToken))
            errors.Add($"{SectionName}:BotToken не задан. Создайте бота у @BotFather и впишите токен в appsettings.Local.json.");

        if (string.IsNullOrWhiteSpace(ProjectPath))
            errors.Add($"{SectionName}:ProjectPath не задан.");
        else if (!Directory.Exists(ProjectPath))
            errors.Add($"{SectionName}:ProjectPath — папки не существует: {ProjectPath}");

        if (AllowedUserIds.Length == 0)
            errors.Add($"{SectionName}:AllowedUserIds пуст. Запустите шлюз, напишите боту — id появится в логе — и впишите его сюда.");

        if (McpPort is < 1 or > 65535)
            errors.Add($"{SectionName}:McpPort вне диапазона: {McpPort}");

        if (MonitorPort is < 0 or > 65535)
            errors.Add($"{SectionName}:MonitorPort вне диапазона: {MonitorPort}. 0 выключает монитор.");
        // Один конвейер на оба порта: совпадение открыло бы страницу монитора и на порту MCP.
        else if (MonitorPort != 0 && MonitorPort == McpPort)
            errors.Add($"{SectionName}:MonitorPort совпадает с McpPort: {MonitorPort}");

        // Нуль тут выглядит как «без ограничения», а на деле CancellationTokenSource
        // с нулевым интервалом срабатывает сразу и убивает каждый запуск.
        if (RunTimeoutMinutes is < 1 or > 1440)
            errors.Add($"{SectionName}:RunTimeoutMinutes = {RunTimeoutMinutes}. Допустимо 1..1440 минут.");

        if (ApprovalTimeoutMinutes is < 1 or > 1440)
            errors.Add($"{SectionName}:ApprovalTimeoutMinutes = {ApprovalTimeoutMinutes}. Допустимо 1..1440 минут.");

        if (!PermissionModes.All.Contains(PermissionMode, StringComparer.Ordinal))
            errors.Add($"{SectionName}:PermissionMode = '{PermissionMode}'. Допустимо: {string.Join(", ", PermissionModes.All)}.");

        if (Effort is { Length: > 0 } effort && EffortLevels.Resolve(effort) is null)
            errors.Add($"{SectionName}:Effort = '{effort}'. Допустимо: {string.Join(", ", EffortLevels.All)}.");

        if (DailyBudgetUsd is <= 0)
            errors.Add($"{SectionName}:DailyBudgetUsd = {DailyBudgetUsd}. Нужна положительная сумма или null.");

        if (RunBudgetUsd is <= 0)
            errors.Add($"{SectionName}:RunBudgetUsd = {RunBudgetUsd}. Нужна положительная сумма или null.");

        foreach (var project in Projects.Where(p => !Directory.Exists(p)))
            errors.Add($"{SectionName}:Projects — папки не существует: {project}");

        if (ProjectsRoot is { Length: > 0 } root && !Directory.Exists(root))
            errors.Add($"{SectionName}:ProjectsRoot — папки не существует: {root}");

        // Обход дерева на каждый показ меню: без потолка глубины один неудачный корень
        // («C:\») подвесил бы отрисовку.
        if (ProjectsRootDepth is < 1 or > 6)
            errors.Add($"{SectionName}:ProjectsRootDepth = {ProjectsRootDepth}. Допустимо 1..6.");

        // Значение в текст ошибки не подставляем: в URI прокси бывает user:pass, а ошибка идёт в лог.
        if (Proxy is { Length: > 0 } && !Uri.TryCreate(Proxy, UriKind.Absolute, out _))
            errors.Add($"{SectionName}:Proxy — некорректный URI (ожидается вида http://host:port).");

        return errors;
    }
}
