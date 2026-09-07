using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentsTracker.Gateway.Infrastructure.Chat;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Карточка запроса разрешения: вместо сырого JSON показывает то, что человек реально
/// решает — команду, файл, суть правки. Незнакомый инструмент показывается общим рендером.
/// </summary>
public static class ApprovalCardRenderer
{
    // Бюджеты в символах уже экранированного HTML: сумма фрагментов влезает в лимит
    // сообщения даже если текст целиком состоит из «&».
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

    /// <summary>
    /// Карточка и, если что-то не влезло, полный текст обрезанного: разрешать команду,
    /// хвоста которой не видел, нельзя. Файл уходит перед карточкой, карточка о нём говорит.
    /// </summary>
    public sealed record ApprovalCard(string Html, ApprovalAttachment? Attachment);

    /// <summary>Файл с тем, что в карточку не влезло: имя с расширением и содержимое.</summary>
    public sealed record ApprovalAttachment(string FileName, string Text);

    /// <summary>
    /// Фрагменты, обрезанные карточкой: без них команду с опасным хвостом или правку
    /// с лишним куском в конце одобрили бы вслепую.
    /// </summary>
    private sealed class Truncated
    {
        private readonly List<(string Title, string Text)> _items = [];
        private ApprovalAttachment? _document;

        /// <summary>Экранирует под бюджет и запоминает исходник, если он не влез целиком.</summary>
        public string Capped(string title, string text, int budget)
        {
            if (ChatHtml.Escape(text).Length > budget) _items.Add((title, text));
            return E(text, budget);
        }

        /// <summary>Запоминает то, что в карточке не показано вовсе (остальные правки, хвост файла).</summary>
        public void Add(string title, string text) => _items.Add((title, text));

        /// <summary>
        /// Целый документ вместо сводки «=== фрагмент ===»: .md чат открывает с разметкой,
        /// а .txt с заголовком-разделителем читался сырым текстом. Заменяет сводку целиком —
        /// у инструмента с документом других обрезанных полей нет.
        /// </summary>
        public void AddDocument(string fileName, string text) => _document = new(fileName, text);

        /// <param name="summaryName">Имя файла сводки, если документа нет.</param>
        public ApprovalAttachment? Render(string summaryName)
        {
            if (_document is { } document) return document;
            if (_items.Count == 0) return null;

            var text = new StringBuilder();
            foreach (var (title, body) in _items)
                text.Append("=== ").Append(title).Append(" ===\n").Append(body).Append("\n\n");

            return new ApprovalAttachment(summaryName, text.ToString().TrimEnd() + "\n");
        }
    }

    private static readonly HashSet<char> InvalidFileNameChars = [.. Path.GetInvalidFileNameChars()];

