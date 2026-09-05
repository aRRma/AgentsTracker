using System.Text.Json;

namespace AgentsTracker.Gateway.Infrastructure.Claude;

/// <summary>
/// Разбор строк <c>claude -p --output-format stream-json</c>. Поток нужен ради одного:
/// показывать в чате, что агент делает прямо сейчас. Итог запуска — последняя строка
/// с <c>"type":"result"</c>, той же формы, что ответ <c>--output-format json</c>.
/// </summary>
public static class ClaudeStreamEvent
{
    /// <summary>Строка потока — итог запуска (её и разбирает <see cref="ClaudeCliJson"/>).</summary>
    public static bool IsResult(string line) =>
        TryParse(line, out var root) && TypeOf(root) == "result";

    /// <summary>
    /// Вызовы инструментов из этой строки — то, что стоит показать пользователю. Текст
    /// и «размышления» агента не показываем: они приходят кусками и в статусе смысла не имеют.
    /// </summary>
    public static IReadOnlyList<(string Description, bool Nested)> ToolCalls(string line)
    {
        if (!TryParse(line, out var root) || TypeOf(root) != "assistant") return [];
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return [];

        var nested = root.TryGetProperty("parent_tool_use_id", out var parent)
                     && parent.ValueKind == JsonValueKind.String;

        var calls = new List<(string, bool)>();
        foreach (var block in content.EnumerateArray())
        {
            if (String(block, "type") != "tool_use") continue;
            if (String(block, "name") is not { Length: > 0 } name) continue;

            block.TryGetProperty("input", out var input);
            calls.Add((Describe(name, input), nested));
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

    private static string? TypeOf(JsonElement root) => String(root, "type");

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryParse(string line, out JsonElement root)
    {
        root = default;
        if (!line.StartsWith('{')) return false;

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
