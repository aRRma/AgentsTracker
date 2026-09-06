using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentsTracker.Agents.Claude;

/// <summary>
/// Плагины Claude Code на диске: список из <c>installed_plugins.json</c> и состояние из
/// <c>enabledPlugins</c>, наслоённое как у CLI — личные настройки → проекта → локальные проекта.
/// Переключение пишет только в личный <c>~/.claude/settings.json</c>: туда же пишет
/// <c>/plugin</c> самого CLI, и следующий <c>claude -p</c> прочитает его без перезапуска.
/// </summary>
internal sealed class ClaudePluginRegistry(ILogger logger)
{
    /// <summary>Установленный плагин: ключ «имя@маркетплейс», имя и папка с содержимым.</summary>
    public sealed record Installed(string Key, string Name, string Path);

    /// <summary>Из какого файла пришло значение: показывается, когда переключить из чата нельзя.</summary>
    public sealed record Setting(bool Enabled, string Layer);

    private const string UserLayer = "личные настройки";

    private static readonly string ClaudeHome =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static readonly string UserSettings = System.IO.Path.Combine(ClaudeHome, "settings.json");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // LF, как пишет сам CLI: иначе каждое переключение меняло бы все строки файла.
        NewLine = "\n",
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Установленные плагины без учёта состояния; папка обязана существовать — иначе читать нечего.</summary>
    public List<Installed> List()
    {
        var result = new List<Installed>();
        var installed = System.IO.Path.Combine(ClaudeHome, "plugins", "installed_plugins.json");
        if (!File.Exists(installed)) return result;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(installed), ReadOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(ex, "Не удалось прочитать {File}", installed);
            return result;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("plugins", out var plugins)
                || plugins.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var plugin in plugins.EnumerateObject())
            {
                // Ключ — «имя@маркетплейс»; в команде используется только имя.
                var key = plugin.Name;
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
                    result.Add(new Installed(key, name, path));
            }
        }

        return result;
    }

    /// <summary>
    /// Действующее состояние по ключу: верхний слой перекрывает нижние. Плагина без записи
    /// здесь нет — установленный без записи считается включённым, как и у самого CLI.
    /// </summary>
    public Dictionary<string, Setting> Settings(string projectPath)
    {
        var result = new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, layer) in Layers(projectPath)) Read(file, layer, result);
        return result;
    }

    /// <summary>
    /// Пишет <c>enabledPlugins[key]</c> в личный settings.json. Возвращает текст ошибки; null — записано.
    /// Остальное содержимое файла сохраняется как есть, комментарии (если были) — нет:
    /// System.Text.Json их не переносит, а CLI сам пишет файл без них.
    /// </summary>
    public string? SetEnabled(string key, bool enabled)
    {
        JsonObject root;
        try
        {
            var text = File.Exists(UserSettings) ? File.ReadAllText(UserSettings) : "{}";
            root = JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
                   ?? throw new JsonException("в корне не объект");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Не удалось прочитать {File}", UserSettings);
            return "Не удалось прочитать settings.json";
        }

        if (root["enabledPlugins"] is not JsonObject section)
        {
            section = new JsonObject();
            root["enabledPlugins"] = section;
        }

        section[key] = enabled;

        // Через временный файл: CLI в соседней сессии может читать settings.json в этот момент,
        // и полузаписанный JSON сломал бы ему старт.
        var temp = UserSettings + ".tmp";
        try
        {
            Directory.CreateDirectory(ClaudeHome);
            File.WriteAllText(temp, root.ToJsonString(WriteOptions) + "\n", new UTF8Encoding(false));
            File.Move(temp, UserSettings, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось записать {File}", UserSettings);
            return "Не удалось записать settings.json";
        }

        logger.LogInformation("Плагин {Plugin} {State} в {File}", key, enabled ? "включён" : "выключен", UserSettings);
        return null;
    }

    /// <summary>Настройка переключается из чата, только если её не перекрывает слой проекта.</summary>
    public static bool IsUserLayer(Setting setting) => setting.Layer == UserLayer;

    private static IEnumerable<(string File, string Layer)> Layers(string projectPath) =>
    [
        (UserSettings, UserLayer),
        (System.IO.Path.Combine(projectPath, ".claude", "settings.json"), ".claude/settings.json проекта"),
        (System.IO.Path.Combine(projectPath, ".claude", "settings.local.json"), ".claude/settings.local.json проекта"),
    ];

    private void Read(string file, string layer, Dictionary<string, Setting> into)
    {
        if (!File.Exists(file)) return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), ReadOptions);
            if (!document.RootElement.TryGetProperty("enabledPlugins", out var section)
                || section.ValueKind != JsonValueKind.Object)
                return;

            foreach (var item in section.EnumerateObject())
            {
                if (item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    into[item.Name] = new Setting(item.Value.GetBoolean(), layer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(ex, "Не удалось прочитать {File}", file);
        }
    }
}
