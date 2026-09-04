using System.Text.Json;
using System.Text.Json.Serialization;
using AgentsTracker.Gateway.State;

namespace AgentsTracker.Gateway.Claude;

/// <summary>Payload от <c>claude -p --output-format json</c>.</summary>
public sealed class ClaudeCliJson
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("subtype")] public string? Subtype { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("session_id")] public string? SessionId { get; set; }
    [JsonPropertyName("is_error")] public bool IsError { get; set; }
    [JsonPropertyName("total_cost_usd")] public decimal? TotalCostUsd { get; set; }
    [JsonPropertyName("num_turns")] public int? NumTurns { get; set; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; set; }
    [JsonPropertyName("permission_denials")] public JsonElement? PermissionDenials { get; set; }
    [JsonPropertyName("usage")] public CliUsage? Usage { get; set; }

    /// <summary>Разбивка по моделям: ключ — полное имя модели, например claude-haiku-4-5-20251001.</summary>
    [JsonPropertyName("modelUsage")] public Dictionary<string, CliModelUsage>? ModelUsage { get; set; }

    /// <summary>Расход запуска в виде, который кладётся в статистику шлюза.</summary>
    public RunUsage ToRunUsage() => new()
    {
        Turns = NumTurns ?? 0,
        CostUsd = TotalCostUsd ?? 0m,
        DurationMs = DurationMs ?? 0,
        InputTokens = Usage?.InputTokens ?? 0,
        OutputTokens = Usage?.OutputTokens ?? 0,
        CacheReadTokens = Usage?.CacheReadInputTokens ?? 0,
        CacheWriteTokens = Usage?.CacheCreationInputTokens ?? 0,
        Models = ModelUsage is null
            ? []
            : [.. ModelUsage.Select(pair => new ModelRunUsage(
                pair.Key,
                pair.Value.CostUsd,
                pair.Value.InputTokens,
                pair.Value.OutputTokens,
                pair.Value.CacheReadInputTokens,
                pair.Value.CacheCreationInputTokens))],
    };
}

/// <summary>Токены запуска. Имена полей — snake_case, как в ответе CLI.</summary>
public sealed class CliUsage
{
    [JsonPropertyName("input_tokens")] public long InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public long OutputTokens { get; set; }
    [JsonPropertyName("cache_read_input_tokens")] public long CacheReadInputTokens { get; set; }
    [JsonPropertyName("cache_creation_input_tokens")] public long CacheCreationInputTokens { get; set; }
}

/// <summary>Расход по одной модели. В modelUsage CLI использует camelCase — в отличие от usage.</summary>
public sealed class CliModelUsage
{
    [JsonPropertyName("inputTokens")] public long InputTokens { get; set; }
    [JsonPropertyName("outputTokens")] public long OutputTokens { get; set; }
    [JsonPropertyName("cacheReadInputTokens")] public long CacheReadInputTokens { get; set; }
    [JsonPropertyName("cacheCreationInputTokens")] public long CacheCreationInputTokens { get; set; }
    [JsonPropertyName("costUSD")] public decimal CostUsd { get; set; }
}

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
