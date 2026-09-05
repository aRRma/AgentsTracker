using System.Text.Json;

namespace AgentsTracker.Gateway.Infrastructure.Claude;

/// <summary>Один скилл (или пользовательская команда) Claude Code: что набрать и что оно делает.</summary>
/// <param name="Command">Полная слэш-команда: <c>/name</c> или <c>/plugin:name</c>.</param>
/// <param name="Description">Первая строка описания из frontmatter, обрезанная для чата.</param>
/// <param name="Group">Откуда скилл: «Проект», «Личные» или имя плагина.</param>
/// <param name="ArgumentHint">Подсказка по аргументам из frontmatter; null — аргументы не нужны.</param>
public sealed record SkillInfo(string Command, string Description, string Group, string? ArgumentHint);

/// <summary>Группа скиллов с общим источником: папка проекта, личная папка или плагин.</summary>
public sealed record SkillGroup(string Name, IReadOnlyList<SkillInfo> Skills);

/// <summary>
/// Собирает скиллы и команды Claude Code с диска: у CLI нет способа их перечислить
/// из <c>-p</c>, а без списка из чата не видно, что вообще можно запустить.
/// Смотрит туда же, куда сам CLI: <c>.claude/skills</c> и <c>.claude/commands</c> проекта
/// и пользователя, плюс включённые плагины из <c>~/.claude/plugins</c>.
/// Встроенные скиллы CLI (<c>/code-review</c> и подобные) на диске не лежат — они берутся
/// из <c>Gateway:BuiltInSkills</c>.
/// </summary>
public sealed class SkillCatalog(IOptions<GatewayOptions> options, ILogger<SkillCatalog> logger)
{
    public const string BuiltInGroup = "Встроенные";

    private const int DescriptionLimit = 120;

