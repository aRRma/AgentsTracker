using System.Security.Cryptography;
using System.Text;
using AgentsTracker.Gateway.Infrastructure.Audit;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Выбор рабочей папки в два шага: сначала папка-группа, потом репозиторий в ней.
/// У каждого репозитория своя активная сессия.
/// </summary>
public sealed class ProjectScreen(
    SessionStore store,
    ProjectCatalog catalog,
    IAuditLog audit,
    IOptions<GatewayOptions> options,
    ILogger<ProjectScreen> logger) : ISettingsScreen
{
    /// <summary>Сколько строк показывать на странице, чтобы сообщение и клавиатура остались читаемыми.</summary>
    private const int PageSize = 12;

    /// <summary>Префикс callback-а перелистывания. Не пересекается с ключами: те — hex.</summary>
    private const string PagePrefix = "p";

    /// <summary>Префикс callback-а открытия папки. Тоже не hex.</summary>
    private const string GroupPrefix = "g";

    /// <summary>Возврат от репозиториев к списку папок.</summary>
    private const string UpArgument = "up";

    /// <summary>
    /// Ключ открытой папки и страница. Поля экрана, а не состояние на пользователя: меню одно
    /// на шлюз, как и его сообщение, которое координатор перерисовывает. Хранится ключ, а не имя:
    /// по нему же ищет и нажатие, и отрисовка — два способа поиска разошлись бы на редких именах.
    /// </summary>
    private string? _group;

    private int _page;

    public string Key => "proj";

    public string? Apply(string argument, long userId, long chatId)
    {
        if (argument == UpArgument)
        {
            _group = null;
            _page = 0;
            return null;
        }

        // Папка проверяется при отрисовке: исчезнувшая молча вернёт к списку папок.
        // Проверять здесь — лишний обход диска на каждое нажатие.
        if (argument.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            _group = argument[GroupPrefix.Length..];
            _page = 0;
            return null;
        }

        if (argument.StartsWith(PagePrefix, StringComparison.Ordinal)
            && int.TryParse(argument[PagePrefix.Length..], out var page))
        {
            _page = page;
            return null;
        }

        // Ищем по ключу, а не по номеру в списке: между отрисовкой и нажатием список мог
        // измениться (появился склонированный репозиторий), и номер указал бы на другой путь.
        var project = catalog.List(store.ProjectPath).FirstOrDefault(p => ProjectKey(p) == argument);
        if (project is null) return "Репозитория уже нет в списке";

        var previous = store.ProjectPath;
        if (ProjectCatalog.Same(project, previous)) return null;

        store.SetProjectPath(project);

        // Выбранная папка становится первой в списке — показывать при этом пятую страницу незачем.
        _page = 0;

        logger.LogInformation("Рабочая папка переключена на {Project}", project);
        audit.Changed(store, userId, "project", Path.GetFileName(previous), Path.GetFileName(project));

        // Сессии живут в папке, где созданы: у нового проекта своя активная сессия
        // или ни одной — предупреждаем, чтобы смена контекста не выглядела потерей истории.
        return store.SessionId is null
            ? $"{Path.GetFileName(project)} — сессия начнётся заново"
            : $"{Path.GetFileName(project)} — вернулись к её сессии";
    }

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(CancellationToken ct) =>
        Task.FromResult(Render());

    private (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        var current = store.ProjectPath;

        // Список пересобирается на каждый показ: папка могла исчезнуть вместе с репозиториями.
        var groups = catalog.Grouped(current);

        // Одна папка — промежуточный экран только добавил бы лишнее нажатие.
        if (groups.Count <= 1)
            return RenderProjects(groups.Count == 0 ? [] : groups[0].Projects, group: null, current);

        var opened = _group is null ? null : groups.FirstOrDefault(g => GroupKey(g.Name) == _group);

        if (opened is null)
        {
            _group = null;
            return RenderGroups(groups, current);
        }

        return RenderProjects(opened.Projects, opened.Name, current);
    }

    private (string Html, InlineKeyboardMarkup Keyboard) RenderGroups(
        IReadOnlyList<ProjectGroup> groups, string current)
    {
        // Папок-владельцев тоже бывает больше, чем влезает в клавиатуру: под корнем с
        // вложенностью группа — это каждая ветка дерева.
        var (page, counter, pageRow) = Page(groups, "папок");

        var lines = page.Select(group =>
            $"{Marker(group.Projects.Any(p => ProjectCatalog.Same(p, current)))} "
            + $"<b>{E(group.Name)}</b> — {group.Projects.Count}");

        var html = $"""
            📁 <b>Репозиторий</b>

            Сейчас: <b>{E(Path.GetFileName(current))}</b>
            <code>{E(current)}</code>

            {string.Join("\n", lines)}

            <i>Выберите папку, потом репозиторий в ней. {Source()}</i>{counter}
            """;

        var buttons = page
            .Select(group => Button(
                $"📂 {group.Name} ({group.Projects.Count})", $"proj:{GroupPrefix}{GroupKey(group.Name)}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    private (string Html, InlineKeyboardMarkup Keyboard) RenderProjects(
        IReadOnlyList<string> all, string? group, string current)
    {
        var (page, counter, pageRow) = Page(all, "репозиториев");

        var lines = page.Select(path =>
            $"{Marker(ProjectCatalog.Same(path, current))} <b>{E(Path.GetFileName(path))}</b>\n   <code>{E(path)}</code>");

        var html = $"""
            📁 <b>{E(group ?? "Репозиторий")}</b>

            {string.Join("\n", lines)}

            <i>У каждой папки своя сессия: переключение не смешивает контексты.{(group is null ? $" {Source()}" : "")}</i>{counter}
            """;

        var buttons = page
            .Select(path => Button(
                $"{(ProjectCatalog.Same(path, current) ? "▶ " : "")}{Path.GetFileName(path)}", $"proj:{ProjectKey(path)}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add(group is null ? [BackButton] : [Button("📂 К папкам", $"proj:{UpArgument}"), BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    /// <summary>
    /// Текущая страница списка, счётчик для текста и ряд кнопок перелистывания (нет — если
    /// страница одна). Страница зажимается в границы: список мог укоротиться после отрисовки.
    /// </summary>
    private (T[] Items, string Counter, InlineKeyboardButton[]? PageRow) Page<T>(IReadOnlyList<T> all, string noun)
    {
        var pages = Math.Max(1, (all.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);

        var items = all.Skip(_page * PageSize).Take(PageSize).ToArray();
        if (pages == 1) return (items, "", null);

        var counter = $"{Environment.NewLine}{Environment.NewLine}Страница {_page + 1} из {pages}, всего {noun}: {all.Count}.";

        InlineKeyboardButton[] row =
        [
            Button("◀", $"proj:{PagePrefix}{(_page - 1 + pages) % pages}"),
            Button($"{_page + 1}/{pages}", $"proj:{PagePrefix}{_page}"),
            Button("▶", $"proj:{PagePrefix}{(_page + 1) % pages}"),
        ];

        return (items, counter, row);
    }

    private string Source() => options.Value switch
    {
        { Projects.Length: > 0 } => "Список задан ключом <code>Gateway:Projects</code>.",
        { ProjectsRoot: { Length: > 0 } root } => $"Список собран обходом <code>{E(root)}</code>.",
        _ => "Список собран из соседних папок с <code>.git</code> или решением. "
             + "Задать корень поиска — ключ <code>Gateway:ProjectsRoot</code>.",
    };

    /// <summary>
    /// Короткий ключ пути для callback_data (лимит 64 байта, полный путь не влезает).
    /// Регистр не учитываем — как и ProjectCatalog при сравнении путей.
    /// </summary>
    private static string ProjectKey(string path) => Key12(ProjectCatalog.Normalize(path));

    /// <summary>Такой же ключ для имени папки: в нём бывает кириллица и разделитель пути.</summary>
    private static string GroupKey(string group) => Key12(group);

    private static string Key12(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..12];
}
