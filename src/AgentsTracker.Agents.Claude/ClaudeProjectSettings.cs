using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentsTracker.Agents.Claude;

/// <summary>
/// Настройки агента в проекте глазами Claude Code: что он выполнит сам, как только запуск
/// начнётся в этой папке. <c>claude -p</c> не показывает диалог доверия к папке, поэтому
/// <c>.claude/settings.json</c> чужого репозитория с хуком <c>SessionStart</c> выполняется
/// без единой карточки — проверено на CLI 2.1.292.
/// </summary>
internal static class ClaudeProjectSettings
{
    private const string LocalSettings = ".claude/settings.local.json";

    /// <summary>Файлы, которые CLI читает из проекта и по которым выполняет команды.</summary>
    private static readonly string[] Files = [".claude/settings.json", LocalSettings, ".mcp.json"];

    /// <summary>
    /// Папки, содержимое которых агент запускает или которыми расширяет свои права: скилл
    /// несёт <c>allowed-tools</c> и свои хуки, хук из настроек обычно вызывает скрипт из
    /// <c>.claude/hooks</c>. Не вся <c>.claude</c>: там же живут worktree с целыми копиями проекта.
    /// </summary>
    private static readonly string[] Folders = [".claude/skills", ".claude/commands", ".claude/agents", ".claude/hooks"];

    /// <summary>Ключи настроек, значение которых — команда оболочки.</summary>
    private static readonly string[] CommandKeys = ["apiKeyHelper", "awsAuthRefresh", "awsCredentialExport", "otelHeadersHelper"];

    /// <summary>
    /// Что рождается само при работе скриптов, а не приходит с репозиторием: без исключения
    /// каждый запуск Python-хука менял бы отпечаток, и доверие спрашивалось бы заново всякий раз.
    /// </summary>
    private static readonly string[] ArtifactFolders = ["__pycache__", "node_modules"];

    private static readonly string[] ArtifactExtensions = [".pyc", ".log"];

    private const int MaxFilesPerFolder = 2000;

    /// <summary>
    /// Файл из папок больше этого — в отпечаток размером и временем: читать мегабайты ради
    /// скилла незачем. Файлы настроек читаются всегда: раздутый пробелами settings.json иначе
    /// прятал бы хук от разбора.
    /// </summary>
    private const long MaxHashedBytes = 1024 * 1024;

    private const long MaxSettingsBytes = 16 * 1024 * 1024;
    // Длинный пункт карточки не режем до безобидного начала: хвост команды иначе прятался бы за «…».
    private const int ItemLength = 400;

    /// <summary>Сколько правил allow показать поимённо: дальше они сворачиваются в одну строку.</summary>
    private const int RulesListed = 3;

