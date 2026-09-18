using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentsTracker.Agents.Claude.Mcp;

/// <summary>
/// Инструмент, который CLI зовёт вместо интерактивного запроса подтверждения (через
/// --permission-prompt-tool). Здесь только перевод: payload CLI →
/// <see cref="IOperatorConsole"/> → JSON вида {"behavior":"allow","updatedInput":{…}} или
/// {"behavior":"deny","message":"…"}. Карточки, правила «всегда» и аудит — у хоста.
/// </summary>
[McpServerToolType]
public sealed class ClaudePermissionTool(IOperatorConsole console, ILogger<ClaudePermissionTool> logger)
{
    private const string AskUserQuestionTool = "AskUserQuestion";

    [McpServerTool(Name = McpConfigFile.ToolName)]
    [Description("Asks the operator, over Telegram, whether Claude may use a tool. Returns a permission decision.")]
    public async Task<string> Approve(
        RequestContext<CallToolRequestParams> context,
        [Description("Name of the tool Claude wants to use")] string? tool_name = null,
        [Description("Input Claude is passing to the tool")] JsonElement? input = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = context.Params?.Arguments;

        // Схема вызова не задокументирована, поэтому на Debug пишем всё как пришло — имена
        // полей потом можно сверить по логу. Выше Debug нельзя: у Edit/Write в payload
        // лежит содержимое файлов.
        logger.LogDebug("Запрос подтверждения: {Payload}",
            arguments is null ? "(нет аргументов)" : JsonSerializer.Serialize(arguments));

        var toolName = tool_name
            ?? Read(arguments, "tool_name", "toolName", "tool")?.GetString()
            ?? "(неизвестный инструмент)";

        var toolInput = input ?? Read(arguments, "input", "tool_input", "toolInput");
        var suggestions = Read(arguments, "permission_suggestions", "suggestions", "permissionSuggestions");

        try
        {
            if (string.Equals(toolName, AskUserQuestionTool, StringComparison.Ordinal))
                return await HandleQuestionsAsync(toolInput, cancellationToken);

            var rules = PersistableRules(suggestions);
            var decision = await console.ApproveAsync(new ApprovalRequest(toolName, toolInput, rules), cancellationToken);

            return decision.Allowed
                ? Allow(toolInput, decision.PersistRules ? rules : null)
                : Deny(decision.Reason ?? "Пользователь отклонил это действие.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Сбой при запросе подтверждения на {Tool}", toolName);
            return Deny($"Шлюз не смог запросить подтверждение: {ex.Message}");
        }
    }

    // ---- permission_suggestions ----

    /// <summary>
    /// Правила, которые CLI может записать в .claude/settings.local.json проекта. Форма
    /// элемента: {type:"addRules", rules:[{toolName, ruleContent}], behavior, destination}.
    /// Берём только localSettings — остальные CLI сам не применит.
    /// </summary>
    private static IReadOnlyList<PersistentRule>? PersistableRules(JsonElement? suggestions)
    {
        if (suggestions is not { ValueKind: JsonValueKind.Array } array) return null;

        var result = new List<PersistentRule>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("destination", out var destination)) continue;
            if (destination.GetString() != "localSettings") continue;

            result.Add(new PersistentRule(DescribeRules(item), item.Clone()));
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>Правила как «Tool(содержимое)» через запятую — так их видно на карточке.</summary>
    private static string DescribeRules(JsonElement suggestion)
    {
        if (!suggestion.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return "(правило без содержимого)";

        var parts = new List<string>();
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object) continue;
            if (Str(rule, "toolName") is not { Length: > 0 } tool) continue;

            parts.Add(Str(rule, "ruleContent") is { Length: > 0 } content ? $"{tool}({content})" : tool);
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "(правило без содержимого)";
    }

    // ---- AskUserQuestion ----

    private async Task<string> HandleQuestionsAsync(JsonElement? input, CancellationToken ct)
    {
        if (input is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array)
        {
            return Deny("Шлюз не смог разобрать вопросы.");
        }

        var parsed = questions.EnumerateArray().Select(ParseQuestion).ToList();

        var result = await console.AskAsync(parsed, ct);
        if (result.Answers is null) return Deny(result.Refusal ?? "Ответы не получены.");

        // CLI ждёт исходные questions и answers с текстом вопроса в ключе.
        var answers = new JsonObject();
        foreach (var answer in result.Answers) answers[answer.Question] = answer.Answer;

        return new JsonObject
        {
            ["behavior"] = "allow",
            ["updatedInput"] = new JsonObject
            {
                ["questions"] = JsonNode.Parse(questions.GetRawText()),
                ["answers"] = answers,
            },
        }.ToJsonString();
    }

    private static AgentQuestion ParseQuestion(JsonElement question)
    {
        var text = Str(question, "question") ?? "";
        var header = Str(question, "header");
        var multiSelect = question.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;

        var options = question.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray()
                .Select((option, i) => new QuestionOption(Str(option, "label") ?? $"Вариант {i + 1}", Str(option, "description")))
                .ToList()
            : [];

        return new AgentQuestion(text, header, multiSelect, options);
    }

    // ---- формирование ответа ----

    private static string Allow(JsonElement? input, IReadOnlyList<PersistentRule>? persist)
    {
        var result = new JsonObject
        {
            ["behavior"] = "allow",
            // Без updatedInput CLI считает результат невалидным и отклоняет вызов.
            ["updatedInput"] = input is { } value ? JsonNode.Parse(value.GetRawText()) : new JsonObject(),
        };

        if (persist is { Count: > 0 })
            result["updatedPermissions"] = new JsonArray([.. persist.Select(rule => JsonNode.Parse(rule.Raw.GetRawText()))]);

        return result.ToJsonString();
    }

    private static string Deny(string message) => new JsonObject
    {
        ["behavior"] = "deny",
        ["message"] = message,
    }.ToJsonString();

    private static JsonElement? Read(IDictionary<string, JsonElement>? arguments, params string[] names)
    {
        if (arguments is null) return null;

        foreach (var name in names)
        {
            if (arguments.TryGetValue(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                return value;
        }

        return null;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