    private static readonly string ClaudeHome =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>Группы в порядке показа: встроенные, проект, личные, потом плагины по алфавиту.</summary>
    public IReadOnlyList<SkillGroup> Grouped(string projectPath)
    {
        var groups = new List<SkillGroup>();

        Add(groups, BuiltInGroup, BuiltIn());

        Add(groups, "Проект", ScanFolder(Path.Combine(projectPath, ".claude"), prefix: null, "Проект"));
        Add(groups, "Личные", ScanFolder(ClaudeHome, prefix: null, "Личные"));

        foreach (var (plugin, path) in EnabledPlugins(projectPath).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            Add(groups, plugin, ScanFolder(path, prefix: plugin, plugin));

        return groups;
    }

    private static void Add(List<SkillGroup> groups, string name, List<SkillInfo> skills)
    {
        if (skills.Count == 0) return;
        skills.Sort((a, b) => string.Compare(a.Command, b.Command, StringComparison.OrdinalIgnoreCase));
        groups.Add(new SkillGroup(name, skills));
    }

    /// <summary>Строки «/команда | описание» из конфига; без слэша и пустые молча пропускаются.</summary>
    private List<SkillInfo> BuiltIn()
    {
        var result = new List<SkillInfo>();

        foreach (var line in options.Value.BuiltInSkills)
        {
            var bar = line.IndexOf('|');
            var command = (bar < 0 ? line : line[..bar]).Trim();
            var description = bar < 0 ? "" : line[(bar + 1)..].Trim();

            if (command.Length < 2 || command[0] != '/' || command.Any(char.IsWhiteSpace))
            {
                logger.LogWarning("Gateway:BuiltInSkills — пропущена строка без команды: {Line}", line);
                continue;
            }

            result.Add(new SkillInfo(command, Shorten(description), BuiltInGroup, null));
        }

        return result;
    }

    /// <summary>
    /// <c>skills/*/SKILL.md</c> и <c>commands/**/*.md</c> под одной папкой. Команда с тем же
    /// именем, что и скилл, не дублируется: CLI тоже показывает её один раз.
    /// </summary>
    private List<SkillInfo> ScanFolder(string root, string? prefix, string group)
    {
        var result = new List<SkillInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Enumerate(Path.Combine(root, "skills"), "SKILL.md", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(Path.GetDirectoryName(file)!);
            if (seen.Add(name) && Parse(file, name, prefix, group) is { } skill) result.Add(skill);
        }

        foreach (var file in Enumerate(Path.Combine(root, "commands"), "*.md", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (seen.Add(name) && Parse(file, name, prefix, group) is { } skill) result.Add(skill);
        }

        return result;
    }

    private IEnumerable<string> Enumerate(string folder, string pattern, SearchOption option)
    {
        if (!Directory.Exists(folder)) return [];

        try
        {
            return Directory.EnumerateFiles(folder, pattern, option).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Не удалось обойти {Folder}", folder);
            return [];
        }
    }

    /// <summary>
    /// Читает frontmatter. Скилл с <c>user-invocable: false</c> из чата не вызвать — его нет
    /// в списке, иначе кнопка вела бы в никуда.
    /// </summary>
    private SkillInfo? Parse(string file, string name, string? prefix, string group)
    {
        Dictionary<string, string> front;
        try
        {
            front = Frontmatter(File.ReadLines(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Не удалось прочитать {File}", file);
            return null;
        }

        if (front.GetValueOrDefault("user-invocable") is { } invocable
            && invocable.Equals("false", StringComparison.OrdinalIgnoreCase))
            return null;

        var description = Shorten(front.GetValueOrDefault("description") ?? "");
        var hint = front.GetValueOrDefault("argument-hint");
        var command = "/" + (prefix is null ? name : $"{prefix}:{name}");

        return new SkillInfo(command, description, group, string.IsNullOrWhiteSpace(hint) ? null : hint.Trim());
    }

    /// <summary>
    /// Блок между первыми двумя строками <c>---</c>. Разбор нарочно плоский: нужны только
    /// «ключ: значение» верхнего уровня; вложенные структуры и списки в скиллах не встречаются.
    /// </summary>
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

            // Продолжение многострочного значения (отступ) склеивается через пробел:
            // описание часто переносят по словам, и одна первая строка обрывалась бы посреди фразы.
            if (line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                if (lastKey is not null) result[lastKey] = (result[lastKey] + " " + line.Trim()).Trim();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            lastKey = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            // Маркеры блочного скаляра YAML — значение на следующих строках.
            if (value is ">" or "|" or ">-" or "|-") value = "";

            result[lastKey] = Unquote(value);
        }

        return result;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')
            ? value[1..^1]
            : value;

    /// <summary>До первого предложения и не длиннее лимита: описания плагинов бывают на абзац.</summary>
    private static string Shorten(string description)
    {
        var text = description.Trim();
        var stop = text.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 0) text = text[..(stop + 1)];
        return text.Length <= DescriptionLimit ? text : text[..(DescriptionLimit - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Плагины из <c>installed_plugins.json</c>, у которых нет <c>false</c> в
    /// <c>enabledPlugins</c>. Настройки наслаиваются как у CLI: личные → проекта → локальные проекта.
    /// Установленный плагин без записи считается включённым — так ведёт себя и сам CLI.
    /// </summary>
    private IEnumerable<(string Name, string Path)> EnabledPlugins(string projectPath)
    {
        var enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var settings in new[]
                 {
                     Path.Combine(ClaudeHome, "settings.json"),
                     Path.Combine(projectPath, ".claude", "settings.json"),
                     Path.Combine(projectPath, ".claude", "settings.local.json"),
                 })
        {
            ReadEnabled(settings, enabled);
        }

        var installed = Path.Combine(ClaudeHome, "plugins", "installed_plugins.json");
        if (!File.Exists(installed)) yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(installed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(ex, "Не удалось прочитать {File}", installed);
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("plugins", out var plugins)
                || plugins.ValueKind != JsonValueKind.Object)
                yield break;

            foreach (var plugin in plugins.EnumerateObject())
            {
                // Ключ — «имя@маркетплейс»; в команде используется только имя.
                var key = plugin.Name;
                if (enabled.GetValueOrDefault(key, true) is false) continue;

                var at = key.IndexOf('@');
                var name = at < 0 ? key : key[..at];

                var entry = plugin.Value.ValueKind == JsonValueKind.Array
                    ? plugin.Value.EnumerateArray().FirstOrDefault()
                    : plugin.Value;

                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("installPath", out var pathElement)
                    && pathElement.ValueKind == JsonValueKind.String
                    && pathElement.GetString() is { Length: > 0 } path
                    && Directory.Exists(path))
                    yield return (name, path);
            }
        }
    }

    private void ReadEnabled(string file, Dictionary<string, bool> into)
    {
        if (!File.Exists(file)) return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("enabledPlugins", out var section)
                || section.ValueKind != JsonValueKind.Object)
                return;

            foreach (var item in section.EnumerateObject())
            {
                if (item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    into[item.Name] = item.Value.GetBoolean();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(ex, "Не удалось прочитать {File}", file);
        }
    }
}
