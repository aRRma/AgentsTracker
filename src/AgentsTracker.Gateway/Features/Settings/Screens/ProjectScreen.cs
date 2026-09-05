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
    /// <summary>Префиксы callback-ов: не hex, чтобы не спутать с 12-значным ключом.</summary>
    private const string PagePrefix = "p";

    private const string GroupPrefix = "g";

    /// <summary>Возврат от репозиториев к списку папок.</summary>
    private const string UpArgument = "up";

    private readonly ScreenNavigation _nav = new();

    public string Key => "proj";

    public void Open(long userId) => _nav.Reset(userId);

    public string? Apply(string argument, long userId, long chatId)
    {
        if (argument == UpArgument)
        {
            _nav.Reset(userId);
            return null;
        }

        // Папка проверяется при отрисовке: исчезнувшая молча вернёт к списку папок.
        // Проверять здесь — лишний обход диска на каждое нажатие.
        if (argument.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            _nav.Set(userId, new ScreenPosition(Group: argument[GroupPrefix.Length..]));
            return null;
        }

        if (argument.StartsWith(PagePrefix, StringComparison.Ordinal)
            && int.TryParse(argument[PagePrefix.Length..], out var page))
        {
            _nav.Update(userId, p => p with { Page = page });
            return null;
        }

        // Ищем по ключу, а не по номеру в списке: между отрисовкой и нажатием список мог
        // измениться (появился склонированный репозиторий), и номер указал бы на другой путь.
        var project = catalog.List(store.ProjectPath).FirstOrDefault(p => Key12(ProjectCatalog.Normalize(p)) == argument);
        if (project is null) return "Репозитория уже нет в списке";

        var previous = store.ProjectPath;
        if (ProjectCatalog.Same(project, previous)) return null;

        store.SetProjectPath(project);

        // Выбранная папка становится первой в списке — показывать при этом пятую страницу незачем.
        _nav.Update(userId, p => p with { Page = 0 });

        logger.LogInformation("Рабочая папка переключена на {Project}", project);
        audit.Changed(store, userId, "project", Path.GetFileName(previous), Path.GetFileName(project));

        // Сессии живут в папке, где созданы: у нового проекта своя активная сессия
        // или ни одной — предупреждаем, чтобы смена контекста не выглядела потерей истории.
        return store.SessionId is null
            ? $"{Path.GetFileName(project)} — сессия начнётся заново"
            : $"{Path.GetFileName(project)} — вернулись к её сессии";
    }

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(long userId, CancellationToken ct) =>
        Task.FromResult(Render(userId));

    private (string Html, InlineKeyboardMarkup Keyboard) Render(long userId)
    {
        var current = store.ProjectPath;
        var position = _nav.Of(userId);

        // Список пересобирается на каждый показ: папка могла исчезнуть вместе с репозиториями.
        var groups = catalog.Grouped(current);

        // Одна папка — промежуточный экран только добавил бы лишнее нажатие.
        if (groups.Count <= 1)
            return RenderProjects(userId, groups.Count == 0 ? [] : groups[0].Projects, group: null, current, position.Page);

        var opened = position.Group is null ? null : groups.FirstOrDefault(g => Key12(g.Name) == position.Group);

        if (opened is null)
        {
            _nav.Reset(userId);
            return RenderGroups(userId, groups, current, position.Page);
        }

        return RenderProjects(userId, opened.Projects, opened.Name, current, position.Page);
    }

    private (string Html, InlineKeyboardMarkup Keyboard) RenderGroups(
        long userId, IReadOnlyList<ProjectGroup> groups, string current, int pageIndex)
    {
        // Папок-владельцев тоже бывает больше, чем влезает в клавиатуру: под корнем с
        // вложенностью группа — это каждая ветка дерева.
        var (page, clamped, counter, pageRow) = Page(groups, pageIndex, Key, PagePrefix, "папок");
        _nav.Update(userId, p => p with { Page = clamped });

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
                $"📂 {group.Name} ({group.Projects.Count})", $"{Key}:{GroupPrefix}{Key12(group.Name)}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    private (string Html, InlineKeyboardMarkup Keyboard) RenderProjects(
        long userId, IReadOnlyList<string> all, string? group, string current, int pageIndex)
    {
        var (page, clamped, counter, pageRow) = Page(all, pageIndex, Key, PagePrefix, "репозиториев");
        _nav.Update(userId, p => p with { Page = clamped });

        var lines = page.Select(path =>
            $"{Marker(ProjectCatalog.Same(path, current))} <b>{E(Path.GetFileName(path))}</b>\n   <code>{E(path)}</code>");

        var html = $"""
            📁 <b>{E(group ?? "Репозиторий")}</b>

            {string.Join("\n", lines)}

            <i>У каждой папки своя сессия: переключение не смешивает контексты.{(group is null ? $" {Source()}" : "")}</i>{counter}
            """;

        var buttons = page
            .Select(path => Button(
                $"{(ProjectCatalog.Same(path, current) ? "▶ " : "")}{Path.GetFileName(path)}",
                $"{Key}:{Key12(ProjectCatalog.Normalize(path))}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add(group is null ? [BackButton] : [Button("📂 К папкам", $"{Key}:{UpArgument}"), BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    private string Source() => options.Value switch
    {
        { Projects.Length: > 0 } => "Список задан ключом <code>Gateway:Projects</code>.",
        { ProjectsRoot: { Length: > 0 } root } => $"Список собран обходом <code>{E(root)}</code>.",
        _ => "Список собран из соседних папок с <code>.git</code> или решением. "
             + "Задать корень поиска — ключ <code>Gateway:ProjectsRoot</code>.",
    };
}
