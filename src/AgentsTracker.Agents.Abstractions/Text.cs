namespace AgentsTracker.Agents;

/// <summary>
/// Обрезка текста под лимит чата, кнопки или журнала — одно правило на всех. Лежит рядом
/// с контрактами: бэкенды режут ей аргументы шагов и описания скиллов, хост — промпты
/// для аудита, а две копии разошлись бы.
/// </summary>
public static class Text
{
    /// <summary>Не длиннее limit символов; хвост заменяется многоточием.</summary>
    public static string Clip(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)].TrimEnd() + "…";

    /// <summary>
    /// Строка-превью для журнала: переводы строк схлопываются, длина ограничена. В аудит
    /// идёт суть, а не полный текст — пользователь мог вставить туда что угодно.
    /// </summary>
    public static string Preview(string text, int limit = 80) =>
        Clip(text.ReplaceLineEndings(" ").Trim(), limit);
}
