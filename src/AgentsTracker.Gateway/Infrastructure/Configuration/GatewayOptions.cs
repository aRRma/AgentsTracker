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
    /// Какой агент стоит за шлюзом: ключ модуля бэкенда (<c>claude</c>). Настройки самого
    /// агента — в одноимённой подсекции (<c>Gateway:Claude</c>), хост их не читает.
    /// </summary>
    public string Agent { get; set; } = "claude";

    /// <summary>Модель или её алиас по умолчанию; null — как решит агент. Из чата меняется меню.</summary>
    public string? Model { get; set; }

    /// <summary>Уровень усилий по умолчанию; null — как решит агент. Из чата меняется меню.</summary>
    public string? Effort { get; set; }

    /// <summary>
    /// Режим разрешений по умолчанию, с которым запускается агент; из чата его меняет /mode.
    /// Допустимые значения объявляет бэкенд (<see cref="AgentCapabilities.PermissionMode"/>),
    /// проверка — при старте, когда бэкенд уже выбран. Задаётся явно: у Claude Code без флага
    /// действовал бы defaultMode из настроек пользователя, и при "auto" кнопки в чате
    /// не появлялись бы вовсе.
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

    /// <summary>Предельная длительность одного запуска агента.</summary>
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

        if (string.IsNullOrWhiteSpace(Agent))
            errors.Add($"{SectionName}:Agent не задан.");

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

    /// <summary>
    /// Проверка значений, допустимость которых знает только бэкенд. Отдельно от
    /// <see cref="Validate"/>: тот работает до выбора агента, а этот — когда агент уже есть.
    /// </summary>
    public IReadOnlyList<string> ValidateFor(AgentCapabilities capabilities)
    {
        var errors = new List<string>();

        if (!capabilities.PermissionMode.IsValid(PermissionMode))
            errors.Add($"{SectionName}:PermissionMode = '{PermissionMode}'. Допустимо: {string.Join(", ", capabilities.PermissionMode.Values)}.");

        if (Effort is { Length: > 0 } effort)
        {
            if (capabilities.Effort is null)
                errors.Add($"{SectionName}:Effort задан, а агент уровень усилий не поддерживает.");
            else if (capabilities.Effort.Resolve(effort) is null)
                errors.Add($"{SectionName}:Effort = '{effort}'. Допустимо: {string.Join(", ", capabilities.Effort.Values)}.");
        }

        return errors;
    }
}
