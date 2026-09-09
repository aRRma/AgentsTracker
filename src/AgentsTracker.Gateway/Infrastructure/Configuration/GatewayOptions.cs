namespace AgentsTracker.Gateway.Infrastructure.Configuration;

/// <summary>
/// Канал связи перед шлюзом. <see cref="Type"/> — ключ модуля; его настройки (токен, кому
/// можно) лежат в <c>Settings</c>, и читает их только сам модуль.
/// </summary>
public sealed class ChannelOptions
{
    public const string DefaultType = "telegram";

    /// <summary>
    /// Ключи, переехавшие из корня Gateway в <c>Channel:Settings</c>. Список один на проверку
    /// при старте и на protect-secrets: разойдись они — шлюз откажется стартовать из-за ключа,
    /// который protect-secrets только что «успешно» обработал.
    /// </summary>
    public static readonly IReadOnlyList<string> MovedKeys = ["BotToken", "AllowedUserIds"];

    public string Type { get; set; } = DefaultType;
}

/// <summary>
/// Картинки, присланные в чат. Шлюз кладёт их в <c>inbox</c> папки данных и открывает агенту
/// доступ только к папке текущего чата.
/// </summary>
public sealed class AttachmentOptions
{
    /// <summary>Принимать ли вложения. false — на картинку приходит тот же отказ, что на видео.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Предел размера скачиваемой картинки. Больше предела канала не поднять: Telegram
    /// отдаёт боту файлы до 20 МБ.
    /// </summary>
    public long MaxBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>
    /// Сколько часов хранить скачанное. Чистка идёт при старте шлюза и перед каждым новым
    /// вложением — фонового таймера ради этого не заводим.
    /// </summary>
    public int RetentionHours { get; set; } = 24;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (MaxBytes < 1)
            errors.Add($"{GatewayOptions.SectionName}:Attachments:MaxBytes = {MaxBytes}. Ожидается размер в байтах.");

        if (RetentionHours is < 1 or > 8760)
            errors.Add($"{GatewayOptions.SectionName}:Attachments:RetentionHours = {RetentionHours}. Допустимо 1..8760 часов.");

