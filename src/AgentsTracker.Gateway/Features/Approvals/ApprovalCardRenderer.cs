using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentsTracker.Gateway.Infrastructure.Telegram;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Собирает карточку запроса разрешения для Telegram: вместо сырого JSON показывает то, что
/// человек реально решает — команду, файл и суть правки. Незнакомые инструменты получают
/// аккуратно отформатированный вход.
/// </summary>
public static class ApprovalCardRenderer
{
    // Бюджеты в символах уже экранированного HTML. Сумма всех фрагментов с запасом влезает
    // в лимит сообщения Telegram (4096), даже если текст целиком состоит из «&».
    private const int ToolNameBudget = 200;
    private const int PathBudget = 300;
    private const int CommandBudget = 1200;
    private const int DescriptionBudget = 300;
    private const int DiffSideBudget = 650;
    private const int ContentBudget = 900;
    private const int ValueBudget = 400;
    private const int RawInputBudget = 1200;
    private const int SignatureBudget = 300;

    /// <summary>Сколько строк файла показывать в превью записи.</summary>
    private const int PreviewLines = 12;

    /// <param name="persistable">
    /// Правила из permission_suggestions, которые CLI запишет в .claude/settings.local.json
    /// проекта по кнопке «Всегда». Когда они есть, показываем их, а не сигнатуру шлюза:
    /// правило CLI обычно префиксное и шире точного совпадения.
    /// </param>
    public static string Render(
        string toolName, JsonElement? input, string signature, JsonArray? persistable, string projectPath)
    {
        var obj = input is { ValueKind: JsonValueKind.Object } o ? o : (JsonElement?)null;
        var card = new StringBuilder();

        card.Append("🔐 <b>").Append(E(toolName, ToolNameBudget)).Append("</b>");
        var caption = Caption(toolName);
        if (caption is not null) card.Append(" — ").Append(caption);
        card.Append('\n');

        var rendered = obj is { } body && RenderKnown(toolName, body, projectPath, card);
        if (!rendered) RenderGeneric(input, card);

        // Блоки <pre> не всегда заканчиваются переводом строки — выравниваем перед подписью.
        if (card[^1] != '\n') card.Append('\n');
        var rules = SuggestedRules(persistable);
        if (rules.Count > 0)
        {
            card.Append("\n<i>Кнопка «Всегда» запишет в .claude/settings.local.json проекта:</i>\n");
            foreach (var rule in rules)
                card.Append("• <code>").Append(E(Shorten(rule, projectPath), SignatureBudget / rules.Count)).Append("</code>\n");
        }
        else
        {
            card.Append("\n<i>Кнопка «Всегда» больше не спросит про: ")
                .Append(E(Shorten(signature, projectPath), SignatureBudget))
                .Append("</i>");
        }

        return card.ToString();
    }

    /// <summary>Сколько правил из suggestions показывать: больше и не пришлёт, и не влезет.</summary>
    private const int MaxSuggestedRules = 4;

    /// <summary>
    /// Правила из permission_suggestions в виде «Tool(содержимое)». Форма элемента:
    /// {type:"addRules", rules:[{toolName, ruleContent}], behavior, destination}.
    /// </summary>
    private static List<string> SuggestedRules(JsonArray? suggestions)
    {
        var result = new List<string>();
        if (suggestions is null) return result;

        foreach (var suggestion in suggestions.OfType<JsonObject>())
        {
            if (suggestion["rules"] is not JsonArray rules) continue;

            foreach (var rule in rules.OfType<JsonObject>())
            {
                var tool = rule["toolName"]?.GetValue<string>();
                if (tool is not { Length: > 0 }) continue;

                var content = rule["ruleContent"]?.GetValue<string>();
                result.Add(content is { Length: > 0 } ? $"{tool}({content})" : tool);

                if (result.Count >= MaxSuggestedRules) return result;
            }
        }

        return result;
    }

