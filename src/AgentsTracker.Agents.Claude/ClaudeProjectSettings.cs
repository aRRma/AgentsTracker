using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentsTracker.Agents.Claude;

/// <summary>
/// Настройки агента в проекте глазами Claude Code: что он выполнит сам, как только запуск
/// начнётся в этой папке. <c>claude -p</c> не показывает диалог доверия к папке, поэтому
/// <c>.claude/settings.json</c> чужого репозитория с хуком <c>SessionStart</c> выполняется
/// без единой карточки — проверено на CLI 2.1.292.
/// </summary>
internal static class ClaudeProjectSettings
{
    /// <summary>Файлы, которые CLI читает из проекта и по которым выполняет команды.</summary>
    private static readonly string[] Files = [".claude/settings.json", ".claude/settings.local.json", ".mcp.json"];

    /// <summary>
    /// Папки, содержимое которых агент запускает или которыми расширяет свои права: скилл
    /// несёт <c>allowed-tools</c> и свои хуки, хук из настроек обычно вызывает скрипт из
    /// <c>.claude/hooks</c>. Не вся <c>.claude</c>: там же живут worktree с целыми копиями проекта.
    /// </summary>
    private static readonly string[] Folders = [".claude/skills", ".claude/commands", ".claude/agents", ".claude/hooks"];

    /// <summary>Ключи настроек, значение которых — команда оболочки.</summary>
    private static readonly string[] CommandKeys = ["apiKeyHelper", "awsAuthRefresh", "awsCredentialExport", "otelHeadersHelper"];

    private const int MaxFiles = 2000;
    private const long MaxHashedBytes = 1024 * 1024;
    private const int ItemLength = 160;

    public static ProjectSettings? Inspect(string projectPath)
    {
        var found = new List<(string Relative, string Full)>();

        foreach (var relative in Files)
        {
            var full = Path.Combine(projectPath, relative);
            if (File.Exists(full)) found.Add((relative, full));
        }

        var folderFiles = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var folder in Folders)
        {
            var count = 0;
            foreach (var full in Walk(Path.Combine(projectPath, folder)))
            {
                if (found.Count >= MaxFiles) break;
                found.Add((Path.GetRelativePath(projectPath, full).Replace('\\', '/'), full));
                count++;
            }

            if (count > 0) folderFiles[folder] = count;
        }

        if (found.Count == 0) return null;

        var items = new List<string>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var (relative, full) in found.OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));

            byte[]? content = null;
            try
            {
                var info = new FileInfo(full);
                // Большой файл — по размеру и времени: читать мегабайты ради скилла незачем,
                // а замену всё равно видно по этим двум полям.
                if (info.Length > MaxHashedBytes)
                    hash.AppendData(Encoding.UTF8.GetBytes($"{info.Length}:{info.LastWriteTimeUtc.Ticks}\0"));
                else
                    hash.AppendData(content = File.ReadAllBytes(full));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Нечитаемый файл не повод пропустить проверку: агент-то его, возможно, прочтёт.
                hash.AppendData(Encoding.UTF8.GetBytes("unreadable\0"));
                items.Add($"{relative}: не прочитан ({ex.Message}) — проверьте вручную");
                continue;
            }

            if (content is not null && Files.Contains(relative, StringComparer.Ordinal))
                Describe(relative, content, items);
        }

        foreach (var (folder, count) in folderFiles)
            items.Add($"{FolderCaption(folder)} (файлов: {count})");

        if (found.Count >= MaxFiles) items.Add($"файлов больше {MaxFiles} — отпечаток по первым");

        // Файл есть, а исполняемого в нём ничего: всё равно спрашиваем — список ключей CLI растёт
        // от версии к версии, и «ничего опасного» по сегодняшнему списку завтра может стать неправдой.
        if (items.Count == 0) items.AddRange(found.Select(f => $"{f.Relative}: опасных ключей не найдено"));

        return new ProjectSettings(Convert.ToHexStringLower(hash.GetHashAndReset())[..16], [.. items.Select(i => Text.Clip(i, ItemLength))]);
    }

    private static void Describe(string relative, byte[] content, List<string> items)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            items.Add($"{relative}: не разобран как JSON — проверьте вручную");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (relative == ".mcp.json")
            {
                Servers(root, items);
                return;
            }

            if (root.TryGetProperty("hooks", out var hooks) && hooks.ValueKind == JsonValueKind.Object)
            {
                foreach (var hookEvent in hooks.EnumerateObject())
                {
                    foreach (var command in HookCommands(hookEvent.Value))
                        items.Add($"хук {hookEvent.Name}: {command}");
                }
            }

            if (root.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object
                && permissions.TryGetProperty("allow", out var allow) && allow.ValueKind == JsonValueKind.Array)
            {
                foreach (var rule in allow.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String))
                    items.Add($"правило «всегда»: {rule.GetString()}");
            }

            if (root.TryGetProperty("mcpServers", out _)) Servers(root, items);

            if (root.TryGetProperty("enableAllProjectMcpServers", out var all) && all.ValueKind == JsonValueKind.True)
                items.Add("все MCP-серверы проекта включены без вопроса");

            if (root.TryGetProperty("enabledMcpjsonServers", out var enabled) && enabled.ValueKind == JsonValueKind.Array)
                items.Add($"включены MCP-серверы: {string.Join(", ", enabled.EnumerateArray().Select(e => e.ToString()))}");

            foreach (var key in CommandKeys)
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    items.Add($"команда {key}: {value.GetString()}");
            }

            if (root.TryGetProperty("statusLine", out var status) && status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("command", out var statusCommand))
                items.Add($"команда statusLine: {statusCommand}");

            // Значения переменных не показываем: там бывают ключи, а для решения хватает имён.
            if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                var names = env.EnumerateObject().Select(p => p.Name).ToList();
                if (names.Count > 0) items.Add($"переменные окружения: {string.Join(", ", names)}");
            }

            if (root.TryGetProperty("enabledPlugins", out var plugins) && plugins.ValueKind == JsonValueKind.Object)
            {
                var names = plugins.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.True).Select(p => p.Name).ToList();
                if (names.Count > 0) items.Add($"плагины: {string.Join(", ", names)}");
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

    /// <summary>
    /// Файлы папки вглубь, без перехода по ссылкам: junction внутри <c>.claude</c> увёл бы обход
    /// в чужое дерево, а то и по кругу. Список целиком, а не ленивый: перечисление бросает
    /// по ходу, а не при создании.
    /// </summary>
    private static List<string> Walk(string root)
    {
        if (!Directory.Exists(root)) return [];

        try
        {
            return [.. Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            }).Take(MaxFiles)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