    public static ProjectSettings? Inspect(string projectPath)
    {
        var found = new List<(string Relative, string Full)>();
        var dangerous = new List<string>();
        var rules = new List<string>();
        var other = new List<string>();

        foreach (var relative in Files)
        {
            var full = Path.Combine(projectPath, relative);
            if (File.Exists(full)) found.Add((relative, full));
        }

        foreach (var folder in Folders)
        {
            var walk = Walk(Path.Combine(projectPath, folder));
            foreach (var full in walk.Files) found.Add((Relative(projectPath, full), full));

            if (walk.Files.Count > 0) other.Add($"{FolderCaption(folder)} (файлов: {walk.Files.Count})");
            if (walk.Truncated) other.Add($"{FolderCaption(folder)}: файлов больше {MaxFilesPerFolder} — отпечаток по первым");

            // Ссылка ведёт за пределы проверки: содержимое цели не разобрано и в отпечаток не вошло.
            foreach (var link in walk.LinkedFolders)
            {
                var relative = Relative(projectPath, link);
                found.Add((relative, link));
                dangerous.Add($"{relative}: ссылка на другую папку — не проверена, посмотрите вручную");
            }
        }

        if (found.Count == 0) return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var (relative, full) in found.OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            var settings = Files.Contains(relative, StringComparer.Ordinal);

            try
            {
                if (Directory.Exists(full))
                {
                    hash.AppendData(Encoding.UTF8.GetBytes($"link:{new DirectoryInfo(full).LinkTarget}\0"));
                    continue;
                }

                var info = new FileInfo(full);
                if (info.Length > (settings ? MaxSettingsBytes : MaxHashedBytes))
                {
                    hash.AppendData(Encoding.UTF8.GetBytes($"{info.Length}:{info.LastWriteTimeUtc.Ticks}\0"));
                    if (settings) dangerous.Add($"{relative}: больше {MaxSettingsBytes / 1024 / 1024} МБ — не разобран, проверьте вручную");
                    continue;
                }

                // BOM (Блокнот, PowerShell 5) ломает разбор: хуки не попали бы в список карточки,
                // а правила «всегда» — мимо отпечатка. Снимаем сразу, для обоих.
                var content = File.ReadAllBytes(full);
                if (content.AsSpan().StartsWith<byte>([0xEF, 0xBB, 0xBF])) content = content[3..];

                hash.AppendData(relative == LocalSettings ? WithoutAllowRules(content) : content);
                if (settings) Describe(relative, content, dangerous, rules, other);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Нечитаемый файл не повод пропустить проверку: агент-то его, возможно, прочтёт.
                hash.AppendData(Encoding.UTF8.GetBytes("unreadable\0"));
                dangerous.Add($"{relative}: не прочитан ({ex.Message}) — проверьте вручную");
            }
        }

        // Опасное первым: карточка показывает начало списка, и хук не должен уйти в «и ещё N»
        // за дюжиной безобидных правил.
        var items = new List<string>(dangerous);
        items.AddRange(rules.Count <= RulesListed
            ? rules.Select(r => $"правило «всегда»: {r}")
            : [$"правила «всегда» ({rules.Count}): {string.Join(", ", rules)}"]);
        items.AddRange(other);

        // Файл есть, а исполняемого в нём ничего: всё равно спрашиваем — список ключей CLI растёт
        // от версии к версии, и «ничего опасного» по сегодняшнему списку завтра может стать неправдой.
        if (items.Count == 0) items.AddRange(found.Select(f => $"{f.Relative}: опасных ключей не найдено"));

