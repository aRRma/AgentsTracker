namespace AgentsTracker.Agents;

/// <summary>
/// Окно лимита тарифа. Key — ключ по версии агента (у Claude: <c>five_hour</c>, <c>seven_day</c>,
/// <c>seven_day_&lt;модель&gt;</c>). Used — доля израсходованного окна, 0..1.
/// </summary>
public sealed record LimitWindow(string Key, double Used, DateTimeOffset? ResetsAt);

/// <summary>Состояние кредитов сверх тарифа («extra usage») на аккаунте: их шлюз тратить не даёт.</summary>
public sealed record ExtraUsageState(bool IsEnabled, double? UsedCredits);

/// <summary>Снимок лимитов либо причина, по которой его не удалось получить.</summary>
public sealed record LimitsSnapshot(
    IReadOnlyList<LimitWindow> Windows,
    ExtraUsageState? ExtraUsage,
    DateTimeOffset FetchedUtc,
    string? Error);

/// <summary>
/// Лимиты тарифа агента. Хост спрашивает их перед каждым запуском и показывает остаток в меню.
/// Любая ошибка опроса должна <b>пропускать</b> запуск, а не блокировать: иначе отвалившийся
/// эндпоинт заставил бы шлюз замолчать целиком.
/// </summary>
public interface IAgentLimits
{
    /// <summary>Причина отказа, если окно тарифа исчерпано, иначе null. model — модель следующего запуска.</summary>
    Task<string?> RefusalAsync(string? model, CancellationToken ct);

    Task<LimitsSnapshot> GetAsync(CancellationToken ct);

    /// <summary>Остаток окон одной строкой для шапки меню; пусто — показывать нечего.</summary>
    Task<string> ShortSummaryAsync(string? model, CancellationToken ct);

    /// <summary>Остаток окон построчно для экрана статистики; пусто — окон нет.</summary>
    Task<IReadOnlyList<string>> RemainingLinesAsync(string? model, CancellationToken ct);
}

/// <summary>Для агента без лимитов тарифа: ничего не запрещает и ничего не показывает.</summary>
public sealed class NoAgentLimits : IAgentLimits
{
    public Task<string?> RefusalAsync(string? model, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<LimitsSnapshot> GetAsync(CancellationToken ct) =>
        Task.FromResult(new LimitsSnapshot([], null, DateTimeOffset.UtcNow, null));

    public Task<string> ShortSummaryAsync(string? model, CancellationToken ct) => Task.FromResult("");

    public Task<IReadOnlyList<string>> RemainingLinesAsync(string? model, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
