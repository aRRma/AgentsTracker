namespace AgentsTracker.Gateway.Infrastructure.Monitoring;

public sealed record LogEntry(DateTimeOffset At, string Level, string Category, string Message, string? Exception);

/// <summary>
/// Хвост обычного лога в памяти — для страницы монитора. Консоль отдаёт русский текст
/// в cp866, и из браузера его читать проще. Debug сюда не пишется: там сырые payload-ы.
/// </summary>
public sealed class RingBufferLog
{
    private const int Kept = 500;

    private readonly Lock _gate = new();
    private readonly Queue<LogEntry> _entries = new(Kept);

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            if (_entries.Count > Kept) _entries.Dequeue();
        }
    }

    /// <summary>Последние записи, свежие в конце. minLevel — имя уровня в терминах ILogger.</summary>
    public IReadOnlyList<LogEntry> Tail(int count, LogLevel minLevel)
    {
        lock (_gate)
        {
            return [.. _entries.Where(e => Level(e.Level) >= minLevel).TakeLast(Math.Max(0, count))];
        }
    }

    public static LogLevel Level(string name) =>
        Enum.TryParse<LogLevel>(name, ignoreCase: true, out var level) ? level : LogLevel.Information;
}

public sealed class RingBufferLoggerProvider(RingBufferLog buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RingLogger(buffer, categoryName);

    public void Dispose() { }

    private sealed class RingLogger(RingBufferLog buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            buffer.Add(new LogEntry(
                DateTimeOffset.UtcNow,
                logLevel.ToString(),
                category.StartsWith("AgentsTracker.Gateway.", StringComparison.Ordinal) ? category["AgentsTracker.Gateway.".Length..] : category,
                formatter(state, exception),
                exception?.ToString()));
        }
    }
}
