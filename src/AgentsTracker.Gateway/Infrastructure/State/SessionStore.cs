using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentsTracker.Gateway.Infrastructure.Git;

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

    /// <summary>Выбор чата: темы — свой, ленты — общий с прошлыми версиями.</summary>
    public ChatScope For(ChatId chat) => new(this, chat);

    /// <summary>
    /// Выбор ленты — для того, у кого чата нет: старт шлюза, монитор. Чат известен — только
    /// <see cref="For"/>, иначе тема покажет и изменит проект ленты.
    /// </summary>
    public ChatScope Feed => new(this, null);

    /// <summary>Корень папок бесед: удалять перед запуском можно только внутри него.</summary>
    public static string TalkRoot => ProjectCatalog.Normalize(Path.Combine(Path.GetTempPath(), "AgentsTracker-talk"));

    /// <summary>Папка беседы внутри <see cref="TalkRoot"/>: своя у каждой темы — по ней CLI находит её сессии.</summary>
    public static bool IsTalkFolder(string path) => ProjectCatalog.IsInside(ProjectCatalog.Normalize(path), TalkRoot);

    private static string TalkFolder(ChatId chat) =>
        Path.Combine(TalkRoot, string.Concat(chat.Key.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')));

    /// <summary>
    /// Выбор одного чата: проект, активная сессия, модель, effort, режим. Тема без записи —
    /// беседа: команда в новой теме не должна тронуть чужой проект.
    /// </summary>
    public sealed class ChatScope
    {
        private readonly SessionStore _store;
        private readonly ChatId? _chat;

        internal ChatScope(SessionStore store, ChatId? chat) => (_store, _chat) = (store, chat);

        public bool IsTopic => _chat?.IsTopic == true;

        /// <summary>Тема, где ещё ничего не выбирали: шлюз сначала спросит её тип.</summary>
        public bool IsNewTopic
        {
            get { lock (_store._gate) return IsTopic && !_store._state.Topics.ContainsKey(_chat!.Key); }
        }

        /// <summary>Беседа: тема без проекта. Идёт в своей пустой папке, только с поиском в сети.</summary>
        public bool IsTalk
        {
            get { lock (_store._gate) return IsTalkLocked(); }
        }

        /// <summary>Папка, в которой запускается агент: выбранная из чата либо из конфига.</summary>
        public string ProjectPath
        {
            get { lock (_store._gate) return _store.ProjectPathLocked(_chat); }
        }

        public string? Model => Read(s => s.Model);

        /// <summary>Модель, которой пойдёт следующий запуск: из чата, иначе из конфига. null — решает CLI.</summary>
        public string? EffectiveModel
        {
            get { lock (_store._gate) return Selection()?.Model ?? (IsTalkLocked() ? _store._options.Question.Model : _store._options.Model); }
        }

        public string? PermissionMode => Read(s => s.PermissionMode);

        /// <summary>Режим работы следующего запуска: выбранный из чата, иначе из конфига.</summary>
        public string EffectivePermissionMode => PermissionMode ?? _store._options.PermissionMode;

        /// <summary>Уровень усилий, выбранный из чата. null — не выбирали, действует значение конфига.</summary>
        public string? Effort => Read(s => s.Effort);

        /// <summary>Уровень усилий следующего запуска: из чата, иначе из конфига. null — решает CLI.</summary>
        public string? EffectiveEffort
        {
            get { lock (_store._gate) return Selection()?.Effort ?? (IsTalkLocked() ? _store._options.Question.Effort : _store._options.Effort); }
        }

        /// <summary>Активная сессия текущего проекта. null — следующий запуск начнёт новую.</summary>
        public string? SessionId
        {
            get { lock (_store._gate) return _store._state.ActiveSessions.GetValueOrDefault(Slot(_store.ProjectPathLocked(_chat))); }
        }

        /// <summary>У темы null — беседа.</summary>
        public void SetProjectPath(string? path)
        {
            // Совпадение с конфигом храним у ленты как null: тогда правка ProjectPath в конфиге
            // не окажется молча перекрыта выбором, сделанным когда-то из чата.
            var value = path is { Length: > 0 } && (IsTopic || !ProjectCatalog.Same(path, _store._options.ProjectPath))
                ? ProjectCatalog.Normalize(path)
                : null;

            Write(s => s.ProjectPath = value);
        }

        public void SetModel(string? model) => Write(s => s.Model = model);

        public void SetPermissionMode(string? mode) => Write(s => s.PermissionMode = mode);

        public void SetEffort(string? effort) => Write(s => s.Effort = effort);

        /// <summary>Задаёт активную сессию текущего проекта; null начинает новую.</summary>
        public void SetSessionId(string? sessionId) => _store.Mutate(s =>
        {
            var slot = Slot(_store.ProjectPathLocked(_chat));

            if (sessionId is { Length: > 0 }) s.ActiveSessions[slot] = sessionId;
            else s.ActiveSessions.Remove(slot);

            s.LastActivityUtc = DateTimeOffset.UtcNow;
        });

        /// <summary>
        /// Меняет активную сессию проекта, только если она всё ещё равна
        /// <paramref name="onlyIfActive"/>: так итог запуска не перетирает /new или смену
        /// сессии, сделанные по ходу. Возвращает, была ли запись.
        /// </summary>
        public bool TrySetSessionId(string projectPath, string? sessionId, string? onlyIfActive)
        {
            var slot = Slot(ProjectCatalog.Normalize(projectPath));
            var applied = false;

            _store.Mutate(s =>
            {
                var current = s.ActiveSessions.GetValueOrDefault(slot);
                if (!string.Equals(current, onlyIfActive, StringComparison.Ordinal)) return;

                if (sessionId is { Length: > 0 }) s.ActiveSessions[slot] = sessionId;
                else s.ActiveSessions.Remove(slot);

                s.LastActivityUtc = DateTimeOffset.UtcNow;
                applied = true;
            });

            return applied;
        }

        /// <summary>
        /// Забывает сессии проекта и начинает новую. Активные в других чатах остаются: в соседней
        /// теме на том же проекте может идти запуск, и его сессия пропала бы на середине.
        /// Возвращает, сколько записей снято.
        /// </summary>
        public int ForgetSessions()
        {
            var removed = 0;

            _store.Mutate(s =>
            {
                var project = _store.ProjectPathLocked(_chat);
                s.ActiveSessions.Remove(Slot(project));
                removed = s.Sessions.RemoveAll(r =>
                    string.Equals(r.ProjectPath, project, StringComparison.OrdinalIgnoreCase)
                    && !s.ActiveSessions.ContainsValue(r.Id));
            });

            return removed;
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

            _store.Mutate(s =>
            {
                s.LastActivityUtc = now.ToUniversalTime();
                Touch(s, project, prompt, sessionId, now, activateIn: Slot(project));
            });
        }

        /// <summary>Правила «всегда» текущего проекта: копия, чтобы читать без замка.</summary>
        public IReadOnlyList<string> AlwaysAllowRules()
        {
            lock (_store._gate)
                return [.. _store._state.AlwaysAllowByProject.GetValueOrDefault(_store.ProjectPathLocked(_chat)) ?? []];
        }

        /// <summary>Снимает правило «всегда». Возвращает false, если такого правила не было.</summary>
        public bool RemoveAlwaysAllow(string signature)
        {
            var removed = false;
            _store.Mutate(s =>
            {
                var project = _store.ProjectPathLocked(_chat);
                removed = s.AlwaysAllowByProject.GetValueOrDefault(project)?.Remove(signature) == true;
                if (s.AlwaysAllowByProject.GetValueOrDefault(project) is { Count: 0 }) s.AlwaysAllowByProject.Remove(project);
            });
            return removed;
        }

        /// <summary>Снимает все правила «всегда» текущего проекта. Возвращает, сколько их было.</summary>
        public int ClearAlwaysAllow()
        {
            var removed = 0;
            _store.Mutate(s =>
            {
                var project = _store.ProjectPathLocked(_chat);
                removed = s.AlwaysAllowByProject.GetValueOrDefault(project)?.Count ?? 0;
                s.AlwaysAllowByProject.Remove(project);
            });
            return removed;
        }

        /// <summary>Ключ активной сессии в <see cref="GatewayState.ActiveSessions"/>.</summary>
        private string Slot(string project) => IsTopic ? $"{_chat!.Key}|{project}" : project;

        /// <summary>Выбор под замком; null — тема без записи.</summary>
        private IChatSelection? Selection() =>
            IsTopic ? _store._state.Topics.GetValueOrDefault(_chat!.Key) : _store._state;

        private bool IsTalkLocked() => IsTopic && Selection()?.ProjectPath is not { Length: > 0 };

        private string? Read(Func<IChatSelection, string?> field)
        {
            lock (_store._gate) return Selection() is { } selection ? field(selection) : null;
        }

        /// <summary>Первая запись в теме заводит её выбор — с этого момента тип темы известен.</summary>
        private void Write(Action<IChatSelection> change) => _store.Mutate(s =>
        {
            if (!IsTopic)
            {
                change(s);
                return;
            }

            if (!s.Topics.TryGetValue(_chat!.Key, out var topic)) s.Topics[_chat.Key] = topic = new TopicState();
            change(topic);
        });
    }

    /// <summary>
    /// Модель для показа человеку: версия, которой алиас ответил в последний раз, иначе
    /// подпись агента. null — модель не выбрана, решает агент.
    /// </summary>
    public string? ModelLabel(string? model, AgentSetting setting)
    {
        if (model is not { Length: > 0 }) return null;

        lock (_gate)
        {
            if (_state.ModelVersions.TryGetValue(model, out var seen)) return seen;
        }

        return setting.Describe(model);
    }

    /// <summary>
    /// Запоминает, во что раскрылся алиас. Только версия того же семейства: «opus» → «opus-5-5»;
    /// ответ другой моделью (агент подменил алиас) версией алиаса не считается.
    /// </summary>
    public void RecordModelVersion(string? requested, string? answered)
    {
        if (requested is not { Length: > 0 } || answered is not { Length: > 0 }
            || !answered.StartsWith(requested + "-", StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
        {
            // Без изменения файл не переписываем: запись идёт после каждого запуска.
            if (_state.ModelVersions.TryGetValue(requested, out var known) && known == answered) return;
        }

        Mutate(s => s.ModelVersions[requested] = answered);
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

    /// <summary>Запись сессии по id, копией; null — id пуст или шлюз такой сессии не знает.</summary>
    public SessionRecord? Session(string? sessionId)
    {
        if (sessionId is not { Length: > 0 }) return null;

        lock (_gate)
        {
            var record = _state.Sessions.FirstOrDefault(r => string.Equals(r.Id, sessionId, StringComparison.Ordinal));
            return record is null ? null : Clone(record);
        }
    }

    /// <summary>
    /// Расход запуска: статистика, а при известном id — ещё и сама сессия, чтобы к ней
    /// можно было вернуться из меню.
    /// </summary>
    /// <param name="cacheMiss">Запуск записал сессию в кэш заново (<see cref="CacheGuard.Miss"/>).</param>
    public void RecordRun(string projectPath, string prompt, string? sessionId, RunUsage usage, bool cacheMiss = false)
    {
        var project = ProjectCatalog.Normalize(projectPath);
        var now = DateTimeOffset.Now;

        Mutate(s =>
        {
            s.LastActivityUtc = now.ToUniversalTime();
            s.Usage.SinceUtc ??= now;
            s.Usage.Total.Add(usage);
            var day = Bucket(s.Usage.ByDay, DayKey(now));
            day.Add(usage);

            if (cacheMiss)
            {
                s.Usage.Total.CacheMisses++;
                day.CacheMisses++;
            }

            foreach (var model in usage.Models)
                Bucket(s.Usage.ByModel, model.Model).Add(model);

            TrimDays(s.Usage.ByDay);

            if (sessionId is not { Length: > 0 }) return;

            // Активной сессию здесь не делаем — это решает ChatWorker через TrySetSessionId.
            // Иначе запись расхода откатила бы /new или смену сессии по ходу запуска.
            var record = Touch(s, project, prompt, sessionId, now, activateIn: null);
            record.Turns += usage.Turns;
        });
    }

    /// <summary>
    /// Заполненность контекста сессии после запуска. Сессию не создаёт: запись заводит
    /// <see cref="RegisterSession"/>, а без неё показывать цифру негде.
    /// </summary>
    public void RecordContext(string sessionId, long tokens, long window)
    {
        Mutate(s =>
        {
            var record = s.Sessions.FirstOrDefault(r => string.Equals(r.Id, sessionId, StringComparison.Ordinal));
            if (record is null) return;

            record.ContextTokens = tokens;
            // Окно не сообщает только запуск без модели — прошлое значение вернее нуля.
            if (window > 0) record.ContextWindow = window;
            record.ContextUtc = DateTimeOffset.UtcNow;
        });
    }

    /// <summary>
    /// Папка сессии после запуска. Папку проекта не храним: запись нужна только сессии,
    /// которую агент увёл в worktree, иначе каждая сессия тянула бы копию своего пути.
    /// </summary>
    public void RecordWorkingDirectory(string sessionId, string path)
    {
        var folder = ProjectCatalog.Normalize(path);

        Mutate(s =>
        {
            var record = s.Sessions.FirstOrDefault(r => string.Equals(r.Id, sessionId, StringComparison.Ordinal));
            if (record is null) return;

            record.WorkingDirectory = ProjectCatalog.Same(folder, record.ProjectPath) ? null : folder;
        });
    }

    /// <summary>
    /// Папка, где пойдёт запуск сессии: туда её вернёт сам агент. Удалённый worktree — уже не
    /// папка сессии: агенту некуда в него вернуться, и ветка была бы веткой несуществующей папки.
    /// Путь пришёл из вывода агента, поэтому сетевую папку не трогаем даже проверкой существования.
    /// </summary>
    public string SessionFolder(string projectPath, string? sessionId) =>
        Session(sessionId)?.WorkingDirectory is { } folder && GitRepository.IsLocal(folder) && Directory.Exists(folder)
            ? folder
            : projectPath;

    /// <summary>
    /// Срок кэша сессии после запуска. Возвращает «кэш до» — его показывает подпись ответа, и
    /// считать его второй раз в другом месте значило бы рискнуть разойтись с «Статусом».
    /// Запуск без записи в кэш срок не сообщает — берём прошлый: он не меняется от запуска к запуску.
    /// </summary>
    /// <param name="model">Модель, которая отвечала («opus-5-5»): на ней и записан кэш.</param>
    /// <param name="mode">Режим со своим кэшем, в котором шёл запуск (<see cref="CacheGuard.ModeScope"/>).</param>
    public DateTimeOffset? RecordCache(
        string sessionId, DateTimeOffset? lastRequestUtc, TimeSpan? ttl, string? model, string? mode)
    {
        DateTimeOffset? until = null;

        Mutate(s =>
        {
            var record = s.Sessions.FirstOrDefault(r => string.Equals(r.Id, sessionId, StringComparison.Ordinal));
            if (record is null) return;

            if (ttl is not null) s.CacheTtl = ttl;
            record.CacheUntilUtc = lastRequestUtc is { } start && s.CacheTtl is { } known ? start + known : null;
            record.CacheModel = model;
            record.CacheMode = mode;
            until = record.CacheUntilUtc;
        });

        return until;
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

    /// <summary>
    /// Пока запись на месте, шлюз обязан либо ответить, либо признать запуск прерванным.
    /// Вопрос пишется в своё место: он идёт параллельно задаче.
    /// </summary>
    public void BeginRun(ActiveRun run) => Mutate(s =>
    {
        if (run.Question) s.ActiveQuestion = run;
        else s.ActiveRuns[run.ChatKey] = run;
    });

    public void EndRun(ActiveRun run) => Mutate(s =>
    {
        if (run.Question) s.ActiveQuestion = null;
        else s.ActiveRuns.Remove(run.ChatKey);
    });

    /// <summary>
    /// Запуски, оставшиеся от убитого экземпляра: задачи и вопрос, если были; записи снимаются.
    /// Зовётся один раз на старте.
    /// </summary>
    public IReadOnlyList<ActiveRun> TakeInterruptedRuns()
    {
        List<ActiveRun> runs = [];
        Mutate(s =>
        {
            if (s.ActiveRun is { } legacy) runs.Add(legacy);
            runs.AddRange(s.ActiveRuns.Values);
            if (s.ActiveQuestion is { } question) runs.Add(question);
            s.ActiveRun = null;
            s.ActiveRuns.Clear();
            s.ActiveQuestion = null;
        });
        return runs;
    }

    /// <summary>Последние запуски, свежие сверху: копия, чтобы читать без замка.</summary>
    public IReadOnlyList<RunRecord> RecentRuns()
    {
        lock (_gate) return [.. Enumerable.Reverse(_state.RecentRuns).Select(Clone)];
    }

    /// <summary>
    /// Обновляет запись сессии, создавая при необходимости; с <paramref name="activateIn"/>
    /// делает её активной в этом ключе <see cref="GatewayState.ActiveSessions"/>. Зовётся под
    /// замком из <see cref="Mutate"/>.
    /// </summary>
    private static SessionRecord Touch(
        GatewayState state, string project, string prompt, string sessionId, DateTimeOffset now, string? activateIn)
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

        if (activateIn is not null) state.ActiveSessions[activateIn] = sessionId;
        TrimSessions(state, project);

        return record;
    }

    public void ResetUsage() => Mutate(s => s.Usage = new UsageStats { SinceUtc = DateTimeOffset.Now });

    // Правила «всегда» живут в проекте. Запуск проверяет и пишет их в своём проекте, а не в
    // текущем проекте чата: /project можно сменить посреди задачи. /rules — в текущем (ChatScope).

    public bool IsAlwaysAllowed(string projectPath, string signature)
    {
        lock (_gate)
            return _state.AlwaysAllowByProject.GetValueOrDefault(ProjectCatalog.Normalize(projectPath))
                ?.Contains(signature, StringComparer.Ordinal) == true;
    }

    public void AddAlwaysAllow(string projectPath, string signature)
    {
        Mutate(s =>
        {
            var rules = RulesOf(s, ProjectCatalog.Normalize(projectPath));
            if (!rules.Contains(signature, StringComparer.Ordinal)) rules.Add(signature);
        });
    }

    private static List<string> RulesOf(GatewayState state, string project)
    {
        if (!state.AlwaysAllowByProject.TryGetValue(project, out var rules))
            state.AlwaysAllowByProject[project] = rules = [];
        return rules;
    }

    /// <summary>Доверились ли настройкам агента в проекте именно с этим отпечатком.</summary>
    public bool IsProjectTrusted(string projectPath, string fingerprint)
    {
        lock (_gate)
            return _state.TrustedProjects.GetValueOrDefault(ProjectCatalog.Normalize(projectPath)) == fingerprint;
    }

    public void TrustProject(string projectPath, string fingerprint) =>
        Mutate(s => s.TrustedProjects[ProjectCatalog.Normalize(projectPath)] = fingerprint);

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
            // Активную не выбрасываем даже старую — ни ленты, ни темы: иначе следующий запуск
            // ушёл бы с --resume на сессию, которой в списке уже нет.
            .Where(r => !state.ActiveSessions.ContainsValue(r.Id))
            .ToArray();

        foreach (var record in stale) state.Sessions.Remove(record);
    }

    /// <summary>Проект чата под замком; null — лента. Тема без проекта — беседа в своей папке.</summary>
    private string ProjectPathLocked(ChatId? chat = null)
    {
        if (chat is { IsTopic: true })
        {
            return _state.Topics.GetValueOrDefault(chat.Key)?.ProjectPath is { Length: > 0 } topic
                ? ProjectCatalog.Normalize(topic)
                : TalkFolder(chat);
        }

        return ProjectCatalog.Normalize(_state.ProjectPath is { Length: > 0 } p ? p : _options.ProjectPath);
    }

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

    /// <summary>
    /// Что случилось с state.json при старте, текстом для чата; null — прочитан как есть.
    /// Молча начать с чистого нельзя: пропали бы сессии, доверие к проектам и правила «всегда»,
    /// а человек узнал бы об этом, только не найдя их.
    /// </summary>
    public string? LoadProblem { get; private set; }

    private string BackupPath => _path + ".bak";

    private GatewayState Load()
    {
        if (!File.Exists(_path)) return new GatewayState();

        if (TryRead(_path, out var state, out var error)) return state;

        _logger.LogError(error, "state.json не прочитан");

        // Битый файл не затираем первой же записью: он может понадобиться, чтобы достать
        // из него сессии руками.
        var kept = Path.GetFileName(_path) + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try
        {
            File.Move(_path, Path.Combine(Path.GetDirectoryName(_path)!, kept), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Повреждённый state.json не отложен в {Kept}", kept);
            kept = "(не удалось сохранить)";
        }

        if (File.Exists(BackupPath) && TryRead(BackupPath, out var backup, out _))
        {
            LoadProblem = $"⚠️ state.json был повреждён — состояние восстановлено из резервной копии, она на одно сохранение старше. Повреждённый файл: {kept}.";
            _logger.LogWarning("Состояние восстановлено из {Backup}", BackupPath);
            return backup;
        }

        LoadProblem = "⚠️ state.json был повреждён, а резервной копии нет — шлюз начал с чистого состояния: "
                      + $"сессии, доверие к проектам и правила «всегда» сброшены. Повреждённый файл: {kept}.";
        return new GatewayState();
    }

    private static bool TryRead(string path, out GatewayState state, out Exception? error)
    {
        try
        {
            if (JsonSerializer.Deserialize<GatewayState>(File.ReadAllText(path), JsonOptions) is { } loaded)
            {
                state = Rehydrate(loaded);
                error = null;
                return true;
            }

            error = new JsonException("state.json пуст");
        }
        catch (Exception ex)
        {
            // Любое: null вместо списка или ключи, различные только регистром, роняют Rehydrate,
            // а исключение из конструктора хранилища не дало бы шлюзу стартовать вовсе.
            error = ex;
        }

        state = new GatewayState();
        return false;
    }

    private static GatewayState Clone(GatewayState state) =>
        Rehydrate(JsonSerializer.Deserialize<GatewayState>(JsonSerializer.Serialize(state, JsonOptions), JsonOptions)!);

    private static SessionRecord Clone(SessionRecord record) => new()
    {
        Id = record.Id,
        ProjectPath = record.ProjectPath,
        WorkingDirectory = record.WorkingDirectory,
        Title = record.Title,
        CreatedUtc = record.CreatedUtc,
        LastActivityUtc = record.LastActivityUtc,
        Turns = record.Turns,
        ContextTokens = record.ContextTokens,
        ContextWindow = record.ContextWindow,
        ContextUtc = record.ContextUtc,
        CacheUntilUtc = record.CacheUntilUtc,
        CacheModel = record.CacheModel,
        CacheMode = record.CacheMode,
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
        state.ModelVersions = new Dictionary<string, string>(state.ModelVersions, StringComparer.OrdinalIgnoreCase);
        state.TrustedProjects = new Dictionary<string, string>(state.TrustedProjects, StringComparer.OrdinalIgnoreCase);
        state.Topics = new Dictionary<string, TopicState>(state.Topics, StringComparer.Ordinal);
        state.ActiveRuns = new Dictionary<string, ActiveRun>(state.ActiveRuns, StringComparer.Ordinal);

        // Старые версии писали голое имя инструмента («WebSearch»). Теперь в сигнатуру входят
        // аргументы, так что такие записи не совпадут никогда — убираем из /rules.
        state.AlwaysAllow.RemoveAll(IsBareToolName);
        foreach (var rules in state.AlwaysAllowByProject.Values) rules.RemoveAll(IsBareToolName);

        return state;
    }

    private static bool IsBareToolName(string signature) => !signature.Contains('(') && !signature.Contains('{');

    /// <summary>
    /// Временный файл, сброс на диск, подмена с резервной копией. Без сброса NTFS после
    /// отключения питания оставляет переименованный файл пустым: метаданные журналируются,
    /// данные — нет. Копия на шаг старше — то, из чего <see cref="Load"/> поднимется после такого.
    /// </summary>
    private void Save(GatewayState state)
    {
        var temp = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(_path))
            {
                File.Move(temp, _path);
                return;
            }

            try
            {
                File.Replace(temp, _path, BackupPath, ignoreMetadataErrors: true);
            }
            catch (IOException ex)
            {
                // ReplaceFile капризен (антивирус, открытый на чтение файл) — запись важнее копии.
                _logger.LogDebug(ex, "state.json подменён без резервной копии");
                File.Move(temp, _path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить состояние в {Path}", _path);
        }
    }
}
