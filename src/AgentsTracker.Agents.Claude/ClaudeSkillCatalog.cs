using System.Text.RegularExpressions;

namespace AgentsTracker.Agents.Claude;

/// <summary>
/// Собирает скиллы и команды с диска: перечислить их из <c>-p</c> CLI не умеет, а без
/// списка из чата не видно, что можно запустить. Смотрим туда же, куда и CLI:
/// <c>.claude/skills</c> и <c>.claude/commands</c> проекта и пользователя плюс включённые
/// плагины. Встроенные скиллы на диске не лежат — они из <c>Gateway:Claude:BuiltInSkills</c>.
/// </summary>
public sealed class ClaudeSkillCatalog(IOptions<ClaudeOptions> options, ILogger<ClaudeSkillCatalog> logger) : IAgentSkillCatalog
{
    public const string BuiltInGroup = "Встроенные";

    private const int DescriptionLimit = 120;

    private const int DetailsLimit = 700;

    private const int FlagsLimit = 8;

    /// <summary>Флаг в тексте скилла: <c>--fix</c>, <c>--no-post</c>. Одиночные буквы дают слишком много ложных.</summary>
    private static readonly Regex FlagPattern = new(@"(?<![\w-])--[a-z][a-z0-9-]{1,30}\b", RegexOptions.Compiled);

    private static readonly string ClaudeHome =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// Сколько держать результат обхода. Одно нажатие в меню — это Apply и Render подряд,
    /// то есть два обхода диска с разбором всех SKILL.md; за пять секунд плагины
    /// не меняются.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();

    private readonly ClaudePluginRegistry _plugins = new(logger);

    private (string Project, DateTimeOffset At, IReadOnlyList<SkillGroup> Groups)? _cache;

    /// <summary>Группы в порядке показа: встроенные, проект, личные, потом плагины по алфавиту.</summary>
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
        "Ничего не найдено: ни <code>.claude/skills</code> в проекте и профиле, "
        + "ни включённых плагинов в <code>~/.claude/plugins</code>, ни строк "
        + "в <code>Gateway:Claude:BuiltInSkills</code>.";

    public void Refresh()
    {
        lock (_gate) _cache = null;
    }

    /// <summary>
    /// Все установленные плагины, не только включённые. Без кэша: список нужен экрану
    /// плагинов, а после переключения он обязан быть свежим.
    /// </summary>
    public IReadOnlyList<PluginInfo> Plugins(string projectPath)
    {
        var settings = _plugins.Settings(projectPath);

        return _plugins.List()
            .Select(p =>
            {
                var setting = settings.GetValueOrDefault(p.Key);
                var locked = setting is not null && !ClaudePluginRegistry.IsUserLayer(setting) ? setting.Layer : null;
                return new PluginInfo(p.Key, p.Name, setting?.Enabled ?? true, locked);
            })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Писать в личные настройки нельзя, если слой проекта их перекрывает: кнопка показала
    /// бы «включено», а CLI считал бы плагин выключенным.
    /// </summary>
    public string? SetPluginEnabled(string key, bool enabled, string projectPath)
    {
        if (_plugins.List().All(p => !string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)))
            return "Плагин уже не установлен";

        if (_plugins.Settings(projectPath).GetValueOrDefault(key) is { } setting
            && !ClaudePluginRegistry.IsUserLayer(setting))
            return $"Задано в {setting.Layer} — там и меняйте";

        var error = _plugins.SetEnabled(key, enabled);
        if (error is null) Refresh();
        return error;
    }

    /// <summary>Тот же проект с точностью до регистра и хвостового слэша: в кэше путь как есть.</summary>
    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private List<SkillGroup> Scan(string projectPath)
    {
        var groups = new List<SkillGroup>();

        Add(groups, BuiltInGroup, BuiltIn());

        Add(groups, "Проект", ScanFolder(Path.Combine(projectPath, ".claude"), prefix: null, "Проект"));
        Add(groups, "Личные", ScanFolder(ClaudeHome, prefix: null, "Личные"));

        // Плагин без записи в enabledPlugins включён — так же считает и сам CLI.
        var settings = _plugins.Settings(projectPath);
        foreach (var plugin in _plugins.List().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (settings.GetValueOrDefault(plugin.Key) is { Enabled: false }) continue;
            Add(groups, plugin.Name, ScanFolder(plugin.Path, prefix: plugin.Name, plugin.Name));
        }

        return groups;
    }

