
namespace AgentsTracker.Gateway.Infrastructure.Configuration;

/// <summary>
/// Папки, между которыми можно переключаться из чата. Список задаётся ключом Gateway:Projects,
/// а если он пуст — собирается из соседей Gateway:ProjectPath, похожих на репозиторий.
/// Список пересобирается на каждый показ меню: новый склонированный репозиторий появится в нём
/// без перезапуска шлюза.
/// </summary>
public sealed class ProjectCatalog(IOptions<GatewayOptions> options, ILogger<ProjectCatalog> logger)
{
    private readonly GatewayOptions _options = options.Value;

    /// <summary>По этим признакам папка-сосед считается проектом.</summary>
    private static readonly string[] ProjectMarkers = [".git", "*.sln", "*.slnx", "package.json", "pyproject.toml"];

    /// <summary>
    /// Нормализованный путь: он же ключ, по которому в state.json хранятся сессии проекта.
    /// Без него «C:\proj» и «C:/proj/» разъехались бы в разные записи.
    /// </summary>
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool Same(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>Текущая папка всегда первая в списке, дальше — по алфавиту.</summary>
    public IReadOnlyList<string> List(string current)
    {
        var found = new List<string>();

        foreach (var path in _options.Projects.Length > 0 ? _options.Projects : Discover())
        {
            if (!Directory.Exists(path)) continue;

            var normalized = Normalize(path);
            if (!found.Contains(normalized, StringComparer.OrdinalIgnoreCase)) found.Add(normalized);
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);

        // Текущая папка могла быть выбрана до правки конфига — иначе она пропала бы из списка,
        // и вернуться к ней было бы нечем.
        var currentNormalized = Normalize(current);
        found.RemoveAll(p => string.Equals(p, currentNormalized, StringComparison.OrdinalIgnoreCase));
        found.Insert(0, currentNormalized);

        return found;
    }

    private IEnumerable<string> Discover()
    {
        yield return _options.ProjectPath;

        string? parent;
        try
        {
            parent = Directory.GetParent(Normalize(_options.ProjectPath))?.FullName;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось определить папку над {Project}", _options.ProjectPath);
            yield break;
        }

        if (parent is null) yield break;

        string[] neighbours;
        try
        {
            neighbours = Directory.GetDirectories(parent);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось прочитать {Parent}", parent);
            yield break;
        }

        foreach (var neighbour in neighbours)
        {
            if (LooksLikeProject(neighbour)) yield return neighbour;
        }
    }

    private bool LooksLikeProject(string directory)
    {
        try
        {
            return ProjectMarkers.Any(marker => marker.Contains('*')
                ? Directory.EnumerateFiles(directory, marker).Any()
                : Path.Exists(Path.Combine(directory, marker)));
        }
        catch (Exception ex)
        {
            // Недоступная папка — не повод ронять построение списка.
            logger.LogDebug(ex, "Пропускаю {Directory}", directory);
            return false;
        }
    }
}
