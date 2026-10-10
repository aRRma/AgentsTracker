namespace AgentsTracker.Gateway.Infrastructure.State;

/// <summary>
/// Правила кэша сессии в одном месте: живой ли он, прочитает ли его следующий запуск и что
/// считать промахом. Кнопки меню, команды, «Статус» и статистика спрашивают здесь — иначе кнопка
/// спросила бы «точно?», а <c>/model</c> молча сбросил бы тот же кэш.
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

    /// <summary>
    /// Живой кэш не прочитается при таком выборе: модель или режим не те, на которых он записан.
    /// Без этой пометки «ещё 40 мин» обещало бы дешёвый запуск, который будет дорогим.
    /// </summary>
    /// <param name="model">
    /// Версия модели («opus-5-5»); null — модель по умолчанию или неизвестна. Её не считаем
    /// расхождением: по умолчанию обычно и отвечала та модель, на которой записан кэш.
    /// </param>
    /// <param name="mode">Режим со своим кэшем (<see cref="ModeScope"/>).</param>
    public static bool Mismatch(SessionRecord? session, string? model, string? mode) =>
        session is { CacheUntilUtc: { } until } && until > DateTimeOffset.UtcNow
        && (session.CacheModel is { } cached && model is not null && !string.Equals(model, cached, StringComparison.Ordinal)
            || !string.Equals(mode, session.CacheMode, StringComparison.Ordinal));

    /// <summary>
    /// Сбросит ли переход большой живой кэш: сейчас выбор его читает, а после смены — нет. Шаг
    /// обратно к модели и режиму кэша его спасает и потому не спрашивается.
    /// </summary>
    public static bool WillReset(
        SessionRecord? session, string? fromModel, string? toModel, string? fromMode, string? toMode) =>
        session is { ContextTokens: >= MinContextTokens }
        && !Mismatch(session, fromModel, fromMode)
        && Mismatch(session, toModel, toMode);
}
