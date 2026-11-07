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

/// <summary>
/// Вопрос вне сессии (<c>/ask</c>): разовый запуск без проекта и без продолжения. Модель своя,
/// из чата не меняется — вопрос заведён ради дешёвого ответа, и выбор тяжёлой модели для
/// сессий не должен тянуть его за собой. Она же — модель беседы по умолчанию: беседа тоже ищет
/// в сети, но у неё /model и /effort меняют выбор темы.
/// </summary>
public sealed class QuestionOptions
{
    /// <summary>Модель или её алиас; null — как решит агент.</summary>
    public string? Model { get; set; } = "sonnet";

    /// <summary>Уровень усилий; null — как решит агент.</summary>
    public string? Effort { get; set; }
}

/// <summary>
/// Тема «Работа»: агенту доступен весь компьютер, а не одна папка проекта. Выключена, пока список
/// пуст: доступ к боту не должен сам собой означать доступ ко всей машине — снять этот барьер
/// можно только правкой конфига на ней.
/// </summary>
public sealed class WorkOptions
{
    /// <summary>
    /// Кому можно тему «Работа»: id из <c>Channel:Settings:AllowedUserIds</c>. Число — id в канале
    /// шлюза, строка «канал:id» — то же с именем канала (так адрес пишет журнал).
    /// </summary>
    public string[] AllowedUserIds { get; set; } = [];

    /// <summary>
    /// Корень папок тем «Работа»: у каждой темы своя подпапка, и её не чистят. null — папка
    /// <c>AgentsTracker-work</c> в профиле пользователя.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>Тема «Работа» включена в конфиге: в списке есть хоть один id.</summary>
    public bool Enabled => AllowedUserIds.Any(id => !string.IsNullOrWhiteSpace(id));

    /// <summary>Корень папок тем «Работа», нормализованный.</summary>
    public string Root => ProjectCatalog.Normalize(Folder is { Length: > 0 } folder
        ? folder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AgentsTracker-work"));

    /// <summary>
    /// Пересечения корня проверяем, только когда тема включена: тот, кто ею не пользуется, не
    /// должен после обновления упереться в отказ старта из-за папки, о которой не знает. Сам путь —
    /// всегда: тема, выбранная до выключения, осталась в state.json, и кривой путь ронял бы каждый
    /// её экран.
    /// </summary>
    public IReadOnlyList<string> Validate(GatewayOptions options)
    {
        var errors = new List<string>();
        var key = $"{GatewayOptions.SectionName}:Work";

        string root;
        try
        {
            // Относительный путь разрешился бы от текущей папки процесса: у службы это System32.
            if (Folder is { Length: > 0 } folder && !Path.IsPathFullyQualified(folder))
            {
                errors.Add($"{key}:Folder = «{folder}». Нужен полный путь.");
                return errors;
            }

            root = Root;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"{key}:Folder — некорректный путь: {ex.Message}");
            return errors;
        }

        if (!Enabled) return errors;

        if (AllowedUserIds.Any(string.IsNullOrWhiteSpace))
            errors.Add($"{key}:AllowedUserIds — пустой id.");

        // Без профиля (Linux без HOME) корень по умолчанию стал бы папкой внутри текущей.
        if (Folder is not { Length: > 0 } && Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is not { Length: > 0 })
            errors.Add($"{key}:Folder не задан, а папки профиля нет — задайте полный путь.");

        try
        {
            // Там state.json, токен MCP и секреты канала: агент со всеми инструментами в этой папке
            // переписал бы себе правила и доверие.
            var data = ProjectCatalog.Normalize(AppPaths.DataDirectory);
            if (Overlaps(root, data))
                errors.Add($"{key}:Folder ({root}) пересекается с папкой данных шлюза ({data}).");

            // Во временных лежат папки бесед и вопросов, которые шлюз стирает перед запуском, — стёр бы
            // и папку «Работы», а беседа взяла бы верх и запуск ушёл бы без инструментов. Да и чистка
            // временных файлов Windows прошлась бы по заметкам агента.
            var temp = ProjectCatalog.Normalize(Path.GetTempPath());
            if (Overlaps(root, temp))
                errors.Add($"{key}:Folder ({root}) пересекается с временной папкой ({temp}).");

            // Тип запуска шлюз узнаёт и по папке: проект внутри корня «Работы» выпал бы из списка
            // проектов, а папка «Работы» внутри проекта попала бы в его сессии и доверие.
            foreach (var project in new[] { options.ProjectPath }.Concat(options.Projects).Where(p => p is { Length: > 0 }))
            {
                if (Overlaps(root, ProjectCatalog.Normalize(project)))
                    errors.Add($"{key}:Folder ({root}) пересекается с проектом {project}.");
            }

            // Корень обхода внутри корня «Работы»: список проектов молча опустел бы — папки «Работы»
            // из него вычёркиваются. Обратное (корень «Работы» внутри обхода) — обычное дело.
            if (options.ProjectsRoot is { Length: > 0 } projectsRoot
                && ProjectCatalog.IsSameOrInside(ProjectCatalog.Normalize(projectsRoot), root))
                errors.Add($"{key}:Folder ({root}) содержит ProjectsRoot ({projectsRoot}): список проектов был бы пуст.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"{key}: проект из конфига — некорректный путь: {ex.Message}");
        }

        return errors;
    }

    private static bool Overlaps(string left, string right) =>
        ProjectCatalog.IsSameOrInside(left, right) || ProjectCatalog.IsInside(right, left);
}

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Канал связи с человеком: тип и его настройки.</summary>
    public ChannelOptions Channel { get; set; } = new();

    /// <summary>Проект по умолчанию. Из чата его меняет меню «Проект».</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>
    /// Папки, между которыми можно переключаться из чата. Пусто — список собирается обходом
    /// <see cref="ProjectsRoot"/>, а если и он пуст — из соседей <see cref="ProjectPath"/>
    /// (см. ProjectCatalog).
    /// </summary>
    public string[] Projects { get; set; } = [];

    /// <summary>
    /// Корень поиска проектов для меню: обход идёт вглубь, пока не встретится папка,
    /// похожая на проект. Нужен, когда проекты разложены по папкам-группам
    /// (source/repos/ГруппаА/Проект) и соседей <see cref="ProjectPath"/> не хватает.
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
    /// Режим по умолчанию; из чата меняется через /mode. Допустимые значения
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

    /// <summary>Вопрос вне сессии: его модель и effort.</summary>
    public QuestionOptions Question { get; set; } = new();

    /// <summary>Тема «Работа»: кому она доступна и где её папки.</summary>
    public WorkOptions Work { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        errors.AddRange(Attachments.Validate());
        errors.AddRange(Work.Validate(this));

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

        ValidateEffort(capabilities, "Effort", Effort, errors);
        ValidateEffort(capabilities, "Question:Effort", Question.Effort, errors);

        return errors;
    }

    private static void ValidateEffort(AgentCapabilities capabilities, string key, string? effort, List<string> errors)
    {
        if (effort is not { Length: > 0 }) return;

        if (capabilities.Effort is null)
            errors.Add($"{SectionName}:{key} задан, а агент уровень усилий не поддерживает.");
        else if (capabilities.Effort.Resolve(effort) is null)
            errors.Add($"{SectionName}:{key} = '{effort}'. Допустимо: {string.Join(", ", capabilities.Effort.Values)}.");
    }
}