    /// <summary>Короткое пояснение к имени инструмента: человеку в чате «Glob» ничего не говорит.</summary>
    private static string? Caption(string toolName) => toolName switch
    {
        "Bash" or "PowerShell" => "выполнить команду",
        "Edit" => "изменить файл",
        "MultiEdit" => "изменить файл (несколько правок)",
        "Write" => "записать файл",
        "NotebookEdit" => "изменить ноутбук",
        "Read" => "прочитать файл",
        "Glob" => "найти файлы",
        "Grep" => "поиск по содержимому",
        "WebFetch" => "загрузить страницу",
        "WebSearch" => "поиск в интернете",
        "Agent" or "Task" => "запустить сабагента",
        "Skill" => "вызвать навык",
        "TodoWrite" => "обновить список задач",
        _ when toolName.StartsWith("mcp__", StringComparison.Ordinal) => "MCP-инструмент",
        _ => null,
    };

    private static bool RenderKnown(string toolName, JsonElement input, string projectPath, StringBuilder card)
    {
        switch (toolName)
        {
            case "Bash":
            case "PowerShell":
                if (Str(input, "command") is not { } command) return false;
                card.Append("<pre>").Append(E(command, CommandBudget)).Append("</pre>\n");
                AppendNote(card, Str(input, "description"));
                if (input.TryGetProperty("run_in_background", out var bg) && bg.ValueKind == JsonValueKind.True)
                    card.Append("⏳ в фоне\n");
                return true;

            case "Edit":
                if (Str(input, "file_path") is not { } editPath) return false;
                AppendPath(card, editPath, projectPath);
                AppendDiff(card, Str(input, "old_string"), Str(input, "new_string"));
                if (input.TryGetProperty("replace_all", out var all) && all.ValueKind == JsonValueKind.True)
                    card.Append("\n<i>заменить все вхождения</i>");
                return true;

            case "MultiEdit":
                if (Str(input, "file_path") is not { } multiPath) return false;
                AppendPath(card, multiPath, projectPath);
                if (input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                {
                    var list = edits.EnumerateArray().ToArray();
                    card.Append("Правок: ").Append(list.Length).Append('\n');
                    // Показываем только первую: остальные всё равно не влезут, а суть ясна.
                    if (list.Length > 0 && list[0].ValueKind == JsonValueKind.Object)
                        AppendDiff(card, Str(list[0], "old_string"), Str(list[0], "new_string"));
                }
                return true;

            case "Write":
                if (Str(input, "file_path") is not { } writePath) return false;
                AppendPath(card, writePath, projectPath);
                if (Str(input, "content") is { } content)
                {
                    var lines = content.Split('\n');
                    card.Append("Строк: ").Append(lines.Length).Append('\n');
                    var preview = string.Join('\n', lines.Take(PreviewLines));
                    card.Append("<pre>").Append(E(preview, ContentBudget));
                    if (lines.Length > PreviewLines) card.Append("\n…");
                    card.Append("</pre>");
                }
                return true;

            case "Read":
                if (Str(input, "file_path") is not { } readPath) return false;
                AppendPath(card, readPath, projectPath);
                if (input.TryGetProperty("offset", out var offset) && offset.ValueKind == JsonValueKind.Number)
                    card.Append("Со строки ").Append(offset.GetRawText());
                if (input.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Number)
                    card.Append(", строк: ").Append(limit.GetRawText());
                return true;

            case "Glob":
            case "Grep":
                if (Str(input, "pattern") is not { } pattern) return false;
                card.Append("Шаблон: <code>").Append(E(pattern, ValueBudget)).Append("</code>\n");
                if (Str(input, "path") is { } searchPath) AppendPath(card, searchPath, projectPath, "в ");
                if (Str(input, "glob") is { } glob) card.Append("Файлы: <code>").Append(E(glob, ValueBudget)).Append("</code>\n");
                return true;

            case "WebFetch":
                if (Str(input, "url") is not { } url) return false;
                card.Append("🌐 ").Append(E(url, ValueBudget)).Append('\n');
                AppendNote(card, Str(input, "prompt"));
                return true;

            case "WebSearch":
                if (Str(input, "query") is not { } query) return false;
                card.Append("🔎 <code>").Append(E(query, ValueBudget)).Append("</code>\n");
                return true;

            case "Agent":
            case "Task":
                if (Str(input, "prompt") is not { } prompt) return false;
                if (Str(input, "description") is { } title) card.Append("<b>").Append(E(title, ValueBudget)).Append("</b>\n");
                if (Str(input, "subagent_type") is { } kind) card.Append("Тип: <code>").Append(E(kind, ValueBudget)).Append("</code>\n");
                card.Append("<pre>").Append(E(prompt, ContentBudget)).Append("</pre>");
                return true;

            case "Skill":
                if (Str(input, "skill") is not { } skill) return false;
                card.Append("Навык: <code>").Append(E(skill, ValueBudget)).Append("</code>\n");
                if (Str(input, "args") is { } args) card.Append("<pre>").Append(E(args, ValueBudget)).Append("</pre>");
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Незнакомый инструмент: плоский объект показываем как список «поле: значение»,
    /// всё остальное — отформатированным JSON с нормальными буквами вместо \u-последовательностей.
    /// </summary>
    private static void RenderGeneric(JsonElement? input, StringBuilder card)
    {
        if (input is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } element) return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            var props = element.EnumerateObject().ToArray();
            if (props.Length == 0) return;

            if (props.All(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
            {
                // Общий бюджет на весь список: полей у незнакомого инструмента может быть
                // сколько угодно, а переполненное сообщение Telegram не примет — отправка упадёт,
                // и исключение превратится в отказ вместо карточки.
                var left = RawInputBudget;

                foreach (var p in props)
                {
                    if (left <= 0)
                    {
                        card.Append("…\n");
                        break;
                    }

                    var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                    var name = E(p.Name, Math.Min(ToolNameBudget, left));
                    left -= name.Length;

                    var shown = E(value, Math.Min(ValueBudget, Math.Max(1, left)));
                    left -= shown.Length;

                    card.Append(name).Append(": ");
                    if (value.Contains('\n'))
                        card.Append("\n<pre>").Append(shown).Append("</pre>");
                    else
                        card.Append("<code>").Append(shown).Append("</code>\n");
                }

                return;
            }
        }

        var pretty = JsonSerializer.Serialize(element, PrettyJson);
        card.Append("<pre>").Append(E(pretty, RawInputBudget)).Append("</pre>");
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        // Кириллица в исходном JSON приходит как А…: без этого карточка нечитаема.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void AppendPath(StringBuilder card, string path, string projectPath, string prefix = "")
    {
        var shown = Shorten(path, projectPath);
        card.Append("📄 ").Append(prefix).Append("<code>").Append(E(shown, PathBudget)).Append("</code>\n");
    }

    private static void AppendNote(StringBuilder card, string? note)
    {
        if (note is { Length: > 0 })
            card.Append("<i>").Append(E(note, DescriptionBudget)).Append("</i>\n");
    }

    private static void AppendDiff(StringBuilder card, string? oldText, string? newText)
    {
        if (oldText is { Length: > 0 })
            card.Append("<b>Было:</b>\n<pre>").Append(E(oldText, DiffSideBudget)).Append("</pre>\n");
        if (newText is not null)
            card.Append("<b>Станет:</b>\n<pre>").Append(E(newText.Length == 0 ? "(пусто)" : newText, DiffSideBudget)).Append("</pre>\n");
    }

    /// <summary>Убирает префикс проекта из пути: абсолютные Windows-пути съедают половину экрана телефона.</summary>
    private static string Shorten(string text, string projectPath)
    {
        if (projectPath.Length == 0) return text;

        var root = projectPath.TrimEnd('\\', '/');
        var index = text.IndexOf(root, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return text;

        var after = index + root.Length;
        if (after < text.Length && text[after] is '\\' or '/') after++;

        return text[..index] + text[after..];
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string E(string text, int budget) => TelegramFormatter.EscapeCapped(text, budget);
}
