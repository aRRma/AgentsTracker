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

    /// <summary>Режим работы агента, выставленный командой /mode. null = из конфига.</summary>
    public string? PermissionMode { get; set; }

    /// <summary>Уровень усилий, выставленный из чата. null = из конфига.</summary>
    public string? Effort { get; set; }

    /// <summary>
    /// Правила «всегда» до разделения по проектам. Читаются при загрузке и переезжают
    /// в <see cref="AlwaysAllowByProject"/> текущего проекта; новые версии сюда не пишут.
    /// </summary>
    public List<string> AlwaysAllow { get; set; } = [];

    /// <summary>
    /// Сигнатуры, разрешённые кнопкой «Всегда», когда CLI не прислал своих правил. Ключ —
    /// нормализованный путь проекта: «git push --force», разрешённый в одном проекте,
    /// не должен действовать во всех остальных.
    /// </summary>
    public Dictionary<string, List<string>> AlwaysAllowByProject { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Известные шлюзу сессии всех проектов.</summary>
    public List<SessionRecord> Sessions { get; set; } = [];

    /// <summary>
    /// Активная сессия каждого проекта: путь папки → id сессии. Ключ — нормализованный путь,
    /// потому что --resume работает только в той папке, где сессия создана.
    /// </summary>
    public Dictionary<string, string> ActiveSessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public UsageStats Usage { get; set; } = new();

    /// <summary>
    /// Сколько раз запускали каждую слэш-команду, кнопкой или текстом; ключ в нижнем
    /// регистре. По счётчику экран скиллов выносит частые наверх.
    /// </summary>
    public Dictionary<string, int> SkillUsage { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Последние запуски для веб-монитора, свежие в конце. Хранится ограниченное число.</summary>
    public List<RunRecord> RecentRuns { get; set; } = [];

    /// <summary>
    /// Запуск, идущий прямо сейчас: живёт от старта процесса до итога. Запись, оставшаяся
    /// к моменту старта шлюза, значит, что прошлый экземпляр умер посреди работы — об этом
    /// надо сказать в чат, иначе ответа не будет, а чат промолчит.
    /// </summary>
    public ActiveRun? ActiveRun { get; set; }
}

/// <summary>Идущий запуск: кому отвечать и что запускали, чтобы после перезапуска сказать «прервано».</summary>
public sealed class ActiveRun
{
    /// <summary>Адрес чата строкой «канал:значение» — разбирает его сам канал.</summary>
    public string ChatKey { get; set; } = "";

    public string UserKey { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public string ProjectPath { get; set; } = "";
    public string? SessionId { get; set; }
    public string? Model { get; set; }

    /// <summary>Превью промпта через Text.Preview — как в RunRecord.</summary>
    public string Prompt { get; set; } = "";
}

/// <summary>
/// Один завершённый запуск — строка в таблице монитора. Промпт только превью: полный
/// текст в state.json не кладём по той же причине, что и в аудит.
/// </summary>
public sealed class RunRecord
{
    public DateTimeOffset StartedUtc { get; set; }
    public string ProjectPath { get; set; } = "";
    public string? SessionId { get; set; }
    public string Prompt { get; set; } = "";
    public string? Model { get; set; }

    /// <summary>ok / cancel / rate-limit / error / interrupted, как в аудите. interrupted — шлюз умер посреди запуска.</summary>
    public string Outcome { get; set; } = "";

    public long DurationMs { get; set; }
    public int Turns { get; set; }
    public int ToolCalls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

/// <summary>Сессия агента, известная шлюзу: её можно выбрать в меню.</summary>
public sealed class SessionRecord
{
    public string Id { get; set; } = "";

    /// <summary>Нормализованный путь папки, в которой сессия создана.</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>Начало первого сообщения — по нему сессия узнаётся в списке.</summary>
    public string Title { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset LastActivityUtc { get; set; }
    public int Turns { get; set; }
}

/// <summary>Накопленная статистика запусков: всего, по дням и по моделям.</summary>
public sealed class UsageStats
{
    /// <summary>С какого момента считаем. Сдвигается при сбросе статистики.</summary>
    public DateTimeOffset? SinceUtc { get; set; }

    public UsageTotals Total { get; set; } = new();

    /// <summary>Ключ — локальная дата «yyyy-MM-dd»: сутки должны совпадать с календарём пользователя.</summary>
    public Dictionary<string, UsageTotals> ByDay { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, UsageTotals> ByModel { get; set; } = new(StringComparer.Ordinal);
}

public sealed class UsageTotals
{
    public int Runs { get; set; }
    public int Turns { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long DurationMs { get; set; }

    public void Add(RunUsage usage)
    {
        Runs++;
        Turns += usage.Turns;
        InputTokens += usage.InputTokens;
        OutputTokens += usage.OutputTokens;
        CacheReadTokens += usage.CacheReadTokens;
        CacheWriteTokens += usage.CacheWriteTokens;
        DurationMs += usage.DurationMs;
    }

    public void Add(ModelRunUsage usage)
    {
        Runs++;
        InputTokens += usage.InputTokens;
        OutputTokens += usage.OutputTokens;
        CacheReadTokens += usage.CacheReadTokens;
        CacheWriteTokens += usage.CacheWriteTokens;
    }
}
