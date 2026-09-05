namespace AgentsTracker.Gateway.Domain;

/// <summary>Всё, что шлюз помнит между перезапусками. Лежит в state.json.</summary>
public sealed class GatewayState
{
    /// <summary>
    /// Активная сессия до появления списка сессий. Читается при загрузке и переезжает
    /// в <see cref="ActiveSessions"/>; новые версии сюда не пишут.
    /// </summary>
    public string? SessionId { get; set; }

    public DateTimeOffset? LastActivityUtc { get; set; }

    /// <summary>Рабочая папка, выбранная из чата. null = Gateway:ProjectPath из конфига.</summary>
    public string? ProjectPath { get; set; }

    /// <summary>Алиас модели, выставленный из чата. null = из конфига.</summary>
    public string? Model { get; set; }

    /// <summary>Уровень доступа, выставленный командой /mode. null = из конфига.</summary>
    public string? PermissionMode { get; set; }

    /// <summary>Уровень усилий, выставленный из чата. null = из конфига.</summary>
    public string? Effort { get; set; }

    /// <summary>
    /// Сигнатуры действий, разрешённых кнопкой «Всегда» (когда CLI не прислал suggestions,
    /// которые можно было бы записать в .claude/settings.local.json).
    /// </summary>
    public List<string> AlwaysAllow { get; set; } = [];

    /// <summary>Известные шлюзу сессии всех проектов.</summary>
    public List<SessionRecord> Sessions { get; set; } = [];

    /// <summary>
    /// Активная сессия каждого проекта: путь папки → id сессии. Ключ — нормализованный путь,
    /// потому что --resume работает только в той папке, где сессия создана.
    /// </summary>
    public Dictionary<string, string> ActiveSessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public UsageStats Usage { get; set; } = new();

    /// <summary>
    /// Сколько раз запускали каждую слэш-команду Claude Code (кнопкой или текстом). Ключ —
    /// команда в нижнем регистре. По этому счётчику экран скиллов выносит частые наверх.
    /// </summary>
    public Dictionary<string, int> SkillUsage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Сессия Claude Code, о которой знает шлюз, — чтобы её можно было выбрать в меню.</summary>
public sealed class SessionRecord
{
    public string Id { get; set; } = "";

    /// <summary>Нормализованный путь папки, в которой сессия создана.</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>Начало первого сообщения — по нему сессию узнаёшь в списке.</summary>
    public string Title { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset LastActivityUtc { get; set; }
    public int Turns { get; set; }
    public decimal CostUsd { get; set; }
}

/// <summary>Накопленная статистика запусков: всего, по дням и по моделям.</summary>
public sealed class UsageStats
{
    /// <summary>С какого момента считаем. Сдвигается при сбросе статистики.</summary>
    public DateTimeOffset? SinceUtc { get; set; }

    public UsageTotals Total { get; set; } = new();

    /// <summary>Ключ — локальная дата «yyyy-MM-dd»: дневной лимит должен совпадать с календарём пользователя.</summary>
    public Dictionary<string, UsageTotals> ByDay { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, UsageTotals> ByModel { get; set; } = new(StringComparer.Ordinal);
}

public sealed class UsageTotals
{
    public int Runs { get; set; }
    public int Turns { get; set; }
    public decimal CostUsd { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long DurationMs { get; set; }

    public void Add(RunUsage usage)
    {
        Runs++;
        Turns += usage.Turns;
        CostUsd += usage.CostUsd;
        InputTokens += usage.InputTokens;
        OutputTokens += usage.OutputTokens;
        CacheReadTokens += usage.CacheReadTokens;
        CacheWriteTokens += usage.CacheWriteTokens;
        DurationMs += usage.DurationMs;
    }

    public void Add(ModelRunUsage usage)
    {
        Runs++;
        CostUsd += usage.CostUsd;
        InputTokens += usage.InputTokens;
        OutputTokens += usage.OutputTokens;
        CacheReadTokens += usage.CacheReadTokens;
        CacheWriteTokens += usage.CacheWriteTokens;
    }
}

/// <summary>Расход одного запуска CLI — то, что шлюз кладёт в статистику.</summary>
public sealed record RunUsage
{
    public int Turns { get; init; }
    public decimal CostUsd { get; init; }
    public long DurationMs { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }

    /// <summary>Разбивка по моделям из modelUsage: за один запуск их может быть несколько.</summary>
    public IReadOnlyList<ModelRunUsage> Models { get; init; } = [];
}

public sealed record ModelRunUsage(
    string Model,
    decimal CostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens);