        return errors;
    }
}

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Канал связи с человеком: тип и его настройки.</summary>
    public ChannelOptions Channel { get; set; } = new();

    /// <summary>Рабочая папка по умолчанию. Из чата её меняет меню «Репозиторий».</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>
    /// Папки, между которыми можно переключаться из чата. Пусто — список собирается обходом
    /// <see cref="ProjectsRoot"/>, а если и он пуст — из соседей <see cref="ProjectPath"/>
    /// (см. ProjectCatalog).
    /// </summary>
    public string[] Projects { get; set; } = [];

    /// <summary>
    /// Корень поиска репозиториев для меню: обход идёт вглубь, пока не встретится папка,
    /// похожая на проект. Нужен, когда репозитории разложены по группам
    /// (source/repos/ГруппаА/Репозиторий) и соседей <see cref="ProjectPath"/> не хватает.
    /// null — искать среди соседей.
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
    /// Режим разрешений по умолчанию; из чата меняется через /mode. Допустимые значения
    /// объявляет бэкенд (<see cref="AgentCapabilities.PermissionMode"/>), проверка — при
    /// старте, когда бэкенд уже выбран. Задаётся всегда: без флага у Claude Code сработал бы
    /// defaultMode из настроек пользователя, и при "auto" кнопок в чате не будет.
    /// </summary>
    public string PermissionMode { get; set; } = "default";

    /// <summary>Порт локального MCP-сервера подтверждений (слушает только 127.0.0.1).</summary>
    public int McpPort { get; set; } = 5099;

    /// <summary>
    /// Порт веб-монитора (страница состояния и статистики, без авторизации). 0 — монитор
    /// выключен. Адрес задаёт <see cref="MonitorBind"/>.
    /// </summary>
    public int MonitorPort { get; set; } = 5100;

    /// <summary>
    /// Где слушать монитор: <c>loopback</c> — только с этой машины, <c>any</c> — на всех
    /// адресах. <c>any</c> нужен в контейнере: порт на 127.0.0.1 внутри него наружу
    /// не опубликовать. Пароля у монитора нет, поэтому в Docker публикуют как
    /// <c>127.0.0.1:5100:5100</c>. Порт MCP всегда на loopback: его клиент — дочерний
    /// процесс агента в том же окружении.
    /// </summary>
    public string MonitorBind { get; set; } = MonitorBindLoopback;

    public const string MonitorBindLoopback = "loopback";
    public const string MonitorBindAny = "any";

    /// <summary>
    /// Папка данных: state.json, аудит, appsettings.Local.json. null — по умолчанию
    /// (<c>%LOCALAPPDATA%\AgentsTracker</c>, на Linux и macOS <c>~/.local/share</c>).
    /// Локальный конфиг лежит в этой же папке, поэтому ключ читается раньше остального
    /// конфига — только из appsettings.json рядом с exe или из <c>Gateway__DataDirectory</c>.
    /// В контейнере сюда монтируют том.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>Сколько ждать нажатия кнопки, прежде чем автоматически отклонить.</summary>
    public int ApprovalTimeoutMinutes { get; set; } = 15;

    /// <summary>Предельная длительность одного запуска агента.</summary>
    public int RunTimeoutMinutes { get; set; } = 60;

    /// <summary>
    /// HTTP-прокси машины, например "http://127.0.0.1:2080": через него ходят и агент, и канал
    /// (свой прокси канал может задать в своих настройках). null = без прокси.
    /// </summary>
    public string? Proxy { get; set; }

    /// <summary>Приём картинок из чата.</summary>
    public AttachmentOptions Attachments { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        errors.AddRange(Attachments.Validate());

        if (string.IsNullOrWhiteSpace(Channel.Type))
            errors.Add($"{SectionName}:Channel:Type не задан.");

        if (string.IsNullOrWhiteSpace(ProjectPath))
            errors.Add($"{SectionName}:ProjectPath не задан.");
        else if (!Directory.Exists(ProjectPath))
            errors.Add($"{SectionName}:ProjectPath — папки не существует: {ProjectPath}");

        if (McpPort is < 1 or > 65535)
            errors.Add($"{SectionName}:McpPort вне диапазона: {McpPort}");

        if (MonitorPort is < 0 or > 65535)
            errors.Add($"{SectionName}:MonitorPort вне диапазона: {MonitorPort}. 0 выключает монитор.");
        // Конвейер один на оба порта: при совпадении страница монитора открылась бы и на MCP.
        else if (MonitorPort != 0 && MonitorPort == McpPort)
            errors.Add($"{SectionName}:MonitorPort совпадает с McpPort: {MonitorPort}");

        if (!MonitorBind.Equals(MonitorBindLoopback, StringComparison.OrdinalIgnoreCase)
            && !MonitorBind.Equals(MonitorBindAny, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{SectionName}:MonitorBind = «{MonitorBind}». Допустимо {MonitorBindLoopback} или {MonitorBindAny}.");
        }

        // Нуль похож на «без ограничения», а на деле CancellationTokenSource срабатывает
        // сразу и убивает каждый запуск.
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

        // Дерево обходится на каждый показ меню: без потолка глубины корень вроде «C:\»
        // подвесил бы отрисовку.
        if (ProjectsRootDepth is < 1 or > 6)
            errors.Add($"{SectionName}:ProjectsRootDepth = {ProjectsRootDepth}. Допустимо 1..6.");

        // Значение в ошибку не подставляем: в URI прокси бывает user:pass, а ошибка идёт в лог.
        if (Proxy is { Length: > 0 } && !Uri.TryCreate(Proxy, UriKind.Absolute, out _))
            errors.Add($"{SectionName}:Proxy — некорректный URI (ожидается вида http://host:port).");

        return errors;
    }

    /// <summary>
    /// Значения, допустимость которых знает только бэкенд. Отдельно от <see cref="Validate"/>:
    /// тот работает до выбора агента.
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
