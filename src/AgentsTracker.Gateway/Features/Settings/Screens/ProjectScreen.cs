using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Git;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Выбор проекта в два шага: сначала папка-группа, потом проект в ней.
/// У каждого проекта своя активная сессия.
/// </summary>
public sealed class ProjectScreen(
    SessionStore store,
    ProjectCatalog catalog,
    GitRepository git,
    IAuditLog audit,
    IOptions<GatewayOptions> options,
    ILogger<ProjectScreen> logger) : ISettingsScreen
{
    /// <summary>Префиксы callback-ов: не hex, чтобы не спутать с 12-значным ключом.</summary>
    private const string PagePrefix = "p";

    private const string GroupPrefix = "g";

    /// <summary>Возврат от проектов к списку папок.</summary>
    private const string UpArgument = "up";

    private readonly ScreenNavigation _nav = new();

    public string Key => "proj";

    public void Open(ChatId chat) => _nav.Reset(chat);

    public string? Apply(string argument, UserId user, ChatId chat)
    {
        if (argument == UpArgument)
        {
            _nav.Reset(chat);
            return null;
        }

        // Папку проверяет отрисовка: исчезнувшая просто вернёт к списку. Проверять здесь —
        // лишний обход диска на каждое нажатие.
        if (argument.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            _nav.Set(chat, new ScreenPosition(Group: argument[GroupPrefix.Length..]));
            return null;
        }

        if (argument.StartsWith(PagePrefix, StringComparison.Ordinal)
            && int.TryParse(argument[PagePrefix.Length..], out var page))
        {
            _nav.Update(chat, p => p with { Page = page });
            return null;
        }

        var scope = store.For(chat);
        var previous = Current(scope);
        var fromLabel = scope.IsNewTopic ? null : previous is null ? "беседа" : Path.GetFileName(previous);

        // По ключу, а не по номеру: между отрисовкой и нажатием список мог измениться,
        // и номер указал бы на другой путь.
        var project = catalog.List(previous).FirstOrDefault(p => Key12(ProjectCatalog.Normalize(p)) == argument);
        if (project is null) return "Проекта уже нет в списке";

        if (IsCurrent(project, previous)) return null;

        scope.SetProjectPath(project);

        // Выбранная папка становится первой в списке, показывать пятую страницу незачем.
        _nav.Update(chat, p => p with { Page = 0 });

        logger.LogInformation("Проект переключён на {Project}", project);
        audit.Changed(scope, user, "project", fromLabel, Path.GetFileName(project));

        // Сессии живут в папке, где созданы: у нового проекта своя или ни одной.
        // Предупреждаем, чтобы смена контекста не выглядела потерей истории.
        return scope.SessionId is null
            ? $"{Path.GetFileName(project)} — сессия начнётся заново"
            : $"{Path.GetFileName(project)} — вернулись к её сессии";
    }

    public Task<(string Html, Keyboard Keyboard)> RenderAsync(ChatId chat, CancellationToken ct) =>
        Task.FromResult(Render(chat));

    /// <summary>Проект чата; null — беседа: её временная папка в список проектов не попадает.</summary>
    private static string? Current(SessionStore.ChatScope scope) => scope.IsTalk ? null : scope.ProjectPath;

    private static bool IsCurrent(string path, string? current) => current is not null && ProjectCatalog.Same(path, current);

    private (string Html, Keyboard Keyboard) Render(ChatId chat)
    {
        var current = Current(store.For(chat));
        var position = _nav.Of(chat);

        // Пересобираем на каждый показ: папка могла исчезнуть вместе с проектами.
        var groups = catalog.Grouped(current);

        // Папка одна — промежуточный экран только добавит нажатие.
        if (groups.Count <= 1)
            return RenderProjects(chat, groups.Count == 0 ? [] : groups[0].Projects, group: null, current, position.Page);

        var opened = position.Group is null ? null : groups.FirstOrDefault(g => Key12(g.Name) == position.Group);

        if (opened is null)
        {
            _nav.Reset(chat);
            return RenderGroups(chat, groups, current, position.Page);
        }

        return RenderProjects(chat, opened.Projects, opened.Name, current, position.Page);
    }

    private (string Html, Keyboard Keyboard) RenderGroups(
        ChatId chat, IReadOnlyList<ProjectGroup> groups, string? current, int pageIndex)
    {
        // Папок-владельцев тоже бывает больше, чем влезает в клавиатуру: под корнем с
        // вложенностью группа — это каждая ветка дерева.
        var (page, clamped, counter, pageRow) = Page(groups, pageIndex, Key, PagePrefix, "папок");
        _nav.Update(chat, p => p with { Page = clamped });

        var lines = page.Select(group =>
            $"{Marker(group.Projects.Any(p => IsCurrent(p, current)))} "
            + $"<b>{E(group.Name)}</b> — {group.Projects.Count}");

        var now = current is null ? "💬 <b>беседа</b>" : $"<b>{E(Path.GetFileName(current))}</b>\n<code>{E(current)}</code>";

        var html = $"""
            📁 <b>Проект</b>

            Сейчас: {now}

            {string.Join("\n", lines)}

            <i>Выберите папку, потом проект в ней. {Source()}</i>{counter}
            """;

        var buttons = page
            .Select(group => Button(
                $"📂 {group.Name} ({group.Projects.Count})", $"{Key}:{GroupPrefix}{Key12(group.Name)}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add([BackButton]);

        return (html, new Keyboard(buttons));
    }

    private (string Html, Keyboard Keyboard) RenderProjects(
        ChatId chat, IReadOnlyList<string> all, string? group, string? current, int pageIndex)
    {
        var (page, clamped, counter, pageRow) = Page(all, pageIndex, Key, PagePrefix, "проектов");
        _nav.Update(chat, p => p with { Page = clamped });

        var lines = page.Select(path =>
            $"{Marker(IsCurrent(path, current))} <b>{E(Path.GetFileName(path))}</b>{Branch(path)}\n   <code>{E(path)}</code>");

        var html = $"""
            📁 <b>{E(group ?? "Проект")}</b>

            {string.Join("\n", lines)}

            <i>У каждого проекта своя сессия: переключение не смешивает контексты.{(group is null ? $" {Source()}" : "")}</i>{counter}
            """;

        var buttons = page
            .Select(path => Button(
                $"{(IsCurrent(path, current) ? "▶ " : "")}{Path.GetFileName(path)}",
                $"{Key}:{Key12(ProjectCatalog.Normalize(path))}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add(group is null ? [BackButton] : [Button("📂 К папкам", $"{Key}:{UpArgument}"), BackButton]);

        return (html, new Keyboard(buttons));
    }

    /// <summary>
    /// Ветка рядом с именем: worktree лежат соседними папками, и без ветки «AgentsTracker» и
    /// «AgentsTracker-cache» в списке различаются только суффиксом, который выбрал человек.
    /// </summary>
    private string Branch(string path) => git.Head(path) is { } head
        ? $" · {(head.WorktreeOf is null ? "" : "🌳 ")}🌿 {E(head.Label)}"
        : "";

    private string Source() => options.Value switch
    {
        { Projects.Length: > 0 } => "Список задан ключом <code>Gateway:Projects</code>.",
        { ProjectsRoot: { Length: > 0 } root } => $"Список собран обходом <code>{E(root)}</code>.",
        _ => "Список собран из соседних папок с <code>.git</code> или решением. "
             + "Задать корень поиска — ключ <code>Gateway:ProjectsRoot</code>.",
    };
}
