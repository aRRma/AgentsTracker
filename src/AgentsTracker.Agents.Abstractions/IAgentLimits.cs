namespace AgentsTracker.Agents;

/// <summary>
/// Окно лимита тарифа. Key — ключ по версии агента (у Claude: <c>five_hour</c>, <c>seven_day</c>,
/// <c>seven_day_&lt;модель&gt;</c>). Used — доля израсходованного окна, 0..1.
/// </summary>
public sealed record LimitWindow(string Key, double Used, DateTimeOffset? ResetsAt);

/// <summary>Состояние кредитов сверх тарифа («extra usage») на аккаунте: их шлюз тратить не даёт.</summary>
public sealed record ExtraUsageState(bool IsEnabled, double? UsedCredits);

/// <summary>
/// Окно для шкалы в чате: подпись, остаток 0..1 и сброс — момент плюс готовая подпись
/// («в 18:00», «9 сентября в 10:00») в формате агента.
/// </summary>
public sealed record LimitGauge(string Title, double Remaining, DateTimeOffset? ResetsAt, string? ResetLabel);

/// <summary>Окна для шкал либо причина, по которой опрос не удался (тогда окон нет).</summary>
public sealed record LimitsView(IReadOnlyList<LimitGauge> Windows, string? Error);

/// <summary>Снимок лимитов либо причина, по которой его не удалось получить.</summary>
public sealed record LimitsSnapshot(
    IReadOnlyList<LimitWindow> Windows,
    ExtraUsageState? ExtraUsage,
    DateTimeOffset FetchedUtc,
    string? Error);

/// <summary>
/// Лимиты тарифа. Хост спрашивает их перед каждым запуском и показывает остаток в меню.
/// Ошибка опроса должна запуск <b>пропускать</b>, а не блокировать: иначе отвалившийся
/// эндпоинт заставил бы шлюз замолчать целиком.
/// </summary>
public interface IAgentLimits
{
    /// <summary>Причина отказа, если окно тарифа исчерпано, иначе null. model — модель следующего запуска.</summary>
    Task<string?> RefusalAsync(string? model, CancellationToken ct);

    Task<LimitsSnapshot> GetAsync(CancellationToken ct);

    /// <summary>Остаток окон одной строкой для шапки меню; пусто — показывать нечего.</summary>
    Task<string> ShortSummaryAsync(string? model, CancellationToken ct);

    /// <summary>
    /// Окна с остатком для шкал в статусе и статистике, ближайший сброс первым.
    /// model — модель следующего запуска.
    /// </summary>
    Task<LimitsView> ViewAsync(string? model, CancellationToken ct);
}

/// <summary>Для агента без лимитов тарифа: ничего не запрещает и ничего не показывает.</summary>
public sealed class NoAgentLimits : IAgentLimits
{
    public Task<string?> RefusalAsync(string? model, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<LimitsSnapshot> GetAsync(CancellationToken ct) =>
        Task.FromResult(new LimitsSnapshot([], null, DateTimeOffset.UtcNow, null));

    public Task<string> ShortSummaryAsync(string? model, CancellationToken ct) => Task.FromResult("");

    public Task<LimitsView> ViewAsync(string? model, CancellationToken ct) =>
        Task.FromResult(new LimitsView([], null));
}
