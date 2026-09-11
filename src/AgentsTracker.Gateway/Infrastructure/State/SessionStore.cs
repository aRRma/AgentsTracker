using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentsTracker.Gateway.Infrastructure.State;

/// <summary>Состояние шлюза в %LOCALAPPDATA%\AgentsTracker\state.json. Пишется атомарно.</summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Сколько сессий помнить на проект и за сколько дней держать статистику.</summary>
    private const int SessionsPerProject = 25;
    private const int UsageDays = 60;

    /// <summary>Сколько последних запусков держать для монитора.</summary>
    private const int RecentRunsKept = 200;

    private readonly string _path;
    private readonly GatewayOptions _options;
    private readonly ILogger<SessionStore> _logger;
    private readonly Lock _gate = new();
    private GatewayState _state;

    public SessionStore(IOptions<GatewayOptions> options, ILogger<SessionStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        _path = Path.Combine(AppPaths.DataDirectory, "state.json");
        _state = Load();
        Migrate();
    }

    /// <summary>Папка, в которой запускается агент: выбранная из чата либо из конфига.</summary>
    public string ProjectPath
    {
        get { lock (_gate) return ProjectPathLocked(); }
    }

    public string? Model
    {
        get { lock (_gate) return _state.Model; }
    }

    /// <summary>Модель, которой пойдёт следующий запуск: из чата, иначе из конфига. null — решает CLI.</summary>
    public string? EffectiveModel
    {
        get { lock (_gate) return _state.Model ?? _options.Model; }
    }

    public string? PermissionMode
    {
        get { lock (_gate) return _state.PermissionMode; }
    }

    /// <summary>Режим работы следующего запуска: выбранный из чата, иначе из конфига.</summary>
    public string EffectivePermissionMode
    {
        get { lock (_gate) return _state.PermissionMode ?? _options.PermissionMode; }
    }

    /// <summary>Уровень усилий, выбранный из чата. null — не выбирали, действует значение конфига.</summary>
    public string? Effort
    {
        get { lock (_gate) return _state.Effort; }
    }

    /// <summary>Уровень усилий следующего запуска: из чата, иначе из конфига. null — решает CLI.</summary>
    public string? EffectiveEffort
    {
        get { lock (_gate) return _state.Effort ?? _options.Effort; }
    }

    /// <summary>Активная сессия текущего проекта. null — следующий запуск начнёт новую.</summary>
    public string? SessionId
    {
        get { lock (_gate) return _state.ActiveSessions.GetValueOrDefault(ProjectPathLocked()); }
    }

    public void SetProjectPath(string? path)
    {
        // Совпадение с конфигом храним как null: тогда правка ProjectPath в конфиге
        // не окажется молча перекрыта выбором, сделанным когда-то из чата.
        var value = path is { Length: > 0 } && !ProjectCatalog.Same(path, _options.ProjectPath)
            ? ProjectCatalog.Normalize(path)
            : null;

        Mutate(s => s.ProjectPath = value);
    }

    public void SetModel(string? model) => Mutate(s => s.Model = model);

    public void SetPermissionMode(string? mode) => Mutate(s => s.PermissionMode = mode);

    public void SetEffort(string? effort) => Mutate(s => s.Effort = effort);

    /// <summary>Задаёт активную сессию текущего проекта; null начинает новую.</summary>
    public void SetSessionId(string? sessionId)
    {
        Mutate(s =>
        {
            var project = ProjectPathLocked();

            if (sessionId is { Length: > 0 }) s.ActiveSessions[project] = sessionId;
            else s.ActiveSessions.Remove(project);

            s.LastActivityUtc = DateTimeOffset.UtcNow;
        });
    }

    /// <summary>
    /// Меняет активную сессию проекта, только если она всё ещё равна
    /// <paramref name="onlyIfActive"/>: так итог запуска не перетирает /new или смену
    /// сессии, сделанные по ходу. Возвращает, была ли запись.
    /// </summary>
    public bool TrySetSessionId(string projectPath, string? sessionId, string? onlyIfActive)
    {
        var project = ProjectCatalog.Normalize(projectPath);
        var applied = false;

        Mutate(s =>
        {
            var current = s.ActiveSessions.GetValueOrDefault(project);
            if (!string.Equals(current, onlyIfActive, StringComparison.Ordinal)) return;

            if (sessionId is { Length: > 0 }) s.ActiveSessions[project] = sessionId;
            else s.ActiveSessions.Remove(project);

            s.LastActivityUtc = DateTimeOffset.UtcNow;
            applied = true;
        });

        return applied;
    }

    /// <summary>Сессии проекта, свежие сверху.</summary>
    public IReadOnlyList<SessionRecord> SessionsFor(string projectPath)
    {
        var project = ProjectCatalog.Normalize(projectPath);

        lock (_gate)
        {
            return [.. _state.Sessions
                .Where(r => string.Equals(r.ProjectPath, project, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.LastActivityUtc)
                .Select(Clone)];
        }
    }

    /// <summary>Забывает все сессии проекта и начинает новую. Возвращает, сколько записей снято.</summary>
    public int ForgetSessions(string projectPath)
    {
        var project = ProjectCatalog.Normalize(projectPath);
        var removed = 0;

        Mutate(s =>
        {
            removed = s.Sessions.RemoveAll(r =>
                string.Equals(r.ProjectPath, project, StringComparison.OrdinalIgnoreCase));
            s.ActiveSessions.Remove(project);
        });

        return removed;
    }

    /// <summary>
    /// Расход запуска: статистика, а при известном id — ещё и сама сессия, чтобы к ней
    /// можно было вернуться из меню.
    /// </summary>
    public void RecordRun(string projectPath, string prompt, string? sessionId, RunUsage usage)
    {
        var project = ProjectCatalog.Normalize(projectPath);
        var now = DateTimeOffset.Now;

        Mutate(s =>
        {
            s.LastActivityUtc = now.ToUniversalTime();
            s.Usage.SinceUtc ??= now;
            s.Usage.Total.Add(usage);
            Bucket(s.Usage.ByDay, DayKey(now)).Add(usage);

            foreach (var model in usage.Models)
                Bucket(s.Usage.ByModel, model.Model).Add(model);

            TrimDays(s.Usage.ByDay);

            if (sessionId is not { Length: > 0 }) return;

            // Активной сессию здесь не делаем — это решает ChatWorker через TrySetSessionId.
            // Иначе запись расхода откатила бы /new или смену сессии по ходу запуска.
            var record = Touch(s, project, prompt, sessionId, now, activate: false);
            record.Turns += usage.Turns;
        });
    }

    /// <summary>
    /// Итог запуска для монитора. Отдельно от <see cref="RecordRun"/>: расход приходит
    /// от агента, а исход, превью промпта и число вызовов знает ChatWorker.
    /// </summary>
    public void RecordRunOutcome(RunRecord record)
    {
        Mutate(s =>
        {
            s.RecentRuns.Add(record);
            if (s.RecentRuns.Count > RecentRunsKept)
                s.RecentRuns.RemoveRange(0, s.RecentRuns.Count - RecentRunsKept);
        });
    }

    /// <summary>Пока запись на месте, шлюз обязан либо ответить, либо признать запуск прерванным.</summary>
    public void BeginRun(ActiveRun run) => Mutate(s => s.ActiveRun = run);

    public void EndRun() => Mutate(s => s.ActiveRun = null);

    /// <summary>
    /// Запуск, оставшийся от убитого экземпляра, если такой был; запись снимается.
    /// Зовётся один раз на старте.
    /// </summary>
    public ActiveRun? TakeInterruptedRun()
    {
        ActiveRun? run = null;
        Mutate(s =>
        {
            run = s.ActiveRun;
            s.ActiveRun = null;
        });
        return run;
    }

    /// <summary>Последние запуски, свежие сверху: копия, чтобы читать без замка.</summary>
    public IReadOnlyList<RunRecord> RecentRuns()
    {
        lock (_gate) return [.. Enumerable.Reverse(_state.RecentRuns).Select(Clone)];
    }

    /// <summary>
    /// Заводит сессию заранее, до ответа CLI: id шлюз выдаёт сам (<c>--session-id</c>),
    /// поэтому прерванный или упавший запуск не теряет ветку — следующее сообщение
    /// продолжит её через <c>--resume</c>.
    /// </summary>
    public void RegisterSession(string projectPath, string prompt, string sessionId)
    {
        var project = ProjectCatalog.Normalize(projectPath);
        var now = DateTimeOffset.Now;

        Mutate(s =>
        {
            s.LastActivityUtc = now.ToUniversalTime();
            Touch(s, project, prompt, sessionId, now, activate: true);
        });
    }

    /// <summary>
    /// Обновляет запись сессии, создавая при необходимости; при <paramref name="activate"/>
    /// делает её активной в проекте. Зовётся под замком из <see cref="Mutate"/>.
    /// </summary>
    private static SessionRecord Touch(
        GatewayState state, string project, string prompt, string sessionId, DateTimeOffset now, bool activate)
    {
        var record = state.Sessions.FirstOrDefault(r => string.Equals(r.Id, sessionId, StringComparison.Ordinal));
        if (record is null)
        {
            record = new SessionRecord
            {
                Id = sessionId,
                ProjectPath = project,
                Title = MakeTitle(prompt),
                CreatedUtc = now,
            };
            state.Sessions.Add(record);
        }

        record.LastActivityUtc = now;

        if (activate) state.ActiveSessions[project] = sessionId;
        TrimSessions(state, project);

        return record;
    }

    public void ResetUsage() => Mutate(s => s.Usage = new UsageStats { SinceUtc = DateTimeOffset.Now });

    // Правила «всегда» живут в проекте: проверяются и правятся только для текущего.

    public bool IsAlwaysAllowed(string signature)
    {
        lock (_gate)
            return _state.AlwaysAllowByProject.GetValueOrDefault(ProjectPathLocked())?.Contains(signature, StringComparer.Ordinal) == true;
    }

    /// <summary>Правила «всегда» текущего проекта: копия, чтобы читать без замка.</summary>
    public IReadOnlyList<string> AlwaysAllowRules()
    {
        lock (_gate)
            return [.. _state.AlwaysAllowByProject.GetValueOrDefault(ProjectPathLocked()) ?? []];
    }

    public void AddAlwaysAllow(string signature)
    {
        Mutate(s =>
        {
            var rules = RulesOf(s, ProjectPathLocked());
            if (!rules.Contains(signature, StringComparer.Ordinal)) rules.Add(signature);
        });
    }

    /// <summary>Снимает правило «всегда». Возвращает false, если такого правила не было.</summary>
    public bool RemoveAlwaysAllow(string signature)
    {
        var removed = false;
        Mutate(s =>
        {
            var project = ProjectPathLocked();
            removed = s.AlwaysAllowByProject.GetValueOrDefault(project)?.Remove(signature) == true;
            if (s.AlwaysAllowByProject.GetValueOrDefault(project) is { Count: 0 }) s.AlwaysAllowByProject.Remove(project);
        });
        return removed;
    }

    /// <summary>Снимает все правила «всегда» текущего проекта. Возвращает, сколько их было.</summary>
    public int ClearAlwaysAllow()
    {
        var removed = 0;
        Mutate(s =>
        {
            var project = ProjectPathLocked();
            removed = s.AlwaysAllowByProject.GetValueOrDefault(project)?.Count ?? 0;
            s.AlwaysAllowByProject.Remove(project);
        });
        return removed;
    }

    private static List<string> RulesOf(GatewayState state, string project)
    {
        if (!state.AlwaysAllowByProject.TryGetValue(project, out var rules))
            state.AlwaysAllowByProject[project] = rules = [];
        return rules;
    }

    /// <summary>Засчитывает запуск слэш-команды: «/plugin:skill» или «/skill», без аргументов.</summary>
    public void RecordSkillUse(string command)
    {
        var key = command.Trim().ToLowerInvariant();
        if (key.Length < 2 || key[0] != '/') return;

        Mutate(s => s.SkillUsage[key] = s.SkillUsage.GetValueOrDefault(key) + 1);
    }

    /// <summary>Счётчики запусков команд: копия, чтобы читать без замка.</summary>
    public IReadOnlyDictionary<string, int> SkillUsage()
    {
        lock (_gate) return new Dictionary<string, int>(_state.SkillUsage, StringComparer.OrdinalIgnoreCase);
    }

    public GatewayState Snapshot()
    {
        lock (_gate) return Clone(_state);
    }

    /// <summary>Первая строка первого сообщения — по ней сессия узнаётся в списке.</summary>
    private static string MakeTitle(string prompt)
    {
        var line = prompt.ReplaceLineEndings(" ").Trim();
        if (line.Length == 0) return "(без текста)";
        return line.Length <= 60 ? line : line[..59] + "…";
    }

    private static string DayKey(DateTimeOffset moment) => moment.ToString("yyyy-MM-dd");

    private static UsageTotals Bucket(Dictionary<string, UsageTotals> buckets, string key)
    {
        if (!buckets.TryGetValue(key, out var totals)) buckets[key] = totals = new UsageTotals();
        return totals;
    }

    private static void TrimDays(Dictionary<string, UsageTotals> byDay)
    {
        if (byDay.Count <= UsageDays) return;

        foreach (var key in byDay.Keys.OrderDescending().Skip(UsageDays).ToArray())
            byDay.Remove(key);
    }

    private static void TrimSessions(GatewayState state, string project)
    {
        var stale = state.Sessions
            .Where(r => string.Equals(r.ProjectPath, project, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.LastActivityUtc)
            .Skip(SessionsPerProject)
            // Активную не выбрасываем даже старую: иначе следующий запуск ушёл бы
            // с --resume на сессию, которой в списке уже нет.
            .Where(r => !string.Equals(r.Id, state.ActiveSessions.GetValueOrDefault(project), StringComparison.Ordinal))
            .ToArray();

        foreach (var record in stale) state.Sessions.Remove(record);
    }

    private string ProjectPathLocked() =>
        ProjectCatalog.Normalize(_state.ProjectPath is { Length: > 0 } p ? p : _options.ProjectPath);

    private void Mutate(Action<GatewayState> change)
    {
        lock (_gate)
        {
            change(_state);
            Save(_state);
        }
    }

    /// <summary>
    /// Переносит состояние старого формата: единственную активную сессию — в список,
    /// общие правила «всегда» — в текущий проект.
    /// </summary>
    private void Migrate()
    {
        lock (_gate)
        {
            if (_state.AlwaysAllow.Count > 0)
            {
                var rules = RulesOf(_state, ProjectPathLocked());
                foreach (var rule in _state.AlwaysAllow.Where(r => !rules.Contains(r, StringComparer.Ordinal)))
                    rules.Add(rule);

                _logger.LogInformation("Правил «всегда» перенесено в проект {Project}: {Count}", ProjectPathLocked(), _state.AlwaysAllow.Count);
                _state.AlwaysAllow.Clear();
                Save(_state);
            }

            if (_state.SessionId is not { Length: > 0 } legacy) return;

            var project = ProjectPathLocked();

            _state.ActiveSessions.TryAdd(project, legacy);

            if (!_state.Sessions.Any(r => string.Equals(r.Id, legacy, StringComparison.Ordinal)))
            {
                _state.Sessions.Add(new SessionRecord
                {
                    Id = legacy,
                    ProjectPath = project,
                    Title = "(сессия до обновления)",
                    CreatedUtc = _state.LastActivityUtc ?? DateTimeOffset.Now,
                    LastActivityUtc = _state.LastActivityUtc ?? DateTimeOffset.Now,
                });
            }

            _state.SessionId = null;
            Save(_state);
            _logger.LogInformation("Сессия {SessionId} перенесена в список сессий проекта {Project}", legacy, project);
        }
    }

    private GatewayState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<GatewayState>(File.ReadAllText(_path), JsonOptions);
                if (loaded is not null) return Rehydrate(loaded);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать {Path}, начинаем с чистого состояния", _path);
        }

        return new GatewayState();
    }

    private static GatewayState Clone(GatewayState state) =>
        Rehydrate(JsonSerializer.Deserialize<GatewayState>(JsonSerializer.Serialize(state, JsonOptions), JsonOptions)!);

    private static SessionRecord Clone(SessionRecord record) => new()
    {
        Id = record.Id,
        ProjectPath = record.ProjectPath,
        Title = record.Title,
        CreatedUtc = record.CreatedUtc,
        LastActivityUtc = record.LastActivityUtc,
        Turns = record.Turns,
    };

    private static RunRecord Clone(RunRecord record) => new()
    {
        StartedUtc = record.StartedUtc,
        ProjectPath = record.ProjectPath,
        SessionId = record.SessionId,
        Prompt = record.Prompt,
        Model = record.Model,
        Outcome = record.Outcome,
        DurationMs = record.DurationMs,
        Turns = record.Turns,
        ToolCalls = record.ToolCalls,
        InputTokens = record.InputTokens,
        OutputTokens = record.OutputTokens,
    };

    /// <summary>
    /// Десериализация теряет компаратор словарей, и «C:\Proj» перестаёт находить сессию,
    /// записанную как «c:\proj». Пересобираем.
    /// </summary>
    private static GatewayState Rehydrate(GatewayState state)
    {
        state.ActiveSessions = new Dictionary<string, string>(state.ActiveSessions, StringComparer.OrdinalIgnoreCase);
        state.AlwaysAllowByProject = new Dictionary<string, List<string>>(state.AlwaysAllowByProject, StringComparer.OrdinalIgnoreCase);

        // Старые версии писали голое имя инструмента («WebSearch»). Теперь в сигнатуру входят
        // аргументы, так что такие записи не совпадут никогда — убираем из /rules.
        state.AlwaysAllow.RemoveAll(IsBareToolName);
        foreach (var rules in state.AlwaysAllowByProject.Values) rules.RemoveAll(IsBareToolName);

        return state;
    }

    private static bool IsBareToolName(string signature) => !signature.Contains('(') && !signature.Contains('{');

    private void Save(GatewayState state)
    {
        var temp = _path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить состояние в {Path}", _path);
        }
    }
}
