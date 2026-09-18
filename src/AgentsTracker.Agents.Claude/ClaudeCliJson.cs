using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentsTracker.Agents.Claude;

/// <summary>Payload от <c>claude -p --output-format json</c>.</summary>
public sealed class ClaudeCliJson
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("subtype")] public string? Subtype { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("session_id")] public string? SessionId { get; set; }
    [JsonPropertyName("is_error")] public bool IsError { get; set; }
    [JsonPropertyName("num_turns")] public int? NumTurns { get; set; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; set; }
    [JsonPropertyName("permission_denials")] public JsonElement? PermissionDenials { get; set; }
    [JsonPropertyName("usage")] public CliUsage? Usage { get; set; }

    /// <summary>Разбивка по моделям: ключ — полное имя модели, например claude-haiku-4-5-20251001.</summary>
    [JsonPropertyName("modelUsage")] public Dictionary<string, CliModelUsage>? ModelUsage { get; set; }

    /// <summary>Расход запуска в виде, который кладётся в статистику шлюза.</summary>
    public RunUsage ToRunUsage()
    {
        var primary = Primary();

        return new()
        {
            Turns = NumTurns ?? 0,
            DurationMs = DurationMs ?? 0,
            InputTokens = Usage?.InputTokens ?? 0,
            OutputTokens = Usage?.OutputTokens ?? 0,
            CacheReadTokens = Usage?.CacheReadInputTokens ?? 0,
            CacheWriteTokens = Usage?.CacheCreationInputTokens ?? 0,
            Models = ModelUsage is null
                ? []
                : [.. ModelUsage.Select(pair => new ModelRunUsage(
                    pair.Key,
                    pair.Value.InputTokens,
                    pair.Value.OutputTokens,
                    pair.Value.CacheReadInputTokens,
                    pair.Value.CacheCreationInputTokens))],
            PrimaryModel = primary is { } main ? ShortModelName(main.Value.CanonicalModel ?? main.Key) : null,
            ContextWindow = primary?.Value.ContextWindow ?? 0,
        };
    }

    /// <summary>
    /// Модель, которая отвечала. Моделей в запуске бывает несколько: CLI зовёт haiku
    /// для служебных мелочей (заголовок, сжатие вывода), и её ответ короче основного.
    /// </summary>
    private KeyValuePair<string, CliModelUsage>? Primary() =>
        ModelUsage is { Count: > 0 } models ? models.MaxBy(pair => pair.Value.OutputTokens) : null;

    /// <summary>
    /// «claude-haiku-4-5-20251001» → «haiku-4-5»: префикс и дата в подписи под каждым
    /// ответом ничего не говорят, а строку удлиняют вдвое.
    /// </summary>
    internal static string ShortModelName(string model)
    {
        var name = model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? model["claude-".Length..] : model;

        var dash = name.LastIndexOf('-');
        if (dash > 0 && name.Length - dash - 1 == 8 && name[(dash + 1)..].All(char.IsAsciiDigit))
            name = name[..dash];

        return name;
    }
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

    /// <summary>Размер окна контекста модели (проверено на CLI 2.1.261).</summary>
    [JsonPropertyName("contextWindow")] public long ContextWindow { get; set; }

    /// <summary>Имя модели без даты сборки: «claude-haiku-4-5». Старые CLI его не присылают.</summary>
    [JsonPropertyName("canonicalModel")] public string? CanonicalModel { get; set; }
}