        return new ProjectSettings(Convert.ToHexStringLower(hash.GetHashAndReset())[..16], [.. items.Select(i => Text.ClipRedacted(i, ItemLength))]);
    }

    private static string Relative(string projectPath, string full) =>
        Path.GetRelativePath(projectPath, full).Replace('\\', '/');

    /// <summary>
    /// <c>settings.local.json</c> для отпечатка — без <c>permissions.allow</c>: туда CLI сам
    /// дописывает правило по кнопке «Всегда», и после каждого нажатия доверие спрашивалось бы
    /// снова. Правило «всегда» человек уже одобрил на карточке; хуки и всё остальное в файле
    /// по-прежнему меняют отпечаток.
    /// </summary>
    private static byte[] WithoutAllowRules(byte[] content)
    {
        try
        {
            if (JsonNode.Parse(content, documentOptions: ParseOptions) is JsonObject root
                && root["permissions"] is JsonObject permissions)
            {
                permissions.Remove("allow");
                return Encoding.UTF8.GetBytes(root.ToJsonString());
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Не разобран (или два ключа с одним именем — JsonNode такое не берёт): в отпечаток как
            // есть. Исключение отсюда нарушило бы контракт InspectProject «не бросает».
        }

        return content;
    }

    private static readonly JsonDocumentOptions ParseOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static void Describe(string relative, byte[] content, List<string> dangerous, List<string> rules, List<string> other)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, ParseOptions);
        }
        catch (JsonException)
        {
            dangerous.Add($"{relative}: не разобран как JSON — проверьте вручную");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (relative == ".mcp.json")
            {
                Servers(root, dangerous);
                return;
            }

            if (root.TryGetProperty("hooks", out var hooks) && hooks.ValueKind == JsonValueKind.Object)
            {
                foreach (var hookEvent in hooks.EnumerateObject())
                {
                    foreach (var command in HookCommands(hookEvent.Value))
                        dangerous.Add($"хук {hookEvent.Name}: {command}");
                }
            }

            foreach (var key in CommandKeys)
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    dangerous.Add($"команда {key}: {value.GetString()}");
            }

            if (root.TryGetProperty("statusLine", out var status) && status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("command", out var statusCommand))
                dangerous.Add($"команда statusLine: {statusCommand}");

            if (root.TryGetProperty("mcpServers", out _)) Servers(root, dangerous);

            if (root.TryGetProperty("enableAllProjectMcpServers", out var all) && all.ValueKind == JsonValueKind.True)
                dangerous.Add("все MCP-серверы проекта включены без вопроса");

            if (root.TryGetProperty("enabledMcpjsonServers", out var enabled) && enabled.ValueKind == JsonValueKind.Array)
                dangerous.Add($"включены MCP-серверы: {string.Join(", ", enabled.EnumerateArray().Select(e => e.ToString()))}");

            if (root.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object
                && permissions.TryGetProperty("allow", out var allow) && allow.ValueKind == JsonValueKind.Array)
                rules.AddRange(allow.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!));

            // Значения переменных не показываем: там бывают ключи, а для решения хватает имён.
            if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                var names = env.EnumerateObject().Select(p => p.Name).ToList();
                if (names.Count > 0) other.Add($"переменные окружения: {string.Join(", ", names)}");
            }

            if (root.TryGetProperty("enabledPlugins", out var plugins) && plugins.ValueKind == JsonValueKind.Object)
            {
                var names = plugins.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.True).Select(p => p.Name).ToList();
                if (names.Count > 0) other.Add($"плагины: {string.Join(", ", names)}");
            }
        }
    }

    /// <summary>Хуки события: массив групп с matcher, в каждой — свой массив хуков.</summary>
    private static IEnumerable<string> HookCommands(JsonElement groups)
    {
        if (groups.ValueKind != JsonValueKind.Array) yield break;

        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("hooks", out var hooks)
                || hooks.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var hook in hooks.EnumerateArray().Where(h => h.ValueKind == JsonValueKind.Object))
            {
                if (hook.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
                    yield return command.GetString()!;
                else if (hook.TryGetProperty("type", out var type))
                    yield return $"({type})";
            }
        }
    }

    private static void Servers(JsonElement root, List<string> items)
    {
        if (!root.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object) return;

        foreach (var server in servers.EnumerateObject())
        {
            var value = server.Value;
            var target = value.ValueKind != JsonValueKind.Object ? ""
                : value.TryGetProperty("command", out var command)
                    ? string.Join(' ', [command.ToString(), .. Args(value)])
                    : value.TryGetProperty("url", out var url) ? url.ToString() : "";

            items.Add($"MCP-сервер {server.Name}: {target}");
        }
    }

    private static IEnumerable<string> Args(JsonElement server) =>
        server.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
            ? args.EnumerateArray().Select(a => a.ToString())
            : [];

    private static string FolderCaption(string folder) => folder switch
    {
        ".claude/skills" => "скиллы",
        ".claude/commands" => "команды",
        ".claude/agents" => "сабагенты",
        _ => "скрипты хуков",
    };

    private sealed record Walked(List<string> Files, List<string> LinkedFolders, bool Truncated);

    /// <summary>
    /// Файлы папки вглубь. Ссылку на папку не обходим — junction увёл бы обход в чужое дерево,
    /// а то и по кругу, — но и не молчим о ней: она становится пунктом карточки. Ссылка на файл
    /// читается как файл: её содержимое и есть то, что выполнит агент.
    /// </summary>
    private static Walked Walk(string root)
    {
        var walked = new Walked([], [], false);
        if (!Directory.Exists(root)) return walked;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (ArtifactExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                    if (walked.Files.Count >= MaxFilesPerFolder) return walked with { Truncated = true };
                    walked.Files.Add(file);
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (ArtifactFolders.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) continue;

                    if (new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint)) walked.LinkedFolders.Add(child);
                    else pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Недоступная вложенная папка: остальное всё равно в отпечатке.
            }
        }

        return walked;
    }
}