    private static void Add(List<SkillGroup> groups, string name, List<SkillInfo> skills)
    {
        if (skills.Count == 0) return;
        skills.Sort((a, b) => string.Compare(a.Command, b.Command, StringComparison.OrdinalIgnoreCase));
        groups.Add(new SkillGroup(name, skills));
    }

    /// <summary>
    /// Строки «/команда | описание | подсказка аргументов» из конфига; третья часть необязательна.
    /// Без слэша в начале строка пропускается с предупреждением.
    /// </summary>
    private List<SkillInfo> BuiltIn()
    {
        var result = new List<SkillInfo>();

        foreach (var line in options.Value.BuiltInSkills)
        {
            var parts = line.Split('|', 3, StringSplitOptions.TrimEntries);
            var command = parts[0];
            var description = parts.Length > 1 ? parts[1] : "";
            var hint = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;

            if (command.Length < 2 || command[0] != '/' || command.Any(char.IsWhiteSpace))
            {
                logger.LogWarning("Gateway:Claude:BuiltInSkills — пропущена строка без команды: {Line}", line);
                continue;
            }

            result.Add(new SkillInfo(
                command, Shorten(description), Text.Clip(description, DetailsLimit), BuiltInGroup, hint,
                Flags(description + " " + hint)));
        }

        return result;
    }

    /// <summary>
    /// <c>skills/*/SKILL.md</c> — ровно один уровень, вложенный SKILL.md в examples не скилл —
    /// и <c>commands/**/*.md</c>. Команда из подпапки зовётся через двоеточие, как у CLI:
    /// <c>commands/db/query.md</c> → <c>/db:query</c>. Одноимённые скилл и команда
    /// не дублируются.
    /// </summary>
    private List<SkillInfo> ScanFolder(string root, string? prefix, string group)
    {
        var result = new List<SkillInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in Enumerate(Path.Combine(root, "skills"), d => Directory.EnumerateDirectories(d)))
        {
            var file = Path.Combine(folder, "SKILL.md");
            if (!File.Exists(file)) continue;

            var name = Path.GetFileName(folder);
            if (seen.Add(name) && Parse(file, name, prefix, group) is { } skill) result.Add(skill);
        }

        var commands = Path.Combine(root, "commands");
        foreach (var file in Enumerate(commands, d => Directory.EnumerateFiles(d, "*.md", SearchOption.AllDirectories)))
        {
            var relative = Path.GetRelativePath(commands, file);
            var name = Path.ChangeExtension(relative, null).Replace(Path.DirectorySeparatorChar, ':').Replace('/', ':');
            if (seen.Add(name) && Parse(file, name, prefix, group) is { } skill) result.Add(skill);
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

    /// <summary>
    /// Читает frontmatter. Скилл с <c>user-invocable: false</c> в список не попадает:
    /// вызвать его из чата нельзя, кнопка вела бы в никуда.
    /// </summary>
    private SkillInfo? Parse(string file, string name, string? prefix, string group)
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
        var command = "/" + (prefix is null ? name : $"{prefix}:{name}");

        // Флаги из тела берём только у скиллов, читающих аргументы ($ARGUMENTS, $1): у прочих
        // «--providers» в тексте — флаг из примера, а не самого скилла.
        var takesArguments = body.Contains("$ARGUMENTS", StringComparison.Ordinal)
                             || body.Contains("$1", StringComparison.Ordinal);

        return new SkillInfo(
            command, Shorten(full), Text.Clip(full, DetailsLimit), group,
            string.IsNullOrWhiteSpace(hint) ? null : hint.Trim(),
            Flags(takesArguments ? body : full + " " + hint));
    }

    /// <summary>
    /// Флаги, упомянутые в тексте: схемы аргументов у скиллов нет, и «--fix» в описании —
    /// единственный намёк. Ложные срабатывания возможны, поэтому в карточке они подписаны
    /// как «упомянутые».
    /// </summary>
    private static IReadOnlyList<string> Flags(string text) =>
        FlagPattern.Matches(text)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(FlagsLimit)
            .ToList();

    /// <summary>
    /// Блок между первыми двумя строками <c>---</c>. Разбор плоский нарочно: нужны только
    /// «ключ: значение» верхнего уровня, вложенных структур в скиллах не бывает.
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

            // Продолжение значения (строка с отступом) склеиваем через пробел: описания
            // часто переносят, и одна первая строка обрывалась бы посреди фразы.
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
        return Text.Clip(text, DescriptionLimit);
    }
}
