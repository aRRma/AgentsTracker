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
    /// <param name="MainModel">Модель хода основной ветки; null — ход сабагента или локальной команды.</param>
    /// <param name="CacheTtl">Срок записи в кэш на этом ходе основной ветки; null — записи не было.</param>
    /// <param name="MainInput">
    /// Ввод основной ветки (результаты инструментов): следующий запрос к модели начнётся после него.
    /// </param>
    /// <param name="CacheRead">Сколько токенов этот ход основной ветки прочитал из кэша; null — ход не основной.</param>
    /// <param name="WorkingDirectory">Папка сессии из события <c>init</c>; null — событие другое.</param>
    /// <param name="WorktreeCalls">Id вызовов EnterWorktree и ExitWorktree основной ветки в этом ходе.</param>
    /// <param name="WorktreeMove">
    /// Куда перешла сессия, если это результат EnterWorktree или ExitWorktree. Только кандидат: что
    /// вызов был именно таким, сверяется по <paramref name="WorktreeCalls"/> прошлого хода.
    /// </param>
    public sealed record Line(
        bool IsJson, bool IsResult, IReadOnlyList<RunActivity> ToolCalls,
        long? ContextTokens = null, ContextCompaction? Compaction = null, string? MainModel = null,
        TimeSpan? CacheTtl = null, bool MainInput = false, long? CacheRead = null,
        string? WorkingDirectory = null, IReadOnlyList<(string Id, bool Exit)>? WorktreeCalls = null,
        (string ToolUseId, string? WorktreePath, string? OriginalCwd)? WorktreeMove = null)
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
            "assistant" => Assistant(root),
            "system" when String(root, "subtype") == "compact_boundary" => new Line(true, false, [], Compaction: Compaction(root)),
            "system" when String(root, "subtype") == "init" && !Nested(root) => new Line(true, false, [], WorkingDirectory: String(root, "cwd")),
            "user" when !Nested(root) => new Line(true, false, [], MainInput: true, WorktreeMove: WorktreeMove(root)),
            _ => Line.Other,
        };
    }

    private const string EnterWorktree = "EnterWorktree";
    private const string ExitWorktree = "ExitWorktree";

    /// <summary>
    /// Переход сессии между worktree по результату инструмента (проверено на CLI 2.1.292). CLI
    /// запоминает новую папку в сессии и на <c>--resume</c> сам продолжает в ней, хотя процесс
    /// запущен в папке проекта. EnterWorktree отдаёт <c>worktreePath</c>; ExitWorktree — тоже
    /// <c>worktreePath</c>, но той папки, из которой вышли, а вернулась сессия в <c>originalCwd</c>.
    /// Какое поле брать, решает вызов (<see cref="Line.WorktreeCalls"/>), а не то, какое поле есть:
    /// иначе отказ ExitWorktree с одним <c>worktreePath</c> оставил бы сессию в покинутой папке.
    /// </summary>
    private static (string ToolUseId, string? WorktreePath, string? OriginalCwd)? WorktreeMove(JsonElement root)
    {
        if (!root.TryGetProperty("tool_use_result", out var result) || result.ValueKind != JsonValueKind.Object)
            return null;

        var worktreePath = String(result, "worktreePath");
        var originalCwd = String(result, "originalCwd");
        if (worktreePath is not { Length: > 0 } && originalCwd is not { Length: > 0 })
            return null;

        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var block in content.EnumerateArray())
            if (String(block, "type") == "tool_result" && String(block, "tool_use_id") is { Length: > 0 } id)
                return (id, worktreePath, originalCwd);

        return null;
    }

    /// <summary>
    /// Id вызовов EnterWorktree и ExitWorktree. По ним, а не по полям результата, узнаётся переход:
    /// <c>worktreePath</c> может вернуть и другой инструмент — например, сабагент в своём worktree,
    /// а сессия при этом остаётся на месте.
    /// </summary>
    private static List<(string Id, bool Exit)>? WorktreeCalls(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return null;

        List<(string Id, bool Exit)>? calls = null;
        foreach (var block in content.EnumerateArray())
        {
            if (String(block, "type") == "tool_use"
                && String(block, "name") is { } name and (EnterWorktree or ExitWorktree)
                && String(block, "id") is { Length: > 0 } id)
                (calls ??= []).Add((id, name == ExitWorktree));
        }

        return calls;
    }

    /// <summary>
    /// Ход модели. Вызовы инструментов — у любого хода, остальное — только у основной ветки:
    /// у сабагентов своё окно, своя модель и свой кэш. Модель берём отсюда, а не из modelUsage
    /// итога: там сабагенты вперемешку с основной моделью, и сабагент на sonnet легко пишет
    /// больше, чем сама сессия. Контекст хода — всё, что модель получила на входе: новые токены
    /// плюс прочитанные и записанные в кэш.
    /// </summary>
    private static Line Assistant(JsonElement root)
    {
        var calls = ToolCalls(root);
        if (!TryMainTurn(root, out var message, out var model)) return new Line(true, false, calls);

        var worktree = WorktreeCalls(root);

        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return new Line(true, false, calls, MainModel: model, WorktreeCalls: worktree);

        var read = Number(usage, "cache_read_input_tokens");
        var context = Number(usage, "input_tokens") + read + Number(usage, "cache_creation_input_tokens");

        return new Line(true, false, calls, context > 0 ? context : null, MainModel: model,
            CacheTtl: CacheTtl(usage), CacheRead: read, WorktreeCalls: worktree);
    }

    /// <summary>
    /// Срок записи в кэш по разбивке usage.cache_creation (проверено на CLI 2.1.292): у основной
    /// ветки на подписке — ephemeral_1h, при оплате сверх подписки — ephemeral_5m. Обе сразу
    /// бывают после веб-поиска: CLI сам дописывает пятиминутную запись хвоста, а начало сессии
    /// живёт час. Чтение без записи срок не сообщает — тогда null, и шлюз берёт прошлый.
    /// </summary>
    private static TimeSpan? CacheTtl(JsonElement usage)
    {
        if (!usage.TryGetProperty("cache_creation", out var creation) || creation.ValueKind != JsonValueKind.Object)
            return null;

        if (Number(creation, "ephemeral_1h_input_tokens") > 0) return TimeSpan.FromHours(1);
        if (Number(creation, "ephemeral_5m_input_tokens") > 0) return TimeSpan.FromMinutes(5);
        return null;
    }

    /// <summary>
    /// parent_tool_use_id лежит в корне события (проверено на CLI 2.1.261): у шага сабагента
    /// там id вызова Agent, у основного хода — null.
    /// </summary>
    private static bool Nested(JsonElement root) =>
        root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String;

    /// <summary>
    /// Сообщение хода основной ветки. Ходы сабагентов — мимо: у них своё окно и своя модель.
    /// «&lt;synthetic&gt;» — ответ локальной команды вроде /context: модель не вызывалась,
    /// нули в usage не значат «контекст пуст», а имя — не модель.
    /// </summary>
    private static bool TryMainTurn(JsonElement root, out JsonElement message, out string model)
    {
        model = "";
        if (Nested(root)
            || !root.TryGetProperty("message", out message)
            || String(message, "model") is not { Length: > 0 } name || name == "<synthetic>")
        {
            message = default;
            return false;
        }

        model = name;
        return true;
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

        var nested = Nested(root);

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