    /// <summary>Имя инструмента приходит от агента — в имени файла разделители путей недопустимы.</summary>
    private static string SafeFileName(string toolName)
    {
        var safe = new string([.. toolName.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c)]);
        return Text.Clip(safe.Length == 0 ? "tool" : safe, 40);
    }

    /// <param name="suggested">
    /// Правила, которые агент запишет у себя по кнопке «Всегда». Если они есть, показываем
    /// их вместо сигнатуры шлюза: правило агента префиксное и шире точного совпадения.
    /// </param>
    public static ApprovalCard Render(
        string toolName, JsonElement? input, string signature, IReadOnlyList<PersistentRule>? suggested, string projectPath)
    {
        var obj = input is { ValueKind: JsonValueKind.Object } o ? o : (JsonElement?)null;
        var card = new StringBuilder();
        var truncated = new Truncated();

        card.Append("🔐 <b>").Append(E(toolName, ToolNameBudget)).Append("</b>");
        var caption = Caption(toolName);
        if (caption is not null) card.Append(" — ").Append(caption);
        card.Append('\n');

        var rendered = obj is { } body && RenderKnown(toolName, body, projectPath, card, truncated);
        if (!rendered) RenderGeneric(input, card, truncated);

        // Блоки <pre> не всегда заканчиваются переводом строки — выравниваем перед подписью.
        if (card[^1] != '\n') card.Append('\n');

        // Предупреждение и файл ставим вместе: любое из них по отдельности ведёт
        // к одобрению вслепую.
        var attachment = truncated.Render($"{SafeFileName(toolName)}-input.txt");
        if (attachment is not null)
            card.Append("\n⚠️ <b>Показано не всё</b> — полный текст в файле выше. Не разрешайте, не прочитав его.\n");
        var rules = suggested?.Take(MaxSuggestedRules).Select(rule => rule.Display).ToList() ?? [];
        if (rules.Count > 0)
        {
            card.Append("\n<i>Кнопка «Всегда» запишет в настройки проекта у агента:</i>\n");
            foreach (var rule in rules)
                card.Append("• <code>").Append(E(Shorten(rule, projectPath), SignatureBudget / rules.Count)).Append("</code>\n");
        }
        else
        {
            card.Append("\n<i>Кнопка «Всегда» больше не спросит про: ")
                .Append(E(Unescape(Shorten(signature, projectPath)), SignatureBudget))
                .Append("</i>");
        }

        return new ApprovalCard(card.ToString(), attachment);
    }

    /// <summary>Сколько правил от агента показывать: больше он и не присылает.</summary>
    private const int MaxSuggestedRules = 4;

    /// <summary>Пояснение к имени инструмента: «Glob» в чате ничего не говорит.</summary>
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
        "ExitPlanMode" => "утвердить план",
        _ when toolName.StartsWith("mcp__", StringComparison.Ordinal) => "MCP-инструмент",
        _ => null,
    };

    private static bool RenderKnown(
        string toolName, JsonElement input, string projectPath, StringBuilder card, Truncated truncated)
    {
        switch (toolName)
        {
            case "Bash":
            case "PowerShell":
                if (Str(input, "command") is not { } command) return false;
                card.Append("<pre>").Append(truncated.Capped("Команда", command, CommandBudget)).Append("</pre>\n");
                AppendNote(card, Str(input, "description"));
                if (input.TryGetProperty("run_in_background", out var bg) && bg.ValueKind == JsonValueKind.True)
                    card.Append("⏳ в фоне\n");
                return true;

            case "Edit":
                if (Str(input, "file_path") is not { } editPath) return false;
                AppendPath(card, editPath, projectPath);
                AppendDiff(card, truncated, Str(input, "old_string"), Str(input, "new_string"));
                if (input.TryGetProperty("replace_all", out var all) && all.ValueKind == JsonValueKind.True)
                    card.Append("\n<i>заменить все вхождения</i>");
                return true;

            case "MultiEdit":
                if (Str(input, "file_path") is not { } multiPath) return false;
                AppendPath(card, multiPath, projectPath);
                if (input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                {
                    var list = edits.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToArray();
                    card.Append("Правок: ").Append(list.Length).Append('\n');
                    // Показываем только первую — остальные не влезут. Но одобряются все,
                    // поэтому они уходят в файл.
                    if (list.Length > 0)
                        AppendDiff(card, truncated, Str(list[0], "old_string"), Str(list[0], "new_string"));
                    for (var i = 1; i < list.Length; i++)
                    {
                        truncated.Add($"Правка {i + 1}: было", Str(list[i], "old_string") ?? "");
                        truncated.Add($"Правка {i + 1}: станет", Str(list[i], "new_string") ?? "");
                    }
                }
                return true;

            case "Write":
                if (Str(input, "file_path") is not { } writePath) return false;
                AppendPath(card, writePath, projectPath);
                // Превью — это начало файла; в конец могли дописать что угодно.
                if (Str(input, "content") is { } content && !AppendPreview(card, "Строк: ", content))
                    truncated.Add("Содержимое файла", content);
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
                card.Append("<pre>").Append(truncated.Capped("Задание сабагенту", prompt, ContentBudget)).Append("</pre>");
                return true;

            case "Skill":
                if (Str(input, "skill") is not { } skill) return false;
                card.Append("Навык: <code>").Append(E(skill, ValueBudget)).Append("</code>\n");
                if (Str(input, "args") is { } args) card.Append("<pre>").Append(E(args, ValueBudget)).Append("</pre>");
                return true;

            case "ExitPlanMode":
                // Пустой план отдаём общему рендеру: «Строк: 1» с пустым блоком бесполезно.
                if (Str(input, "plan") is not { Length: > 0 } rawPlan) return false;
                var plan = rawPlan.Replace("\r\n", "\n").Trim('\n');
                // План — markdown, поэтому целиком уходит файлом .md: чат и редакторы
                // покажут его с заголовками и списками, а не сплошным текстом.
                if (!AppendPreview(card, "📋 Строк: ", plan))
                    truncated.AddDocument("plan.md", PlanDocument(plan));
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Незнакомый инструмент: плоский объект — списком «поле: значение», остальное —
    /// отформатированным JSON с читаемыми буквами вместо \u-последовательностей.
    /// </summary>
    private static void RenderGeneric(JsonElement? input, StringBuilder card, Truncated truncated)
    {
        if (input is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } element) return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            var props = element.EnumerateObject().ToArray();
            if (props.Length == 0) return;

            if (props.All(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
            {
                // Бюджет общий на весь список: полей может быть сколько угодно, а слишком
                // длинное сообщение канал не примет — вместо карточки выйдет отказ.
                var left = RawInputBudget;

                foreach (var p in props)
                {
                    if (left <= 0)
                    {
                        card.Append("…\n");
                        truncated.Add("Вход целиком", JsonSerializer.Serialize(element, PrettyJson));
                        break;
                    }

                    var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                    var name = E(p.Name, Math.Min(ToolNameBudget, left));
                    left -= name.Length;

                    var shown = truncated.Capped(p.Name, value, Math.Min(ValueBudget, Math.Max(1, left)));
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
        card.Append("<pre>").Append(truncated.Capped("Вход целиком", pretty, RawInputBudget)).Append("</pre>");
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        // Иначе кириллица в JSON остаётся \u-последовательностями и карточка нечитаема.
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

    private static void AppendDiff(StringBuilder card, Truncated truncated, string? oldText, string? newText)
    {
        if (oldText is { Length: > 0 })
            card.Append("<b>Было:</b>\n<pre>").Append(truncated.Capped("Было", oldText, DiffSideBudget)).Append("</pre>\n");
        if (newText is not null)
            card.Append("<b>Станет:</b>\n<pre>")
                .Append(newText.Length == 0 ? "(пусто)" : truncated.Capped("Станет", newText, DiffSideBudget))
                .Append("</pre>\n");
    }

    /// <summary>Убирает префикс проекта: полный windows-путь съедает половину экрана телефона.</summary>
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

    /// <summary>
    /// Сигнатура без ключевого поля — это JSON входа, и многострочный текст попадал
    /// в карточку с буквальными «\n». Меняем только показ, ключ в state.json тот же.
    /// </summary>
    private static string Unescape(string signature) =>
        signature.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", "\t");

    /// <summary>
    /// Счётчик строк и первые <see cref="PreviewLines"/> строк в блоке <pre>. false —
    /// что-то осталось за кадром, полный текст должен уйти файлом.
    /// </summary>
    private static bool AppendPreview(StringBuilder card, string counter, string content)
    {
        var lines = content.Split('\n');
        card.Append(counter).Append(lines.Length).Append('\n');

        var preview = string.Join('\n', lines.Take(PreviewLines));
        var shown = E(preview, ContentBudget);
        var cut = ChatHtml.Escape(preview).Length > ContentBudget;

        card.Append("<pre>").Append(shown);
        // Если EscapeCapped уже обрезал, многоточие он поставил сам — второго не нужно.
        if (lines.Length > PreviewLines && !cut) card.Append("\n…");
        card.Append("</pre>");

        return lines.Length <= PreviewLines && !cut;
    }

    /// <summary>
    /// План как самостоятельный документ: без заголовка файл в просмотрщике начинается
    /// с середины, поэтому дописываем свой, если агент своего не поставил.
    /// </summary>
    private static string PlanDocument(string plan)
    {
        var titled = plan.TrimStart().StartsWith('#');
        return (titled ? plan : "# План\n\n" + plan) + "\n";
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string E(string text, int budget) => ChatHtml.EscapeCapped(text, budget);
}
