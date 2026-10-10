namespace AgentsTracker.Gateway.Infrastructure.State;

/// <summary>
/// Сбросит ли смена модели или режима тёплый кэш большой сессии. Одно правило для кнопок меню
/// и для команд: иначе кнопка спросила бы «точно?», а <c>/model</c> молча сбросил бы тот же кэш.
/// </summary>
public static class CacheGuard
{
    /// <summary>
    /// С какого контекста стоит предупреждать. Меньше — запись заново стоит как несколько
    /// обычных шагов, и лишний вопрос раздражал бы сильнее, чем потеря.
    /// </summary>
    public const long MinContextTokens = 100_000;

    /// <summary>
    /// С какого прошлого контекста считать промахи. Инструменты и системный промпт — около 20k,
    /// они общие у всех сессий одной модели; меньше — промахиваться почти нечем.
    /// </summary>
    public const long MissContextTokens = 20_000;

    /// <summary>
    /// Промах: первый запрос продолжения прочитал из кэша меньше половины прошлого контекста —
    /// сессия записала себя заново (пауза дольше срока, смена модели или режима).
    /// </summary>
    /// <param name="previousContext">Контекст сессии после прошлого запуска.</param>
    /// <param name="firstCacheRead">Чтение из кэша первым запросом этого запуска.</param>
    public static bool Miss(long previousContext, long? firstCacheRead) =>
        previousContext >= MissContextTokens && firstCacheRead is { } read && read < previousContext / 2;

    /// <summary>Режим со своим кэшем; null — режим из группы с общим кэшем.</summary>
    public static string? ModeScope(AgentCapabilities capabilities, string mode) =>
        capabilities.OwnCacheModes?.Contains(mode, StringComparer.Ordinal) == true ? mode : null;

    /// <summary>Кэш сессии ещё жив и достаточно велик, чтобы его потеря была заметна.</summary>
    public static bool Worth(SessionRecord? session) =>
        session is { CacheUntilUtc: { } until, ContextTokens: >= MinContextTokens } && until > DateTimeOffset.UtcNow;

    /// <summary>
    /// Живой кэш не прочитается: выбор в меню уже ушёл от модели или режима, на которых он записан.
    /// Без этой пометки «ещё 40 мин» обещало бы дешёвый запуск, который будет дорогим.
    /// </summary>
    /// <param name="model">Версия выбранной модели; null — неизвестна, и сравнивать не с чем.</param>
    /// <param name="mode">Режим со своим кэшем, выбранный сейчас (<see cref="ModeScope"/>).</param>
    public static bool Mismatch(SessionRecord? session, string? model, string? mode) =>
        session is { CacheUntilUtc: { } until } && until > DateTimeOffset.UtcNow
        && (session.CacheModel is { } cached && model is not null && !string.Equals(model, cached, StringComparison.Ordinal)
            || !string.Equals(mode, session.CacheMode, StringComparison.Ordinal));

    /// <summary>
    /// Сбросит ли переход кэш. Сбрасывает только шаг, который уводит от записанного: если выбор
    /// ушёл от него раньше, кэш и так не прочитается, а возврат к нему кэш спасает.
    /// </summary>
    /// <param name="fromModel">Версия модели сейчас («opus-5-5»); null — неизвестна.</param>
    /// <param name="toModel">Версия после смены; null — неизвестна, и предупреждать не о чем.</param>
    /// <param name="fromMode">Режим со своим кэшем сейчас (<see cref="ModeScope"/>).</param>
    /// <param name="toMode">Режим со своим кэшем после смены.</param>
    public static bool WillReset(
        SessionRecord? session, string? fromModel, string? toModel, string? fromMode, string? toMode)
    {
        if (!Worth(session)) return false;

        // Модель «по умолчанию» (fromModel — null) отвечала на прошлом запуске, и кэш записан на ней:
        // без этой ветки уход с неё молча сбрасывал бы кэш, а предупреждение молчало.
        var model = session!.CacheModel is { } cached
                    && (fromModel is null || string.Equals(fromModel, cached, StringComparison.Ordinal))
                    && toModel is not null && !string.Equals(toModel, cached, StringComparison.Ordinal);

        var mode = string.Equals(fromMode, session.CacheMode, StringComparison.Ordinal)
                   && !string.Equals(toMode, session.CacheMode, StringComparison.Ordinal);

        return model || mode;
    }
}
