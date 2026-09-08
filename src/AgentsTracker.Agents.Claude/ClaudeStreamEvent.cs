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
    public sealed record Line(bool IsJson, bool IsResult, IReadOnlyList<RunActivity> ToolCalls)
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
            "assistant" => ToolCalls(root) is { Count: > 0 } calls ? new Line(true, false, calls) : Line.Other,
            _ => Line.Other,
        };
    }

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
