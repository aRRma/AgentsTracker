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
    /// <param name="mainModel">Модель основной ветки из потока; null — ходов в потоке не было.</param>
    public RunUsage ToRunUsage(string? mainModel = null)
    {
        var primary = Primary(mainModel);

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
    /// Модель, которая отвечала. Моделей в запуске бывает несколько: CLI зовёт haiku для
    /// служебных мелочей, сабагенты работают на своей. По числу токенов основную не угадать —
    /// сабагент на sonnet пишет больше самой сессии, — поэтому ищем модель основной ветки,
    /// а самую разговорчивую берём, только если поток модель не назвал.
    /// </summary>
    private KeyValuePair<string, CliModelUsage>? Primary(string? mainModel)
    {
        if (ModelUsage is not { Count: > 0 } models) return null;

        if (mainModel is { Length: > 0 })
        {
            var wanted = Normalize(mainModel);
            foreach (var pair in models)
                if (Normalize(pair.Key) == wanted || (pair.Value.CanonicalModel is { } canonical && Normalize(canonical) == wanted))
                    return pair;
        }

        return models.MaxBy(pair => pair.Value.OutputTokens);
    }

    /// <summary>
    /// Ключ modelUsage и model хода расходятся: при --model claude-opus-5-5[1m] ход называет
    /// claude-opus-5-5, а ключ — с «[1m]»; у haiku в ключе дата сборки (проверено на CLI 2.1.283).
    /// Без приведения основная модель не нашлась бы и подпись снова взяла бы сабагента.
    /// </summary>
    private static string Normalize(string model)
    {
        var bracket = model.IndexOf('[');
        return ShortModelName(bracket > 0 ? model[..bracket] : model).ToLowerInvariant();
    }

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
