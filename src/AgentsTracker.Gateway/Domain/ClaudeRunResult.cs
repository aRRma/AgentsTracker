namespace AgentsTracker.Gateway.Domain;

/// <summary>Итог одного запуска CLI в виде, пригодном для отправки в чат.</summary>
public sealed record ClaudeRunResult
{
    public required bool Ok { get; init; }

    /// <summary>Текст для пользователя: ответ агента либо описание ошибки.</summary>
    public required string Text { get; init; }

    public string? SessionId { get; init; }
    public decimal? CostUsd { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Cancelled { get; init; }

    /// <summary>
    /// Запуск оборвался на лимите тарифа. Очередь после такого дожидается сброса: каждая
    /// следующая задача упёрлась бы в тот же лимит, а «продолжить за кредиты» шлюзу запрещено.
    /// </summary>
    public bool RateLimited { get; init; }

    /// <summary>Расход запуска. null — CLI ничего не сказал (например, не смог стартовать).</summary>
    public RunUsage? Usage { get; init; }

    public static ClaudeRunResult Failure(string text, TimeSpan duration) =>
        new() { Ok = false, Text = text, Duration = duration };
}
