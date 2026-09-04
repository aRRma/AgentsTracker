using System.Text;
using System.Text.RegularExpressions;

namespace AgentsTracker.Gateway.Telegram;

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
                        yield return new Block(false, buffer.ToString(), null);
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

        if (buffer.Length > 0)
            yield return new Block(inCode, buffer.ToString().TrimEnd('\n'), language);
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

        // Курсив намеренно не разбираем: одиночные * и _ слишком часто встречаются
        // в путях, именах и коде, и разметка ломается чаще, чем помогает.

        for (var i = 0; i < spans.Count; i++)
            html = html.Replace($"{Sentinel}{i}{Sentinel}", $"<code>{Escape(spans[i])}</code>", StringComparison.Ordinal);

        return html;
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
}
