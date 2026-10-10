using System.Text.RegularExpressions;

namespace AgentsTracker.Agents;

/// <summary>
/// Обрезка текста под лимит чата, кнопки или журнала — одно правило на всех. Лежит рядом
/// с контрактами: бэкенды режут ей аргументы шагов и описания скиллов, хост — промпты
/// для аудита, а две копии разошлись бы.
/// </summary>
public static partial class Text
{
    private const string Mask = "***";

    /// <summary>Не длиннее limit символов; хвост заменяется многоточием.</summary>
    public static string Clip(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)].TrimEnd() + "…";

    /// <summary>
    /// Строка-превью для журнала: переводы строк схлопываются, секреты маскируются, длина
    /// ограничена. В аудит идёт суть, а не полный текст — пользователь мог вставить туда что угодно.
    /// </summary>
    public static string Preview(string text, int limit = 80) =>
        ClipRedacted(text.ReplaceLineEndings(" ").Trim(), limit);

    /// <summary>
    /// Обрезка с маской секретов. Маска раньше окончательной обрезки — по ту сторону предела
    /// обрезанный токен уже не узнать. Грубая обрезка до маски — чтобы не гонять регулярки
    /// по многокилобайтному плану или содержимому файла.
    /// </summary>
    public static string ClipRedacted(string text, int limit) =>
        Clip(Redact(Clip(text, limit * 4)), limit);

    /// <summary>
    /// Секреты заменены на «***»: токены известного вида, заголовок Authorization, пароль в
    /// адресе и значения ключей вроде <c>token=</c> или <c>--password</c>. Нужно всему, что
    /// оседает на диске или уходит в монитор: в подтверждённой команде бывает токен бота
    /// из переменной окружения, а журнал без пароля отдаёт монитор. Маскировать до обрезки:
    /// обрезанный токен уже не узнать по форме, и его хвост ушёл бы в журнал.
    /// </summary>
    public static string Redact(string text)
    {
        if (text.Length == 0) return text;

        text = UrlCredentials().Replace(text, Mask);
        text = AuthorizationHeader().Replace(text, m => m.Groups["head"].Value + Mask);
        text = BearerToken().Replace(text, m => m.Groups["head"].Value + Mask);
        text = KnownTokens().Replace(text, Mask);
        text = SecretFlag().Replace(text, m => m.Groups["head"].Value + m.Groups["quote"].Value + Mask);
        text = SecretAssignment().Replace(text, m => m.Groups["head"].Value + m.Groups["quote"].Value + Mask);
        return text;
    }

    /// <summary>Логин и пароль в адресе: <c>https://user:pass@host</c>.</summary>
    [GeneratedRegex(@"(?<=://)[^/\s:@'""]+:[^/\s@'""]+(?=@)")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?<head>authorization\s*[:=]\s*(?:(?:bearer|basic|token|bot)\s+)?)[^\s'""]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"(?<head>\bbearer\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    /// <summary>
    /// Токены, которые узнаются по форме без имени переменной: бот Telegram, Anthropic и прочие
    /// <c>sk-</c>, GitHub, AWS, Slack, JWT.
    /// </summary>
    [GeneratedRegex(
        @"(?<![0-9])[0-9]{6,12}:[A-Za-z0-9_-]{20,}" +
        @"|\bsk-[A-Za-z0-9_-]{16,}" +
        @"|\bgh[pousr]_[A-Za-z0-9]{20,}" +
        @"|\bgithub_pat_[A-Za-z0-9_]{20,}" +
        @"|\bAKIA[0-9A-Z]{16}\b" +
        @"|\bxox[abpr]-[A-Za-z0-9-]{10,}" +
        @"|\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex KnownTokens();

    /// <summary>Ключ командной строки: <c>--token abc</c>, <c>--api-key "abc"</c>.</summary>
    [GeneratedRegex(
        @"(?<head>(?<![\w-])--?[\w-]*(?:token|secret|passw(?:or)?d|api-?key)[\w-]*\s+)(?<quote>[""']?)(?:(?<=[""'])[^""']+|[^\s""')]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretFlag();

    /// <summary>
    /// Присваивание и пара в JSON: <c>BotToken='…'</c>, <c>$env:API_KEY = "…"</c>,
    /// <c>"password": "…"</c>, <c>--token=…</c>.
    /// </summary>
    [GeneratedRegex(
        @"(?<head>[\w.$:-]*(?:token|secret|passw(?:or)?d|pwd|api[_-]?key|apikey|access[_-]?key|private[_-]?key|credential)s?[\w.-]*[""']?\s*[:=]\s*)(?<quote>[""']?)(?:(?<=[""'])[^""']+|[^\s""'&;,)]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();
}
