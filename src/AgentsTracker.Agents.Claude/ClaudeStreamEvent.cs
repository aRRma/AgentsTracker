using System.Text.Json;

namespace AgentsTracker.Agents.Claude;

/// <summary>
/// Разбор строк <c>claude -p --output-format stream-json</c>. Поток нужен только затем,
/// чтобы показывать в чате, чем агент занят. Итог — последняя строка с <c>"type":"result"</c>,
/// той же формы, что ответ <c>--output-format json</c>.
/// </summary>
public static class ClaudeStreamEvent
{
    /// <summary>Что в строке: итог, вызовы инструментов, другое событие или вообще не JSON.</summary>
    /// <param name="ContextTokens">Размер контекста на этом ходе основной ветки; null — ход не основной или без usage.</param>
    /// <param name="Compaction">Событие сжатия контекста: сколько было и сколько стало.</param>
    public sealed record Line(
        bool IsJson, bool IsResult, IReadOnlyList<RunActivity> ToolCalls,
        long? ContextTokens = null, ContextCompaction? Compaction = null)
    {
        public static readonly Line Text = new(false, false, []);
        public static readonly Line Other = new(true, false, []);
        public static readonly Line Result = new(true, true, []);
    }

    /// <summary>
    /// Разбирает строку один раз: на долгом запуске их тысячи, а в <c>user</c>-событиях
    /// лежит содержимое прочитанных файлов.
    /// </summary>
    public static Line Classify(string line)
    {
        if (!TryParse(line, out var root)) return Line.Text;

        return String(root, "type") switch
        {
            "result" => Line.Result,
            "assistant" => new Line(true, false, ToolCalls(root), ContextTokens(root)),
            "system" when String(root, "subtype") == "compact_boundary" => new Line(true, false, [], Compaction: Compaction(root)),
            _ => Line.Other,
        };
    }

    /// <summary>
    /// Контекст хода — всё, что модель получила на входе: новые токены плюс прочитанные
    /// и записанные в кэш. Ходы сабагентов не считаем: у них своё окно. «&lt;synthetic&gt;» —
    /// ответ локальной команды вроде /context, модель не вызывалась и нули в usage не значат
    /// «контекст пуст».
    /// </summary>
    private static long? ContextTokens(JsonElement root)
    {
        if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String)
            return null;

        if (!root.TryGetProperty("message", out var message)
            || String(message, "model") == "<synthetic>"
            || !message.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            return null;

        var total = Number(usage, "input_tokens") + Number(usage, "cache_read_input_tokens")
                    + Number(usage, "cache_creation_input_tokens");
        return total > 0 ? total : null;
    }

    /// <summary>compact_metadata события compact_boundary (проверено на CLI 2.1.261).</summary>
    private static ContextCompaction? Compaction(JsonElement root)
    {
        if (!root.TryGetProperty("compact_metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            return null;

        var before = Number(metadata, "pre_tokens");
        var after = Number(metadata, "post_tokens");
        return before > 0 ? new ContextCompaction(before, after) : null;
    }

    private static long Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    /// <summary>
    /// Вызовы инструментов из события — единственное, что стоит показывать. Текст
    /// и размышления агента приходят кусками, в статусе от них толку нет.
    /// </summary>
    private static List<RunActivity> ToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return [];

        // parent_tool_use_id лежит в корне события (проверено на CLI 2.1.261): у шага
        // сабагента там id вызова Agent, у основного хода — null.
        var nested = root.TryGetProperty("parent_tool_use_id", out var parent)
                     && parent.ValueKind == JsonValueKind.String;

        var calls = new List<RunActivity>();
        foreach (var block in content.EnumerateArray())
        {
            if (String(block, "type") != "tool_use") continue;
            if (String(block, "name") is not { Length: > 0 } name) continue;

            block.TryGetProperty("input", out var input);
            calls.Add(new RunActivity(Describe(name, input), nested));
        }

        return calls;
    }

    /// <summary>
    /// Имя инструмента и главный аргумент — тот, по которому понятно, что происходит.
    /// Полный ввод не показываем: у Edit/Write это содержимое файла.
    /// </summary>
    private static string Describe(string name, JsonElement input)
    {
        var argument = name switch
        {
            "Read" or "Edit" or "Write" or "NotebookEdit" => FileName(String(input, "file_path")),
            "Bash" or "PowerShell" => String(input, "command"),
            "Grep" or "Glob" => String(input, "pattern"),
            "Agent" or "Task" => String(input, "description"),
            "WebFetch" => String(input, "url"),
            "WebSearch" => String(input, "query"),
            "Skill" => "/" + String(input, "skill"),
            Mcp.McpConfigFile.SendFileToolFullName => FileName(String(input, "path")),
            _ => null,
        };

        // MCP-инструменты зовутся mcp__сервер__имя: серверный префикс пользователю ни о чём.
        if (name.StartsWith("mcp__", StringComparison.Ordinal))
            name = name.Split("__", 3) is { Length: 3 } parts ? $"{parts[1]}:{parts[2]}" : name;

        return argument is { Length: > 0 }
            ? $"{name} {Text.Preview(argument, 60)}"
            : name;
    }

    private static string? FileName(string? path) =>
        path is { Length: > 0 } ? Path.GetFileName(path) : path;

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryParse(string line, out JsonElement root)
    {
        root = default;
        if (!line.AsSpan().TrimStart().StartsWith('{')) return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
