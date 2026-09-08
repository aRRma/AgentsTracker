using System.Text.RegularExpressions;

namespace AgentsTracker.Agents.Cursor;

/// <summary>
/// Скиллы с диска: перечислить их из ACP CLI не умеет. Смотрим туда же, куда Cursor:
/// <c>.cursor/skills</c> и <c>.agents/skills</c> проекта и профиля.
/// </summary>
public sealed class CursorSkillCatalog(ILogger<CursorSkillCatalog> logger) : IAgentSkillCatalog
{
    private const int DescriptionLimit = 120;
    private const int DetailsLimit = 700;
    private const int FlagsLimit = 8;

    private static readonly Regex FlagPattern = new(@"(?<![\w-])--[a-z][a-z0-9-]{1,30}\b", RegexOptions.Compiled);

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();

    private (string Project, DateTimeOffset At, IReadOnlyList<SkillGroup> Groups)? _cache;

    public IReadOnlyList<SkillGroup> Grouped(string projectPath)
    {
        lock (_gate)
        {
            if (_cache is { } cached
                && SamePath(cached.Project, projectPath)
                && DateTimeOffset.UtcNow - cached.At < CacheFor)
                return cached.Groups;

            var groups = Scan(projectPath);
            _cache = (projectPath, DateTimeOffset.UtcNow, groups);
            return groups;
        }
    }

    public string EmptyHint =>
        "Ничего не найдено: положите <code>SKILL.md</code> в <code>.cursor/skills/имя</code> "
        + "проекта или в <code>~/.cursor/skills</code>.";

    public void Refresh()
    {
        lock (_gate) _cache = null;
    }

    public IReadOnlyList<PluginInfo> Plugins(string projectPath) => [];

    public string? SetPluginEnabled(string key, bool enabled, string projectPath) =>
        "У Cursor из чата плагины не переключаются";

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private List<SkillGroup> Scan(string projectPath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var groups = new List<SkillGroup>();

        Add(groups, "Проект",
            ScanRoot(Path.Combine(projectPath, ".cursor"), "Проект"),
            ScanRoot(Path.Combine(projectPath, ".agents"), "Проект"));
        Add(groups, "Личные",
            ScanRoot(Path.Combine(home, ".cursor"), "Личные"),
            ScanRoot(Path.Combine(home, ".agents"), "Личные"));

        return groups;
    }

    private static void Add(List<SkillGroup> groups, string name, params List<SkillInfo>[] parts)
    {
        // Одно имя в .cursor/skills и .agents/skills иначе дало бы две кнопки на одну команду.
        var skills = parts.SelectMany(p => p)
            .GroupBy(s => s.Command, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (skills.Count == 0) return;
        skills.Sort((a, b) => string.Compare(a.Command, b.Command, StringComparison.OrdinalIgnoreCase));
        groups.Add(new SkillGroup(name, skills));
    }

    private List<SkillInfo> ScanRoot(string root, string group)
    {
        var result = new List<SkillInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skills = Path.Combine(root, "skills");

        foreach (var folder in Enumerate(skills, Directory.EnumerateDirectories))
        {
            var file = Path.Combine(folder, "SKILL.md");
            if (!File.Exists(file)) continue;

            var name = Path.GetFileName(folder);
            if (seen.Add(name) && Parse(file, name, group) is { } skill) result.Add(skill);
        }

        return result;
    }

    private IEnumerable<string> Enumerate(string folder, Func<string, IEnumerable<string>> list)
    {
        if (!Directory.Exists(folder)) return [];

        try
        {
            return list(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Не удалось обойти {Folder}", folder);
            return [];
        }
    }

    private SkillInfo? Parse(string file, string name, string group)
    {
        Dictionary<string, string> front;
        string body;
        try
        {
            body = File.ReadAllText(file);
            front = Frontmatter(body.Split('\n'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Не удалось прочитать {File}", file);
            return null;
        }

        if (front.GetValueOrDefault("user-invocable") is { } invocable
            && invocable.Equals("false", StringComparison.OrdinalIgnoreCase))
            return null;

        var full = front.GetValueOrDefault("description") ?? "";
        var hint = front.GetValueOrDefault("argument-hint");
        var takesArguments = body.Contains("$ARGUMENTS", StringComparison.Ordinal)
                             || body.Contains("$1", StringComparison.Ordinal);

        return new SkillInfo(
            "/" + name, Shorten(full), Text.Clip(full, DetailsLimit), group,
            string.IsNullOrWhiteSpace(hint) ? null : hint.Trim(),
            Flags(takesArguments ? body : full + " " + hint));
    }

    private static IReadOnlyList<string> Flags(string text) =>
        FlagPattern.Matches(text)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(FlagsLimit)
            .ToList();

    private static Dictionary<string, string> Frontmatter(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inside = false;
        string? lastKey = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line == "---")
            {
                if (inside) break;
                inside = true;
                continue;
            }

            if (!inside) continue;

            if (line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                if (lastKey is not null) result[lastKey] = (result[lastKey] + " " + line.Trim()).Trim();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            lastKey = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value is ">" or "|" or ">-" or "|-") value = "";
            result[lastKey] = Unquote(value);
        }

        return result;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')
            ? value[1..^1]
            : value;

    private static string Shorten(string description)
    {
        var text = description.Trim();
        var stop = text.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 0) text = text[..(stop + 1)];
        return Text.Clip(text, DescriptionLimit);
    }
}
