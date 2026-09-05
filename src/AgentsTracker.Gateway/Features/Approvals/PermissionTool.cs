using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// MCP-инструмент, который Claude Code вызывает вместо интерактивного запроса разрешения
/// (передаётся через --permission-prompt-tool). Возвращает JSON-строку вида
/// {"behavior":"allow","updatedInput":{…}} либо {"behavior":"deny","message":"…"}.
/// </summary>
[McpServerToolType]
public sealed class PermissionTool(
    ApprovalBroker broker,
    SessionStore store,
    IAuditLog audit,
    ILogger<PermissionTool> logger)
{
    private const string AskUserQuestionTool = "AskUserQuestion";

    // Бюджеты в символах уже экранированного HTML: сумма с запасом влезает в лимит
    // сообщения Telegram (4096), даже если текст целиком состоит из «&».
    private const int HeaderBudget = 200;
    private const int QuestionBudget = 1000;
    private const int OptionLabelBudget = 120;
    private const int OptionDescriptionBudget = 300;

    /// <summary>Предел длины подписи на инлайн-кнопке Telegram.</summary>
    private const int ButtonLabelLimit = 64;

    [McpServerTool(Name = McpConfigFileToolName)]
    [Description("Asks the operator, over Telegram, whether Claude may use a tool. Returns a permission decision.")]
    public async Task<string> Approve(
        RequestContext<CallToolRequestParams> context,
        [Description("Name of the tool Claude wants to use")] string? tool_name = null,
        [Description("Input Claude is passing to the tool")] JsonElement? input = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = context.Params?.Arguments;

        // Точная схема вызова публично не задокументирована — на Debug пишем всё, что пришло,
        // чтобы имена полей можно было сверить по логу. На Information полный payload не нужен:
        // для Edit/Write это содержимое файлов, которому в логе не место.
        logger.LogDebug("Запрос разрешения: {Payload}",
            arguments is null ? "(нет аргументов)" : JsonSerializer.Serialize(arguments));

        var toolName = tool_name
            ?? Read(arguments, "tool_name", "toolName", "tool")?.GetString()
            ?? "(неизвестный инструмент)";

        var toolInput = input ?? Read(arguments, "input", "tool_input", "toolInput");
        var suggestions = Read(arguments, "permission_suggestions", "suggestions", "permissionSuggestions");

        // Только начало: полная команда может нести токен в заголовке curl, ему в логе не место.
        logger.LogInformation("Запрос разрешения: {Tool} {Key}", toolName, Text.Preview(Highlight(toolName, toolInput) ?? ""));

        try
        {
            if (string.Equals(toolName, AskUserQuestionTool, StringComparison.Ordinal))
                return await HandleQuestionsAsync(toolInput, cancellationToken);

            return await HandleApprovalAsync(toolName, toolInput, suggestions, cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Разрешение на {Tool} не получено за отведённое время", toolName);
            Audit(AuditKinds.Approval, BuildSignature(toolName, toolInput), "timeout");
            return Deny("Пользователь не ответил на запрос разрешения за отведённое время. Не повторяйте это действие.");
        }
        catch (OperationCanceledException)
        {
            Audit(AuditKinds.Approval, BuildSignature(toolName, toolInput), "cancel");
            return Deny("Запрос отменён пользователем.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Сбой при запросе разрешения на {Tool}", toolName);
            return Deny($"Шлюз не смог запросить подтверждение: {ex.Message}");
        }
    }

    // ---- обычные разрешения ----

    private async Task<string> HandleApprovalAsync(
        string toolName, JsonElement? input, JsonElement? suggestions, CancellationToken ct)
    {
        var signature = BuildSignature(toolName, input);

        if (store.IsAlwaysAllowed(signature))
        {
            logger.LogInformation("Автоматически разрешено по правилу «всегда»: {Signature}", signature);
            Audit(AuditKinds.Approval, signature, "rule");
            return Allow(input);
        }

        var persistable = PersistableSuggestions(suggestions);

        var buttons = new List<ChoiceOption>
        {
            new("allow", "✅ Разрешить"),
            new("always", "♾ Всегда"),
            new("deny", "❌ Отклонить"),
            new("reason", "✋ Отклонить с причиной"),
        };

        var card = ApprovalCardRenderer.Render(toolName, input, signature, persistable, store.ProjectPath);

        // Обрезанный вход — файлом до карточки: разрешать команду, хвост которой не виден, нельзя.
        if (card.FullText is { } fullText)
            await broker.SendAttachmentAsync($"{SafeFileName(toolName)}-input.txt", fullText, ct);

        var (key, userId) = await broker.AskChoiceAsync(card.Html, buttons, ct);
        Audit(AuditKinds.Approval, signature, key, userId);

        switch (key)
        {
            case "allow":
                return Allow(input);

            case "always":
                if (persistable is null) store.AddAlwaysAllow(signature);
                Audit(AuditKinds.Rules, $"add {signature}", persistable is null ? "gateway" : "cli", userId);
                return Allow(input, persistable);

            case "reason":
                var reason = await broker.AskTextAsync(
                    "Напишите сообщением, почему отклоняете и что делать вместо этого:", ct);
                return Deny(reason);

            default:
                return Deny("Пользователь отклонил это действие.");
        }
    }

    /// <summary>Имя инструмента приходит от CLI: в имени файла ему нечего делать с разделителями путей.</summary>
    private static string SafeFileName(string toolName)
    {
        var safe = new string(toolName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return Text.Clip(safe.Length == 0 ? "tool" : safe, 40);
    }

    /// <summary>Самое важное поле инструмента — команда или путь к файлу.</summary>
    private static string? Highlight(string toolName, JsonElement? input)
    {
        if (input is not { ValueKind: JsonValueKind.Object } obj) return null;

        string[] interesting = ["command", "file_path", "path", "url", "pattern"];

        foreach (var name in interesting)
        {
            if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    /// <summary>
    /// Ключ для кнопки «Всегда», когда CLI не прислал suggestions. Запоминаем точное значение
    /// ключевого аргумента, а не префикс: разрешённое «git status» не должно открывать дорогу
    /// «git status &amp;&amp; rm -rf .», а один разрешённый Write — записи в любой файл.
    /// </summary>
    private static string BuildSignature(string toolName, JsonElement? input)
    {
        var key = Highlight(toolName, input)?.Trim();
        if (key is { Length: > 0 }) return $"{toolName}({key})";

        // Ключевого поля нет (WebSearch с query, MCP-инструменты) — берём весь вход целиком.
        // Голое имя инструмента открыло бы «Всегда» для любых его аргументов.
        var raw = input is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } element
            ? JsonSerializer.Serialize(element)
            : "";

        // Скобки обязательны даже при пустом входе: по ним отличаются сигнатуры этого формата
        // от голого имени инструмента, которое писала старая версия (оно разрешало любые
        // аргументы и вычищается при загрузке state.json).
        return raw is { Length: > 0 } && raw != "{}" ? $"{toolName}{raw}" : $"{toolName}()";
    }

    /// <summary>Отбирает подсказки правил, которые CLI может записать в .claude/settings.local.json.</summary>
    private static JsonArray? PersistableSuggestions(JsonElement? suggestions)
    {
        if (suggestions is not { ValueKind: JsonValueKind.Array } array) return null;

        var result = new JsonArray();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("destination", out var destination)) continue;
            if (destination.GetString() != "localSettings") continue;

            result.Add(JsonNode.Parse(item.GetRawText()));
        }

        return result.Count > 0 ? result : null;
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

        var answers = new JsonObject();

        foreach (var question in questions.EnumerateArray())
        {
            var text = question.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
            var header = question.TryGetProperty("header", out var h) ? h.GetString() : null;
            var multiSelect = question.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;

            var options = question.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                ? o.EnumerateArray().ToArray()
                : [];

            var card = new StringBuilder("❓ ");
            if (header is { Length: > 0 })
                card.Append("<b>").Append(TelegramFormatter.EscapeCapped(header, HeaderBudget)).Append("</b>\n");
            card.Append(TelegramFormatter.EscapeCapped(text, QuestionBudget));

            var buttons = new List<ChoiceOption>();
            // На кнопке подпись урезана, а агенту нужен полный текст варианта.
            var fullLabels = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var i = 0; i < options.Length; i++)
            {
                var label = options[i].TryGetProperty("label", out var l) ? l.GetString() ?? $"Вариант {i + 1}" : $"Вариант {i + 1}";
                var description = options[i].TryGetProperty("description", out var d) ? d.GetString() : null;

                card.Append("\n\n<b>").Append(TelegramFormatter.EscapeCapped(label, OptionLabelBudget)).Append("</b>");
                if (description is { Length: > 0 })
                    card.Append(" — ").Append(TelegramFormatter.EscapeCapped(description, OptionDescriptionBudget));

                fullLabels[$"o{i}"] = label;
                buttons.Add(new ChoiceOption($"o{i}", Text.Clip(label, ButtonLabelLimit)));
            }

            buttons.Add(new ChoiceOption("free", "✍️ Свой ответ"));

            if (multiSelect)
                card.Append("\n\n<i>Можно выбрать несколько — тогда «Свой ответ» и перечислите через запятую.</i>");

            var (key, userId) = await broker.AskChoiceAsync(card.ToString(), buttons, ct);

            var answer = key == "free"
                ? await broker.AskTextAsync("Напишите ответ сообщением:", ct)
                : fullLabels.GetValueOrDefault(key, key);

            Audit(AuditKinds.Question, $"{Text.Preview(header ?? text, 60)}: {Text.Preview(answer, 60)}", key == "free" ? "free" : "option", userId);
            answers[text] = answer;
        }

        var updatedInput = new JsonObject
        {
            ["questions"] = JsonNode.Parse(questions.GetRawText()),
            ["answers"] = answers,
        };

        return new JsonObject
        {
            ["behavior"] = "allow",
            ["updatedInput"] = updatedInput,
        }.ToJsonString();
    }

    private void Audit(string kind, string summary, string outcome, long? userId = null) =>
        audit.Write(AuditEvent.Now(kind, summary, userId, broker.ActiveChatId, store.ProjectPath, store.SessionId, outcome));

    // ---- формирование ответа ----

    private static string Allow(JsonElement? input, JsonArray? updatedPermissions = null)
    {
        var result = new JsonObject
        {
            ["behavior"] = "allow",
            // updatedInput обязателен: без него CLI считает результат невалидным и отклоняет вызов.
            ["updatedInput"] = input is { } value ? JsonNode.Parse(value.GetRawText()) : new JsonObject(),
        };

        if (updatedPermissions is not null)
            result["updatedPermissions"] = updatedPermissions;

        return result.ToJsonString();
    }

    private static string Deny(string message) => new JsonObject
    {
        ["behavior"] = "deny",
        ["message"] = message,
    }.ToJsonString();

    private static JsonElement? Read(
        IDictionary<string, JsonElement>? arguments, params string[] names)
    {
        if (arguments is null) return null;

        foreach (var name in names)
        {
            if (arguments.TryGetValue(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                return value;
        }

        return null;
    }

    // Атрибуту нужна константа времени компиляции, а McpConfigFile.ToolName задаёт то же значение.
    private const string McpConfigFileToolName = Infrastructure.Mcp.McpConfigFile.ToolName;
}
