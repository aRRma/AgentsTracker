namespace AgentsTracker.Agents;

/// <summary>
/// Всё, что нужно бэкенду для одного запуска. Хост фиксирует сессию, папку и настройки
/// до старта: переключение из меню по ходу работы иначе развело бы каталог процесса
/// и проект, которому запишется сессия.
/// </summary>
/// <param name="ResumeSessionId">Сессия, которую продолжаем; null — начать новую.</param>
/// <param name="NewSessionId">
/// Id новой сессии, выданный хостом заранее. Бэкенд, умеющий принимать id снаружи, передаёт
/// его агенту и сообщает через <see cref="IAgentRunObserver.SessionStarted"/>, как только
/// процесс поднялся: так /stop или падение первого запуска не теряют ветку.
/// </param>
/// <param name="Model">Модель или её алиас; null — как решит агент.</param>
/// <param name="Effort">Уровень усилий; null — как решит агент. У агента без этой настройки игнорируется.</param>
/// <param name="PermissionMode">Режим разрешений — одно из значений <see cref="AgentCapabilities.PermissionMode"/>.</param>
/// <param name="Timeout">Предельная длительность: по истечении процесс убивается.</param>
public sealed record AgentRunRequest(
    string Prompt,
    string ProjectPath,
    string? ResumeSessionId,
    string NewSessionId,
    string? Model,
    string? Effort,
    string PermissionMode,
    TimeSpan Timeout);

/// <summary>
/// Что бэкенд сообщает по ходу запуска. <see cref="Activity"/> зовётся из потока чтения
/// вывода: обработчик должен быть быстрым и не бросать, иначе застопорит разбор.
/// </summary>
public interface IAgentRunObserver
{
    /// <summary>Процесс поднялся с этой сессией — её можно продолжить, даже если запуск оборвут.</summary>
    void SessionStarted(string sessionId);

    /// <summary>Агент позвал инструмент.</summary>
    void Activity(RunActivity activity);
}

/// <summary>Итог одного запуска в виде, пригодном для отправки в чат.</summary>
public sealed record AgentRunResult
{
    public required bool Ok { get; init; }

    /// <summary>Текст для пользователя: ответ агента либо описание ошибки.</summary>
    public required string Text { get; init; }

    /// <summary>Сессия, в которой шёл запуск, по версии агента. null — сессии нет или она потеряна.</summary>
    public string? SessionId { get; init; }

    public TimeSpan Duration { get; init; }
    public bool Cancelled { get; init; }

    /// <summary>
    /// Запуск оборвался на лимите тарифа. Очередь после такого ждёт сброса: следующая
    /// задача упёрлась бы в тот же лимит, а платить кредитами шлюзу запрещено.
    /// </summary>
    public bool RateLimited { get; init; }

    /// <summary>
    /// Агент прямо сказал, что не нашёл <see cref="AgentRunRequest.ResumeSessionId"/>. Только
    /// тогда хост сбрасывает активную сессию: битый id переживёт перезапуск и будет валить
    /// каждый запуск, а при других сбоях терять контекст хуже, чем повторить.
    /// </summary>
    public bool SessionLost { get; init; }

    /// <summary>Расход запуска. null — агент ничего не сказал (например, не смог стартовать).</summary>
    public RunUsage? Usage { get; init; }

    public static AgentRunResult Failure(string text, TimeSpan duration) =>
        new() { Ok = false, Text = text, Duration = duration };
}
