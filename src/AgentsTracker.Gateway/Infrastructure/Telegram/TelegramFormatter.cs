using System.Text;
using System.Text.RegularExpressions;

namespace AgentsTracker.Gateway.Infrastructure.Telegram;

/// <summary>Одна исходящая порция: либо готовый HTML для сообщения, либо файл.</summary>
public sealed record OutgoingPart(string? Html, string? DocumentText, string? DocumentName)
{
    public static OutgoingPart Message(string html) => new(html, null, null);
    public static OutgoingPart Document(string text, string name) => new(null, text, name);
}

/// <summary>
/// Переводит markdown из ответа Claude в подмножество HTML, которое понимает Telegram,
/// и режет результат под лимит сообщения (4096 символов).
/// </summary>
public static partial class TelegramFormatter
{
    public const int MaxMessageLength = 3800;

    public static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>
    /// Экранирует и обрезает так, чтобы результат гарантированно уложился в maxLength.
    /// Считать бюджет по исходной строке нельзя: один символ «&amp;» превращается в пять.
    /// </summary>
    public static string EscapeCapped(string text, int maxLength)
    {
        // Один символ придерживаем под многоточие, чтобы обрезка не выходила за maxLength.
        var budget = maxLength - 1;
        var result = new StringBuilder(Math.Min(text.Length, maxLength));

        foreach (var ch in text)
        {
            var entity = ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                _ => null,
            };

            if (result.Length + (entity?.Length ?? 1) > budget)
            {
                // Обрезка между половинками суррогатной пары (эмодзи) даёт невалидный UTF-16:
                // Telegram отвергнет сообщение, а карточка подтверждения превратится в отказ.
                if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result.Length--;
                result.Append('…');
                break;
            }

            if (entity is null) result.Append(ch);
            else result.Append(entity);
        }

