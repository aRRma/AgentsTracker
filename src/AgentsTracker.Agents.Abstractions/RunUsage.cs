namespace AgentsTracker.Agents;

/// <summary>Расход одного запуска агента — то, что шлюз кладёт в статистику.</summary>
public sealed record RunUsage
{
    public int Turns { get; init; }
    public long DurationMs { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }

    /// <summary>Разбивка по моделям: за один запуск их может быть несколько.</summary>
    public IReadOnlyList<ModelRunUsage> Models { get; init; } = [];

    /// <summary>
    /// Короткое имя модели, которая отвечала («opus-5-5»): им подписан ответ. Выбранная
    /// в меню модель не то же самое — агент вправе подменить её. null — модель не вызывалась.
    /// </summary>
    public string? PrimaryModel { get; init; }

    /// <summary>
    /// Сколько токенов занимал контекст к концу запуска. Не сумма <see cref="InputTokens"/>:
    /// та копит каждый ход, а контекст — это размер последнего. 0 — неизвестно.
    /// </summary>
    public long ContextTokens { get; init; }

    /// <summary>Размер окна контекста основной модели. 0 — неизвестно.</summary>
    public long ContextWindow { get; init; }

    /// <summary>
    /// Начало последнего запроса основной ветки к модели: от него, а не от конца ответа,
    /// отсчитывается жизнь кэша — длинный ответ съедает часть срока. null — модель не вызывалась
    /// или контекст сжат после последнего запроса, и старый кэш к сессии уже не относится.
    /// </summary>
    public DateTimeOffset? LastRequestUtc { get; init; }

    /// <summary>
    /// Сколько модель держит кэш после запроса: час на подписке, пять минут при оплате сверх
    /// неё или по ключу API. null — запуск ничего не записал в кэш, срок не узнать.
    /// </summary>
    public TimeSpan? CacheTtl { get; init; }
}

public sealed record ModelRunUsage(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens);
