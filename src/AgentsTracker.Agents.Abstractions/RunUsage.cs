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
}

public sealed record ModelRunUsage(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens);