        return result.ToString();
    }

    public static IReadOnlyList<OutgoingPart> Render(string markdown, int maxLength = MaxMessageLength)
    {
        var parts = new List<OutgoingPart>();
        var current = new StringBuilder();
        var documentIndex = 0;

        void Flush()
        {
            if (current.Length == 0) return;
            parts.Add(OutgoingPart.Message(current.ToString().TrimEnd('\n')));
            current.Clear();
        }

        void Append(string html)
        {
            // +1 на перевод строки между блоками.
            if (current.Length > 0 && current.Length + html.Length + 1 > maxLength) Flush();
            if (current.Length > 0) current.Append('\n');
            current.Append(html);
        }

        foreach (var block in Tokenize(markdown))
        {
            if (block.IsCode)
            {
                var cls = block.Language is { Length: > 0 } l
                    ? $" class=\"language-{Escape(l)}\""
                    : "";

                // Меряем готовый HTML: экранирование и теги раздувают исходник,
                // а лимит Telegram считается по тому, что реально уходит в сообщение.
                var html = $"<pre><code{cls}>{Escape(block.Content)}</code></pre>";

                if (html.Length > maxLength)
                {
                    Flush();
                    var name = block.Language is { Length: > 0 } lang
                        ? $"fragment{++documentIndex}.{SanitizeExtension(lang)}"
                        : $"fragment{++documentIndex}.txt";
                    parts.Add(OutgoingPart.Document(block.Content, name));
                    continue;
                }

                Append(html);
                continue;
            }

            foreach (var piece in SplitPlainText(block.Content, maxLength))
                foreach (var html in RenderInlineCapped(piece, maxLength))
                    Append(html);
        }

        Flush();

        if (parts.Count == 0)
            parts.Add(OutgoingPart.Message("(пустой ответ)"));

        return parts;
    }

    // ---- разбор на блоки ----

    private readonly record struct Block(bool IsCode, string Content, string? Language);

    private static IEnumerable<Block> Tokenize(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var buffer = new StringBuilder();
        var inCode = false;
        string? language = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCode)
                {
                    if (buffer.Length > 0)
                    {
                        foreach (var block in SplitTables(buffer.ToString())) yield return block;
                        buffer.Clear();
                    }

                    inCode = true;
                    language = trimmed[3..].Trim();
                    if (language.Length == 0) language = null;
                }
                else
                {
                    yield return new Block(true, buffer.ToString().TrimEnd('\n'), language);
                    buffer.Clear();
                    inCode = false;
                    language = null;
                }

                continue;
            }

            buffer.Append(line).Append('\n');
        }

        if (buffer.Length == 0) yield break;

        if (inCode) yield return new Block(true, buffer.ToString().TrimEnd('\n'), language);
        else foreach (var block in SplitTables(buffer.ToString())) yield return block;
    }

    /// <summary>
    /// Выделяет из обычного текста markdown-таблицы и отдаёт их как блоки кода.
    /// В HTML Telegram таблиц нет: без выравнивания моноширинным шрифтом столбцы
    /// расползаются и читать нечего.
    /// </summary>
    private static IEnumerable<Block> SplitTables(string content)
    {
        var lines = content.Split('\n');
        var text = new StringBuilder();

        for (var i = 0; i < lines.Length;)
        {
            var isTable = i + 1 < lines.Length
                && lines[i].Contains('|', StringComparison.Ordinal)
                && TableDividerRegex().IsMatch(lines[i + 1]);

            if (!isTable)
            {
                text.Append(lines[i]).Append('\n');
                i++;
                continue;
            }

            var rows = new List<string[]> { ParseTableRow(lines[i]) };
            var j = i + 2;
            while (j < lines.Length && lines[j].Contains('|', StringComparison.Ordinal) && lines[j].Trim().Length > 0)
                rows.Add(ParseTableRow(lines[j++]));

            if (text.Length > 0)
            {
                yield return new Block(false, text.ToString(), null);
                text.Clear();
            }

            yield return new Block(true, FormatTable(rows), null);
            i = j;
        }

        if (text.Length > 0) yield return new Block(false, text.ToString(), null);
    }

    private static string[] ParseTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return [.. trimmed.Split('|').Select(c => c.Trim())];
    }

    private static string FormatTable(List<string[]> rows)
    {
        var columns = rows.Max(r => r.Length);
        var widths = new int[columns];

        foreach (var row in rows)
            for (var c = 0; c < row.Length; c++)
                widths[c] = Math.Max(widths[c], row[c].Length);

        var result = new StringBuilder();

        for (var r = 0; r < rows.Count; r++)
        {
            var cells = Enumerable.Range(0, columns)
                .Select(c => (c < rows[r].Length ? rows[r][c] : "").PadRight(widths[c]));

            result.Append(string.Join(" | ", cells).TrimEnd()).Append('\n');

            // Линия под шапкой: без неё заголовок сливается с данными.
            if (r == 0)
                result.Append(string.Join("-+-", widths.Select(w => new string('-', w)))).Append('\n');
        }

        return result.ToString().TrimEnd('\n');
    }

    /// <summary>Режет обычный текст по абзацам, затем по строкам, затем жёстко.</summary>
    private static IEnumerable<string> SplitPlainText(string text, int maxLength)
    {
        text = text.Trim('\n');
        if (text.Length == 0) yield break;

        if (text.Length <= maxLength)
        {
            yield return text;
            yield break;
        }

        foreach (var chunk in SplitBy(text, "\n\n", maxLength))
        {
            if (chunk.Length <= maxLength) { yield return chunk; continue; }

            foreach (var line in SplitBy(chunk, "\n", maxLength))
            {
                if (line.Length <= maxLength) { yield return line; continue; }

                for (var i = 0; i < line.Length; i += maxLength)
                    yield return line.Substring(i, Math.Min(maxLength, line.Length - i));
            }
        }
    }

    private static IEnumerable<string> SplitBy(string text, string separator, int maxLength)
    {
        var buffer = new StringBuilder();

        foreach (var segment in text.Split(separator))
        {
            var candidateLength = buffer.Length == 0 ? segment.Length : buffer.Length + separator.Length + segment.Length;

            if (buffer.Length > 0 && candidateLength > maxLength)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (buffer.Length > 0) buffer.Append(separator);
            buffer.Append(segment);
        }

        if (buffer.Length > 0) yield return buffer.ToString();
    }

    // ---- инлайновая разметка ----

    private const char Sentinel = '\u0001';

    private static string RenderInline(string text)
    {
        // Содержимое `...` вынимаем до экранирования, чтобы внутри не сработали остальные правила.
        var spans = new List<string>();
        var withPlaceholders = InlineCodeRegex().Replace(text, m =>
        {
            spans.Add(m.Groups[1].Value);
            return $"{Sentinel}{spans.Count - 1}{Sentinel}";
        });

        var html = Escape(withPlaceholders);

        // Кавычка в URL иначе закрыла бы атрибут href и впустила в сообщение произвольный HTML.
        html = LinkRegex().Replace(html, m => $"<a href=\"{m.Groups[2].Value.Replace("\"", "&quot;")}\">{m.Groups[1].Value}</a>");
        html = BoldRegex().Replace(html, "<b>$1</b>");
        html = StrikeRegex().Replace(html, "<s>$1</s>");
        html = HeadingRegex().Replace(html, "<b>$1</b>");

        // Курсив только с «*» и только когда звёздочки вплотную к тексту: «*.cs и *.md»
        // или «2 * 3 * 4» иначе превращались бы в курсив. «_» не разбираем вовсе —
        // он встречается в именах (__init__.py, snake_case) чаще, чем как разметка.
        html = ItalicRegex().Replace(html, "<i>$1</i>");

        html = ApplyLineBlocks(html);

        for (var i = 0; i < spans.Count; i++)
            html = html.Replace($"{Sentinel}{i}{Sentinel}", $"<code>{Escape(spans[i])}</code>", StringComparison.Ordinal);

        return html;
    }

    /// <summary>
    /// Построчные элементы markdown, которых в HTML Telegram нет: маркеры списка,
    /// горизонтальная линия, цитата. Без этого «- пункт» и «---» уходят в чат как есть.
    /// </summary>
    private static string ApplyLineBlocks(string html)
    {
        var result = new StringBuilder();
        var quote = new List<string>();

        void FlushQuote()
        {
            if (quote.Count == 0) return;
            result.Append("<blockquote>").Append(string.Join('\n', quote)).Append("</blockquote>").Append('\n');
            quote.Clear();
        }

        foreach (var line in html.Split('\n'))
        {
            var quoted = QuoteRegex().Match(line);
            if (quoted.Success)
            {
                quote.Add(quoted.Groups[1].Value);
                continue;
            }

            FlushQuote();

            if (HorizontalRuleRegex().IsMatch(line))
            {
                result.Append("──────────").Append('\n');
                continue;
            }

            // Вложенный уровень отличаем пустым кружком — отступ в Telegram сохраняется,
            // но одинаковые маркеры на разных уровнях сливаются в одну кашу.
            result
                .Append(BulletRegex().Replace(line, m => m.Groups[1].Value.Length > 0 ? $"{m.Groups[1].Value}◦ " : "• "))
                .Append('\n');
        }

        FlushQuote();
        return result.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Рендерит кусок текста так, чтобы каждая отданная часть уложилась в лимит уже
    /// после экранирования и вставки тегов. Режем исходник (а не HTML) — теги не рвутся.
    /// </summary>
    private static IEnumerable<string> RenderInlineCapped(string text, int maxLength)
    {
        var html = RenderInline(text);

        if (html.Length <= maxLength || text.Length <= 1)
        {
            yield return html;
            yield break;
        }

        var split = SplitPoint(text);

        foreach (var part in RenderInlineCapped(text[..split], maxLength)) yield return part;
        foreach (var part in RenderInlineCapped(text[split..], maxLength)) yield return part;
    }

    /// <summary>Точка разреза около середины, по возможности на переводе строки или пробеле.</summary>
    private static int SplitPoint(string text)
    {
        var middle = text.Length / 2;

        var newline = text.LastIndexOf('\n', middle);
        if (newline > 0) return newline + 1;

        var space = text.LastIndexOf(' ', middle);
        return space > 0 ? space + 1 : middle;
    }

    private static string SanitizeExtension(string language)
    {
        var cleaned = new string([.. language.Where(char.IsLetterOrDigit)]).ToLowerInvariant();
        return cleaned.Length is > 0 and <= 12 ? cleaned : "txt";
    }

    [GeneratedRegex("`([^`\\n]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\[([^\]\n]+)\]\((https?://[^\s)]+)\)")]
    private static partial Regex LinkRegex();

    // Только «**»: вариант с «__» превращал __init__.py в жирный «init», а Claude им не пишет.
    [GeneratedRegex(@"\*\*([^\n*]+)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"~~([^\n~]+)~~")]
    private static partial Regex StrikeRegex();

    [GeneratedRegex(@"^#{1,6}[ \t]+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    // Звёздочки вплотную к содержимому и на границе слова: «2 * 3» и «*.cs» не курсив.
    [GeneratedRegex(@"(?<![\w*])\*([^\s*][^*\n]*[^\s*]|[^\s*])\*(?![\w*])")]
    private static partial Regex ItalicRegex();

    // Строка уже экранирована, поэтому «>» ищем как &gt;.
    [GeneratedRegex(@"^&gt;[ \t]?(.*)$")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"^[ \t]*([-*_])(?:[ \t]*\1){2,}[ \t]*$")]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"^([ \t]*)[-*+][ \t]+")]
    private static partial Regex BulletRegex();

    // Вторая строка таблицы: |---|:--:| — минимум один дефис и только служебные символы.
    [GeneratedRegex(@"^[ \t]*\|?[ \t:|-]*-[ \t:|-]*\|[ \t:|-]*$")]
    private static partial Regex TableDividerRegex();
}
