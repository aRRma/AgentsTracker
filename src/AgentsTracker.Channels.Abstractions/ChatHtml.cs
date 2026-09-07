using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentsTracker.Channels;

/// <summary>
/// Общий формат текста между хостом и каналом: подмножество HTML —
/// <c>b, i, s, code, pre, a href, blockquote</c>. В нём хост пишет карточки и экраны, канал
/// отдаёт как есть (Telegram) или переводит в свою разметку. Здесь же экранирование, которым
/// оборачивается всё пришедшее от пользователя и агента.
/// </summary>
public static partial class ChatHtml
{
    public static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>
    /// Экранирует и обрезает так, чтобы результат точно уложился в maxLength. Бюджет
    /// по исходной строке считать нельзя: один «&amp;» превращается в пять символов.
    /// </summary>
    public static string EscapeCapped(string text, int maxLength)
    {
        if (maxLength <= 0) return "";

        // Уложившийся целиком текст символа под многоточие не тратит: иначе строка ровно
        // в maxLength теряла бы последний символ, хотя обрезать было нечего.
        if (EscapedLength(text) <= maxLength) return Escape(text);

        // Один символ придерживаем под многоточие, чтобы обрезка не выходила за maxLength.
        var budget = maxLength - 1;
        var result = new StringBuilder(maxLength);

        foreach (var ch in text)
        {
            var entity = EntityOf(ch);

            if (result.Length + (entity?.Length ?? 1) > budget)
            {
                // Обрезка между половинками суррогатной пары (эмодзи) даёт невалидный UTF-16:
                // канал отвергнет сообщение, а карточка подтверждения превратится в отказ.
                if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result.Length--;
                result.Append('…');
                break;
            }

            if (entity is null) result.Append(ch);
            else result.Append(entity);
        }

        return result.ToString();
    }

    private static string? EntityOf(char ch) => ch switch
    {
        '&' => "&amp;",
        '<' => "&lt;",
        '>' => "&gt;",
        _ => null,
    };

    /// <summary>Длина экранированного текста без его сборки: нужна, чтобы решить, надо ли резать.</summary>
    private static int EscapedLength(string text)
    {
        var length = 0;
        foreach (var ch in text) length += EntityOf(ch)?.Length ?? 1;
        return length;
    }

    /// <summary>
    /// Снимает всю разметку, а не перечисленные вручную теги: иначе &lt;a href=…&gt; и
    /// &lt;code class="language-x"&gt; уезжают пользователю как есть. Сущности разворачиваем
    /// после удаления тегов, иначе экранированный текст сам стал бы разметкой.
    /// </summary>
    public static string StripTags(string html) => WebUtility.HtmlDecode(TagRegex().Replace(html, ""));

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagRegex();
}
