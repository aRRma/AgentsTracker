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
    /// <summary>Сколько папок показывать, чтобы сообщение и клавиатура остались читаемыми.</summary>
    private const int MaxShown = 12;

    public string Key => "proj";

    public string? Apply(string argument, long userId)
    {
        // Ищем по ключу пути, а не по номеру в списке: между отрисовкой и нажатием список
        // мог измениться (появилась папка-сосед), и номер указал бы на другой проект.
        var project = catalog.List(store.ProjectPath).FirstOrDefault(p => ProjectKey(p) == argument);
        if (project is null) return "Папки уже нет в списке";

        var previous = store.ProjectPath;
        if (ProjectCatalog.Same(project, previous)) return null;

        store.SetProjectPath(project);
        logger.LogInformation("Рабочая папка переключена на {Project}", project);
        audit.Changed(store, userId, "project", Path.GetFileName(previous), Path.GetFileName(project));

        // Сессии живут в папке, где созданы: у нового проекта своя активная сессия
        // или ни одной — предупреждаем, чтобы смена контекста не выглядела потерей истории.
        return store.SessionId is null
            ? $"{Path.GetFileName(project)} — сессия начнётся заново"
            : $"{Path.GetFileName(project)} — вернулись к её сессии";
    }

    public (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        var current = store.ProjectPath;
        var projects = catalog.List(current).Take(MaxShown).ToArray();

        var lines = projects.Select(path =>
            $"{Marker(ProjectCatalog.Same(path, current))} <b>{E(Path.GetFileName(path))}</b>\n   <code>{E(path)}</code>");

        var source = options.Value.Projects.Length > 0
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
