using System.Security.Cryptography;
using System.Text;
using AgentsTracker.Gateway.Infrastructure.Audit;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>Выбор рабочей папки. У каждой папки своя активная сессия.</summary>
public sealed class ProjectScreen(
    SessionStore store,
    ProjectCatalog catalog,
    IAuditLog audit,
    IOptions<GatewayOptions> options,
    ILogger<ProjectScreen> logger) : ISettingsScreen
{
    /// <summary>Сколько папок показывать на странице, чтобы сообщение и клавиатура остались читаемыми.</summary>
    private const int PageSize = 12;

    /// <summary>Префикс callback-а перелистывания. Не пересекается с ключом пути: тот — hex.</summary>
    private const string PagePrefix = "p";

    /// <summary>
    /// Открытая страница. Поле экрана, а не состояние на пользователя: меню одно на шлюз,
    /// как и его сообщение, которое координатор перерисовывает.
    /// </summary>
    private int _page;

    public string Key => "proj";

    public string? Apply(string argument, long userId)
    {
        if (argument.StartsWith(PagePrefix, StringComparison.Ordinal)
            && int.TryParse(argument[PagePrefix.Length..], out var page))
        {
            _page = page;
            return null;
        }

        // Ищем по ключу пути, а не по номеру в списке: между отрисовкой и нажатием список
        // мог измениться (появилась папка-сосед), и номер указал бы на другой проект.
        var project = catalog.List(store.ProjectPath).FirstOrDefault(p => ProjectKey(p) == argument);
        if (project is null) return "Папки уже нет в списке";

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
        var all = catalog.List(current);

        // Список пересобирается на каждый показ: страница могла исчезнуть вместе с папками.
        var pages = Math.Max(1, (all.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);

        var projects = all.Skip(_page * PageSize).Take(PageSize).ToArray();

        var lines = projects.Select(path =>
            $"{Marker(ProjectCatalog.Same(path, current))} <b>{E(Path.GetFileName(path))}</b>\n   <code>{E(path)}</code>");

        var source = options.Value switch
        {
            { Projects.Length: > 0 } => "Список задан ключом <code>Gateway:Projects</code>.",
            { ProjectsRoot: { Length: > 0 } root } => $"Список собран обходом <code>{E(root)}</code>.",
            _ => "Список собран из соседних папок с <code>.git</code> или решением. "
                 + "Задать корень поиска — ключ <code>Gateway:ProjectsRoot</code>.",
        };

        var counter = pages > 1
            ? $"{Environment.NewLine}{Environment.NewLine}Страница {_page + 1} из {pages}, всего папок: {all.Count}."
            : "";

        var html = $"""
            📁 <b>Репозиторий</b>

            {string.Join("\n", lines)}

            <i>У каждой папки своя сессия: переключение не смешивает контексты. {source}</i>{counter}
            """;

        var buttons = projects
            .Select(path => Button(
                $"{(ProjectCatalog.Same(path, current) ? "▶ " : "")}{Path.GetFileName(path)}", $"proj:{ProjectKey(path)}"))
            .Chunk(2)
            .ToList();

        if (pages > 1)
        {
            buttons.Add([
                Button("◀", $"proj:{PagePrefix}{(_page - 1 + pages) % pages}"),
                Button($"{_page + 1}/{pages}", $"proj:{PagePrefix}{_page}"),
                Button("▶", $"proj:{PagePrefix}{(_page + 1) % pages}"),
            ]);
        }

        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    /// <summary>
    /// Короткий ключ пути для callback_data (лимит 64 байта, полный путь не влезает).
    /// Регистр не учитываем — как и ProjectCatalog при сравнении путей.
    /// </summary>
    private static string ProjectKey(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(ProjectCatalog.Normalize(path).ToLowerInvariant());
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
    }
}
