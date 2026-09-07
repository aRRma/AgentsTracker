using System.Text;
using System.Text.RegularExpressions;

namespace AgentsTracker.Channels;

/// <summary>
/// Канонический формат текста между хостом и каналом: подмножество HTML —
/// <c>b, i, s, code, pre, a href, blockquote</c>. Хост пишет в нём карточки и экраны, канал
/// либо отдаёт как есть (Telegram), либо переводит в свою разметку. Здесь — экранирование,
/// которым хост оборачивает всё, что пришло от пользователя и агента.
/// </summary>
public static partial class ChatHtml
{
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

    /// <summary>
    /// Снимает всю разметку, а не перечисленные вручную теги: иначе &lt;a href=…&gt; и
    /// &lt;code class="language-x"&gt; уезжают пользователю как есть. Сущности разворачиваем
    /// после удаления тегов, иначе экранированный текст сам стал бы разметкой.
    /// </summary>
    public static string StripTags(string html) => TagRegex().Replace(html, "")
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&amp;", "&");

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagRegex();
}
