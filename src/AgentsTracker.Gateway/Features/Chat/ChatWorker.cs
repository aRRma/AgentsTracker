using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentsTracker.Gateway.Features.Approvals;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat;
using AgentsTracker.Gateway.Infrastructure.Git;
using AgentsTracker.Gateway.Infrastructure.Monitoring;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>
/// Обрабатывает сообщения полосами: задачи каждого чата (ленты и каждой темы) по одной, вопросы
/// по одному, а полосы между собой — параллельно. Иначе вопрос «вне сессии» ждал бы конца долгой
/// задачи, ради которой его и задают отдельно, а тема — чужого проекта. В одной папке запуск
/// идёт один: две темы на одном проекте иначе правили бы одни файлы разом. Сессии тоже
/// здесь: бэкенд лишь сообщает, что сессия началась или потерялась, а помнит их шлюз.
/// </summary>
public sealed class ChatWorker(
    IChatChannel channel,
    IAgentBackend agent,
    IAgentLimits limits,
    ApprovalBroker broker,
    AttachmentInbox inbox,
    ProjectTrustGate trust,
    SessionStore store,
    GitRepository git,
    IOptions<GatewayOptions> options,
    IAuditLog audit,
    RunMonitor monitor,
    ILogger<ChatWorker> logger) : BackgroundService
{
    /// <summary>Полосы задач по ключу чата. Заводятся с первой задачей чата и живут до остановки шлюза.</summary>
    private readonly ConcurrentDictionary<string, Lane> _tasks = new(StringComparer.Ordinal);
    private readonly Lane _questions = new();

    /// <summary>Новые полосы для <see cref="ExecuteAsync"/>: читать их очередь некому, пока он их не увидит.</summary>
    private readonly Channel<Lane> _newLanes = Channel.CreateUnbounded<Lane>();

    private readonly Lock _lanesGate = new();

    /// <summary>Замок на папку запуска: один запуск в папке за раз, какой бы чат его ни прислал.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Очередь и идущий запуск одной полосы.</summary>
    private sealed class Lane
    {
        // Без SingleReader: с ним канал становится SingleConsumerUnboundedChannel, у которого
        // Reader.Count бросает NotSupportedException, и /status падает на QueueLength.
        public Channel<QueuedPrompt> Queue { get; } = Channel.CreateUnbounded<QueuedPrompt>();

        /// <summary>Ставится до ожидания замка папки: иначе /stop не прервал бы и ожидание.</summary>
        public CancellationTokenSource? RunCts { get; set; }

        /// <summary>Чат идущего запуска: полоса вопросов общая, а /stop в теме прерывает только её вопрос.</summary>
        public ChatId? Chat { get; set; }

        /// <summary>
        /// Взят из очереди и ещё не закончен — шире <see cref="RunCts"/>, который ставится
        /// только перед самим процессом. Без этого вопрос, ждущий отправки статуса, не
        /// считался бы идущим, и следующему обещали бы «отвечу, не дожидаясь».
        /// </summary>
        public int InFlight;

        /// <summary>Прерывает идущий запуск полосы; false — прерывать нечего.</summary>
        public bool Cancel()
        {
            var cts = RunCts;
            if (cts is null) return false;

            try { cts.Cancel(); }
            catch (ObjectDisposedException) { return false; }

            return true;
        }

        /// <summary>Снимает очередь полосы; возвращает снятое.</summary>
        public List<QueuedPrompt> Drain()
        {
            List<QueuedPrompt> dropped = [];
            while (Queue.Reader.TryRead(out var prompt)) dropped.Add(prompt);
            return dropped;
        }
    }

    /// <param name="Text">
    /// Задача; у команд контекста — только подпись для журнала и монитора, промпт подставит бэкенд.
    /// </param>
    /// <param name="ReplyTo">Сообщение, ответом на которое уходят статус и ответ; null — просто в чат.</param>
    private sealed record QueuedPrompt(ChatId Chat, UserId User, string Text, AgentRunKind Kind, MessageRef? ReplyTo);

    /// <summary>Идёт задача чата. Вопрос сюда не входит: меню, смена сессии и «Сжать» его не ждут.</summary>
    public bool IsBusy(ChatId chat) => _tasks.TryGetValue(chat.Key, out var lane) && lane.RunCts is not null;

    /// <summary>Идёт задача хоть в одном чате — для монитора, у которого чата нет.</summary>
    public bool AnyBusy => _tasks.Values.Any(lane => lane.RunCts is not null);

    /// <summary>Задачи чата в очереди, без вопросов.</summary>
    public int QueueLength(ChatId chat) => _tasks.TryGetValue(chat.Key, out var lane) ? lane.Queue.Reader.Count : 0;

    /// <summary>Идёт вопрос — с момента, как его взяли из очереди.</summary>
    public bool IsAnswering => Volatile.Read(ref _questions.InFlight) > 0;

    /// <summary>
    /// Идёт вопрос, который этот чат может остановить: в теме — только свой, в ленте — любой, как
    /// и у /stop. Иначе тема показывала бы чужой вопрос и «Остановить», которое ничего не делает.
    /// </summary>
    public bool IsAnsweringFor(ChatId chat) => IsAnswering && (!chat.IsTopic || _questions.Chat == chat);

    /// <summary>Вопросы, ждущие своей очереди за идущим вопросом.</summary>
    public int QuestionQueueLength => _questions.Queue.Reader.Count;

    public void Enqueue(
        ChatId chat, UserId user, string text, AgentRunKind kind = AgentRunKind.Task, MessageRef? replyTo = null)
    {
        var question = kind == AgentRunKind.Question;

        // Монитор показывает очередь задач: вопрос в ней стоял бы, хотя ждёт не их. До записи в
        // очередь: свободная полоса берёт задачу сразу, и превью осталось бы в мониторе навсегда.
        if (!question) monitor.Enqueued(Text.Preview(text));
        (question ? _questions : TaskLane(chat)).Queue.Writer.TryWrite(new QueuedPrompt(chat, user, text, kind, replyTo));
    }

    private Lane TaskLane(ChatId chat)
    {
        lock (_lanesGate)
        {
            if (_tasks.TryGetValue(chat.Key, out var lane)) return lane;

            _tasks[chat.Key] = lane = new Lane();
            _newLanes.Writer.TryWrite(lane);
            return lane;
        }
    }

    /// <summary>
    /// Прерывает идущие запуски чата — задачу и его вопрос — и снимает его висящие запросы
    /// подтверждений. Из ленты — всё во всех темах: это общий рубильник, каким /stop был до тем.
    /// false — прерывать было нечего.
    /// </summary>
    public bool Stop(ChatId chat)
    {
        var all = !chat.IsTopic;

        // Карточка доверия висит до запуска, когда прерывать ещё нечего: без неё в ответе
        // /stop сказал бы «ничего не выполняется», сняв её.
        var cards = broker.HasPending(all ? null : chat);
        broker.Cancel(all ? null : chat);

        // Не «||»: короткое замыкание оставило бы вопрос идти после остановленной задачи.
        var stopped = cards;
        foreach (var (key, lane) in _tasks)
            if (all || key == chat.Key) stopped |= lane.Cancel();

        if (all || _questions.Chat == chat) stopped |= _questions.Cancel();
        return stopped;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Картинки прошлых суток переживают перезапуск: чистим их до первого сообщения.
        inbox.SweepStale();

        List<Task> consumers = [ConsumeAsync(_questions, stoppingToken)];
        try
        {
            await foreach (var lane in _newLanes.Reader.ReadAllAsync(stoppingToken))
                consumers.Add(ConsumeAsync(lane, stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(Lane lane, CancellationToken stoppingToken)
    {
        await foreach (var prompt in lane.Queue.Reader.ReadAllAsync(stoppingToken))
        {
            Interlocked.Increment(ref lane.InFlight);
            try
            {
                await ProcessAsync(lane, prompt, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при обработке сообщения");
                await SendPlainAsync(prompt.Chat, $"Внутренняя ошибка шлюза: {ex.Message}", prompt.ReplyTo, stoppingToken);
            }
            finally
            {
                // Сбой до конца запуска (тема удалена, статус не ушёл) иначе оставил бы полосу
                // «занятой» навсегда.
                lane.RunCts = null;
                // Иначе общая полоса вопросов помнила бы чат прошлого запуска, пока следующий не взят в работу.
                lane.Chat = null;
                Interlocked.Decrement(ref lane.InFlight);
            }
        }
    }

    /// <summary>
    /// Один запуск. Вопрос не трогает монитор (текущий запуск и очередь там — задачи):
    /// иначе его шаги смешались бы с шагами идущей задачи.
    /// </summary>
    private async Task ProcessAsync(Lane lane, QueuedPrompt prompt, CancellationToken stoppingToken)
    {
        var kind = prompt.Kind;
        var question = kind == AgentRunKind.Question;

        if (!question) monitor.Dequeued(Text.Preview(prompt.Text));

        // Доверие — первым: карточка ждёт до четверти часа, и модель, лимит и режим, посчитанные
        // до неё, к запуску устарели бы. Вопрос идёт в пустой временной папке — настроек агента
        // там нет. Проект задачи проверяем снова после карточки: пока она ждала, /project мог
        // смениться, а доверие спрашивали про прежний. Сессию фиксируем здесь же: её папка (worktree,
        // куда её увёл агент) — тоже то, чему доверились.
        var scope = store.For(prompt.Chat);
        var project = question ? QuestionDirectory() : scope.ProjectPath;
        string? session = null;
        var sessionFolder = project;
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        IDisposable? folderLock = null;

        // Чат — раньше токена: /stop читает их в этом порядке и иначе мог бы снять чужой вопрос.
        void Arm()
        {
            lane.Chat = prompt.Chat;
            lane.RunCts = runCts;
        }

        while (!question)
        {
            while (true)
            {
                // Беседа — по итоговой папке, а не по теме при постановке: пока задача ждала, тему
                // могли привязать к проекту. Её папку чистим до проверки доверия, как папку вопроса.
                if (SessionStore.IsTalkFolder(project)) EmptyFolder(project);

                session = scope.SessionId;
                // Папку запуска берём ту же, которой доверились: считай её заново после карточки, и
                // worktree, появившийся за это время, запустился бы без проверки.
                sessionFolder = store.SessionFolder(project, session);
                switch (await TrustAsync(prompt, project, sessionFolder, stoppingToken))
                {
                    case ProjectTrustGate.Outcome.Declined:
                        await DropQueueAsync(lane, "после решения о доверии к проекту", stoppingToken);
                        return;
                    case ProjectTrustGate.Outcome.Cancelled:
                        return;
                }

                var current = scope.ProjectPath;
                if (ProjectCatalog.Same(current, project) && scope.SessionId == session) break;
                project = current;
            }

            Arm();

            // Замок — до модели, лимита и сессии: ожидание бывает долгим, и посчитанное до него
            // устарело бы — на исчерпанном за это время тарифе CLI ушёл бы на кредиты. Карточка
            // доверия — снаружи: она ждёт до четверти часа и держала бы чужую тему. Вопрос идёт в
            // своей пустой папке и ни с кем её не делит. Замок — на папку сессии, а не проекта: агент
            // правит worktree, куда увёл сессию, и этот же worktree может быть проектом другой темы.
            folderLock = await LockFolderAsync(sessionFolder, prompt, runCts.Token, stoppingToken);
            if (folderLock is null) return;

            // Пока ждали замок, запуск соседней темы в этой папке мог положить в неё хук, а /new или
            // /project — сменить сессию и её папку. Под замком проверяем без карточки; что-то
            // изменилось — отпускаем папку и проходим доверие заново.
            var admitted = false;
            try
            {
                admitted = ProjectCatalog.Same(scope.ProjectPath, project) && scope.SessionId == session
                    && Trusted(project) && (ProjectCatalog.Same(sessionFolder, project) || Trusted(sessionFolder));
            }
            finally
            {
                if (!admitted) folderLock.Dispose();
            }

            if (admitted) break;

            // Доверие — заново к проекту, который выбран сейчас: прежний мог смениться через /project.
            project = scope.ProjectPath;
        }

        if (question) Arm();
        using var heldLock = folderLock;

        var talk = !question && SessionStore.IsTalkFolder(project);

        // Вопрос идёт своей моделью из конфига, а не выбранной в меню: ради него и заведён
        // отдельный запуск — не тратить лимит тяжёлой модели на простой ответ.
        var model = question ? options.Value.Question.Model : scope.EffectiveModel;
        var effort = question ? options.Value.Question.Effort : scope.EffectiveEffort;
        // Человеку — с версией: по алиасу «opus» в журнале и мониторе не понять, какой Opus это был.
        var modelLabel = store.ModelLabel(model, agent.Capabilities.Model);

        // Лимит проверяем здесь, а не при постановке в очередь: пока сообщение ждало,
        // предыдущие запуски могли выбрать окно. На исчерпанном тарифе CLI ушёл бы
        // на кредиты, а это запрещено. Разбивка контекста модель не зовёт — ей лимит
        // не помеха, и как раз на исчерпанном тарифе полезно понять, чем забит контекст.
        if (kind != AgentRunKind.ContextReport && await limits.RefusalAsync(model, stoppingToken) is { } exhausted)
        {
            Audit(prompt, AuditKinds.LimitRefused, "лимит тарифа");
            await SendPlainAsync(prompt.Chat, exhausted, prompt.ReplyTo, stoppingToken);
            await DropQueueAsync(lane, AfterLimitReset, stoppingToken);
            return;
        }

        // /stop, пока проверяли лимит: процесс ради мгновенной отмены не запускаем.
        if (runCts.IsCancellationRequested) return;

        // /new или выбор сессии, пока проверяли лимит (он ходит в сеть): берём новую сессию, если
        // она в той же папке, что под замком. Иначе идём с прежней: папку без замка правил бы и
        // запуск соседней темы, а заново ждать замок и карточку — устарели бы модель и лимит.
        if (!question && scope.SessionId is var latest && latest != session
            && store.SessionFolder(project, latest) is var latestFolder
            && ProjectCatalog.Same(latestFolder, sessionFolder))
        {
            session = latest;
            sessionFolder = latestFolder;
        }

        // Пока команда ждала в очереди, /new мог снять сессию: разбирать и сжимать нечего,
        // а запуск без сессии создал бы новую ради одной команды.
        if (kind is AgentRunKind.ContextReport or AgentRunKind.Compact && session is null)
        {
            await SendPlainAsync(prompt.Chat, "Активной сессии нет — контекст пуст.", prompt.ReplyTo, stoppingToken);
            return;
        }

        var thread = question ? "вопрос" : session is { Length: > 0 } ? session.ShortId : "новая сессия";

        // Ветка папки сессии, а не проекта: агент мог увести сессию в worktree (EnterWorktree), и
        // CLI продолжит её там, хотя процесс запущен в проекте. Уход по абсолютным путям шлюз всё
        // так же не видит. Вопрос идёт во временной папке — ветки нет.
        var folder = question ? null : new RunFolder(sessionFolder);
        var startBranch = folder is null ? null : git.Head(folder.Path);

        var preview = Text.Preview(prompt.Text);

        // Сессию и папку фиксируем до запуска: переключение из меню посреди работы иначе
        // разведёт каталог процесса и проект, которому запишется сессия. Id новой сессии
        // выдаём сами — так /stop или падение первого запуска не теряют ветку.
        var request = new AgentRunRequest(
            Guid.NewGuid().ToString("N"),
            prompt.Text, project,
            ResumeSessionId: session,
            NewSessionId: Guid.NewGuid().ToString(),
            Model: model,
            Effort: effort,
            PermissionMode: PermissionMode(scope),
            Timeout: TimeSpan.FromMinutes(options.Value.RunTimeoutMinutes),
            // Папка чата, а не проекта: пока сообщение ждало очереди, /project мог смениться,
            // а путь к картинке в промпте уже записан. Вопросу картинки не передаются —
            // команда /ask с картинкой картинку не сохраняет. Беседе тоже: картинку ей не
            // принимают, а лишняя папка — лишний доступ.
            AttachmentsPath: question || talk ? null : inbox.ChatDirectory(prompt.Chat),
            Kind: kind,
            WebOnly: talk);

        // Тот же id нужен после запуска: активной станет только сессия, с которой он шёл,
        // иначе итог перетёр бы /new или смену сессии по ходу работы.
        var runSessionId = session is { Length: > 0 } ? session : request.NewSessionId;

        // Статус — первое, что уходит в чат: все темы лички делят один предел отправки, и 429 здесь
        // ждём, как у частей ответа.
        RunStatusMessage? started = null;
        try
        {
            await RateLimitRetry.OnceAsync(
                async () => started = await RunStatusMessage.StartAsync(
                    channel, prompt.Chat, thread, folder is null ? null : () => git.Head(folder.Path)?.Label, prompt.ReplyTo, logger, stoppingToken),
                wait => logger.LogWarning("Канал просит подождать {Wait}, статус запуска будет отправлен повторно", wait),
                stoppingToken);
        }
        catch (ChannelRequestException ex) when (ex.Kind == ChannelFailure.CannotReach)
        {
            // Тему удалили в клиенте: Telegram об этом не сообщает, узнаём по отказу. Ни статус, ни
            // ответ туда не дойдут — не запускаем, и очередь темы туда же: иначе каждая задача из
            // неё жгла бы запуск впустую. Полоса вопросов общая — её чужие вопросы не трогаем.
            List<QueuedPrompt> dropped = question ? [] : lane.Drain();
            foreach (var queued in dropped) monitor.Dequeued(Text.Preview(queued.Text));
            logger.LogWarning("Чат {Chat} недоступен: запуск не начат, снято из очереди: {Dropped}", prompt.Chat.Key, dropped.Count);
            return;
        }

        var status = started!;
        var startedUtc = DateTimeOffset.UtcNow;

        // После статуса: не ушёл он — запуска не было, и «начат» без конца в журнале соврал бы.
        Audit(prompt, AuditKinds.RunStart,
            $"{(question ? "вопрос, " : talk ? "беседа, " : "")}{modelLabel ?? "модель по умолчанию"}, {scope.EffectivePermissionMode}, effort {effort ?? "—"}"
            + (startBranch is null ? "" : $", ветка {startBranch.Label}"),
            session);

        // Всё, что после статусного сообщения, — внутри try: сбой записи state.json или монитора
        // оставил бы статус тикать в чате, а «идёт запуск» в state.json — до ложного «прервано»
        // после перезапуска.
        IDisposable? registration = null;
        var watched = false;
        var active = new ActiveRun
        {
            ChatKey = prompt.Chat.Key,
            UserKey = prompt.User.Key,
            StartedUtc = startedUtc,
            ProjectPath = project,
            SessionId = session,
            Model = modelLabel,
            Prompt = preview,
            Question = question,
        };
        AgentRunResult result;
        try
        {
            // Монитор показывает один запуск — первый из идущих; шаги и конец чужого его не трогают.
            watched = !question
                && monitor.RunStarted(new RunStart(project, session, preview, modelLabel, scope.EffectivePermissionMode, effort, startBranch?.Label));

            // В state.json, а не только в памяти: если шлюз убьют посреди запуска, следующему
            // экземпляру нужно знать, кому и про что сказать «прервано».
            store.BeginRun(active);

            // Карточки и файлы запуска — в его чат, а не туда, где идёт другой. Проект для
            // send_file у вопроса — проект чата, как было, пока вопрос шёл общей очередью.
            registration = broker.BeginRun(new RunScope(
                request.RunId, prompt.Chat, prompt.ReplyTo,
                question ? scope.ProjectPath : project,
                question ? null : runSessionId));

            result = await agent.RunAsync(
                request, new RunObserver(store, scope, watched ? monitor : null, request, runSessionId, folder, status), runCts.Token);
        }
        finally
        {
            lane.RunCts = null;
            store.EndRun(active);
            if (watched) monitor.RunFinished();
            // Процесс завершён — отвечать на его карточки уже некому. Карточки другого запуска
            // остаются: тот ещё идёт. До сетевых вызовов ниже: их сбой оставил бы запуск
            // зарегистрированным, и поздние вызовы его процесса не получили бы отказ.
            registration?.Dispose();
            await status.DisposeAsync();
            await DeleteQuietlyAsync(status.Message, stoppingToken);
        }

        // Неудачный запуск тоже расходует тариф, поэтому пишем и его — если агент успел сказать.
        // Разбивка контекста модель не звала: в статистике она была бы запуском без расхода.
        if (result.Usage is { } usage && kind != AgentRunKind.ContextReport)
        {
            // Промах считаем только у продолжения задачи: сжатие переписывает сессию нарочно,
            // а новой сессии читать нечего. Прошлый контекст берём до RecordContext ниже. Без срока
            // кэша (сессия старше поля или сжата) запись заново ожидаема: после сжатия кэша под
            // пересказ ещё нет, и первый запрос прочитает только общее начало, а не «половину».
            var previous = kind == AgentRunKind.Task && store.Session(session) is { CacheUntilUtc: not null } known
                ? known.ContextTokens
                : 0;
            store.RecordRun(project, prompt.Text, result.SessionId, usage, CacheGuard.Miss(previous, usage.FirstCacheRead));
            store.RecordModelVersion(model, usage.PrimaryModel);
        }

        if (result.SessionId is { Length: > 0 } reported && result.Usage is { ContextTokens: > 0 } context)
            store.RecordContext(reported, context.ContextTokens, context.ContextWindow);

        // Разбивка контекста и локальная команда вроде /cost модель не звали и кэш не трогали:
        // прошлый срок остаётся верным. Стирает его только сжатие — оно заменяет начало сессии.
        // Прерванный запуск расхода не сообщает, поэтому модель — выбранная, а не ответившая.
        DateTimeOffset? cacheUntil = null;
        if (kind != AgentRunKind.ContextReport && result.SessionId is { Length: > 0 } cached
            && (result.LastRequestUtc is not null || result.Compaction is not null))
            cacheUntil = store.RecordCache(cached, result.LastRequestUtc, result.CacheTtl,
                result.Usage?.PrimaryModel ?? modelLabel, CacheGuard.ModeScope(agent.Capabilities, request.PermissionMode));

        // Вопрос сессию не создавал и не продолжал — трогать активную нечего.
        var note = question ? "" : SettleSession(scope, prompt.Chat, project, session, runSessionId, result);

        var outcome = result switch
        {
            { Cancelled: true } => "cancel",
            { RateLimited: true } => "rate-limit",
            { Ok: true } => "ok",
            _ => "error",
        };

        Audit(prompt, AuditKinds.RunEnd, $"{result.Duration.Elapsed}, ходов {result.Usage?.Turns ?? 0}", result.SessionId, outcome);

        store.RecordRunOutcome(new RunRecord
        {
            StartedUtc = startedUtc,
            ProjectPath = project,
            SessionId = result.SessionId ?? session,
            Prompt = preview,
            // Та, что ответила: выбранный алиас мог уже раскрыться в другую версию.
            Model = result.Usage?.PrimaryModel ?? modelLabel,
            Outcome = outcome,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            Turns = result.Usage?.Turns ?? 0,
            ToolCalls = status.ToolCalls,
            InputTokens = result.Usage?.InputTokens ?? 0,
            OutputTokens = result.Usage?.OutputTokens ?? 0,
        });

        // Ветку — до отпускания папки: следующий в ней запуск мог бы её сменить, и подпись соврала бы.
        var branch = folder is null ? null : BranchCaption(startBranch, git.Head(folder.Path));

        // Сессия и расход записаны — папку можно отдать следующему, не дожидаясь отправки ответа.
        heldLock?.Dispose();

        await SendRenderedAsync(
            prompt.Chat,
            Answer(kind, result) + note + Footer(kind, result, modelLabel, effort, cacheUntil, branch),
            prompt.ReplyTo, stoppingToken);

        // Упёрлись в лимит — следующие задачи упрутся в него же; очередь чистим,
        // чтобы не жечь её на отказах.
        if (result.RateLimited) await DropQueueAsync(lane, AfterLimitReset, stoppingToken);
    }

    /// <summary>
    /// Текст ответа. У /compact своего текста нет — CLI возвращает пустой итог, и без
    /// подстановки в чат ушло бы «(пустой ответ)» вместо «сжато».
    /// </summary>
    private static string Answer(AgentRunKind kind, AgentRunResult result) => (kind, result) switch
    {
        (_, { Ok: false }) => $"⚠️ {result.Text}",
        (AgentRunKind.Compact, { Compaction: { } c }) =>
            $"🗜 Контекст сжат: {c.Before.Tokens} → {c.After.Tokens} токенов. Сессия та же, разговор продолжается.",
        (AgentRunKind.Compact, _) => "🗜 Сжатие завершено, но агент не сообщил, сколько стало.",
        (AgentRunKind.ContextReport, _) => "📦 **Контекст сессии**\n\n" + result.Text,
        _ => result.Text,
    };

    /// <summary>
    /// Рабочая папка вопроса. Не проект: иначе подтянулись бы его CLAUDE.md и настройки,
    /// а вопрос к проекту не относится. И не папка данных: там state.json и секреты.
    /// Пустая папка во временных — нечего читать и нечего испортить. Пустая заново перед каждым
    /// вопросом: агент задачи может сам положить туда <c>.claude/settings.json</c>, и его хук
    /// выполнялся бы на каждом /ask — а доверие к проекту у вопроса не спрашивается.
    /// </summary>
    private string QuestionDirectory() => EmptyFolder(Path.Combine(Path.GetTempPath(), "AgentsTracker-question"));

    /// <summary>
    /// Пустая папка заново — у вопроса и у беседы. Доверие у них не спрашивается, а беседа
    /// продолжает сессию по пути папки, не по содержимому: всё, что в ней лежит, — лишнее.
    /// </summary>
    private string EmptyFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Временная папка не очищена: {Path}", path);
        }

        Directory.CreateDirectory(path);

        // Не очистилась — запускать в ней нельзя: настройки в ней выполнились бы без вопроса.
        if (agent.InspectProject(path) is not null)
            throw new InvalidOperationException($"в папке {path} остались настройки агента — удалите её вручную");

        return path;
    }

    /// <summary>
    /// Ждёт, пока в папке закончится чужой запуск. null — ожидание прервал /stop. Чат узнаёт, почему
    /// молчит: иначе тема стояла бы без статуса, пока в соседней идёт долгая задача.
    /// </summary>
    private async Task<IDisposable?> LockFolderAsync(string folder, QueuedPrompt prompt, CancellationToken run, CancellationToken stopping)
    {
        var gate = _folders.GetOrAdd(ProjectCatalog.Normalize(folder), _ => new SemaphoreSlim(1, 1));

        if (!gate.Wait(0))
        {
            MessageRef? notice = null;
            try
            {
                notice = await channel.SendAsync(prompt.Chat,
                    new OutgoingMessage("⏳ В проекте идёт другой запуск — начну, когда он закончится.", Rich: false, ReplyTo: prompt.ReplyTo), stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Не удалось отправить сообщение в чат {Chat}", prompt.Chat.Key);
            }

            try
            {
                await gate.WaitAsync(run);
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                await DeleteQuietlyAsync(notice, stopping);
                return null;
            }

            // Ожидание кончилось — стартом или /stop: висящее «⏳» обещало бы запуск, которого не
            // будет. Замок уже взят: удаление не бросает ничего, кроме отмены при остановке шлюза, а
            // на неё папку отпускаем — иначе она осталась бы занятой.
            var held = new FolderLock(gate);
            try
            {
                await DeleteQuietlyAsync(notice, stopping);
            }
            catch
            {
                held.Dispose();
                throw;
            }

            return held;
        }

        return new FolderLock(gate);
    }

    /// <summary>Отпускает папку ровно один раз: явно перед ответом и ещё раз на выходе из метода.</summary>
    private sealed class FolderLock(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    /// <summary>
    /// Режим из /mode, иначе из конфига. Значение из state.json проверяем: файл правят
    /// руками, а неизвестный режим уронил бы каждый запуск.
    /// </summary>
    private string PermissionMode(SessionStore.ChatScope scope) =>
        scope.PermissionMode is { Length: > 0 } mode && agent.Capabilities.PermissionMode.IsValid(mode)
            ? mode
            : options.Value.PermissionMode;

    /// <summary>
    /// Судьба активной сессии проекта по итогу запуска; возвращает приписку к ответу.
    /// Сбрасываем только если агент прямо сказал «не нашёл»: битый id переживёт перезапуск
    /// в state.json и будет валить каждый запуск. Пишем в проект запуска и только когда
    /// сессия там не менялась по ходу — /new и выбор из меню важнее.
    /// </summary>
    private string SettleSession(
        SessionStore.ChatScope scope, ChatId chat, string project, string? resumed, string runSessionId, AgentRunResult result)
    {
        if (result.SessionLost && resumed is { Length: > 0 })
        {
            if (!scope.TrySetSessionId(project, null, onlyIfActive: resumed)) return "";

            // Контекст потерян — картинки той сессии агенту больше не понадобятся.
            inbox.ClearSession(chat, project);

            logger.LogWarning("Сессия {SessionId} сброшена: агент не нашёл её", resumed);
            audit.Write(AuditEvent.Now(AuditKinds.SessionReset, "агент не нашёл сессию", chat: chat, project: project, session: resumed));
            return "\n\n_Сессия сброшена — следующее сообщение начнёт новую._";
        }

        if (result.SessionId is { Length: > 0 } reported
            && !scope.TrySetSessionId(project, reported, onlyIfActive: runSessionId))
        {
            logger.LogInformation(
                "Сессия {SessionId} не сделана активной: пользователь сменил сессию во время запуска", reported);
        }

        return "";
    }

    /// <summary>
    /// Доверие к папкам запуска: проекту и worktree, куда агент увёл сессию. <c>--resume</c>
    /// вернёт агента туда, и настройки той папки выполнятся без карточки так же, как настройки проекта.
    /// </summary>
    private async Task<ProjectTrustGate.Outcome> TrustAsync(
        QueuedPrompt prompt, string project, string folder, CancellationToken ct)
    {
        var outcome = await trust.EnsureAsync(prompt.Chat, prompt.ReplyTo, project, ct);

        return outcome == ProjectTrustGate.Outcome.Trusted && !ProjectCatalog.Same(folder, project)
            ? await trust.EnsureAsync(prompt.Chat, prompt.ReplyTo, folder, ct)
            : outcome;
    }

    /// <summary>Доверие без карточки: настроек агента в папке нет или их отпечаток уже доверен.</summary>
    private bool Trusted(string folder) =>
        agent.InspectProject(folder) is not { } settings || store.IsProjectTrusted(folder, settings.Fingerprint);

    /// <summary>
    /// Папка сессии на время запуска. Меняет её поток чтения вывода агента, а читает каждый такт
    /// статус — отсюда volatile.
    /// </summary>
    private sealed class RunFolder(string path)
    {
        private string _path = path;

        public string Path
        {
            get => Volatile.Read(ref _path);
            set => Volatile.Write(ref _path, value);
        }
    }

    /// <summary>
    /// Мост между бэкендом и шлюзом на время запуска: новая сессия и её папка — в state.json,
    /// шаги — в статусное сообщение и монитор. <paramref name="monitor"/> и <paramref name="folder"/>
    /// null у вопроса: текущий запуск монитора — задача, и шаги вопроса вписались бы в неё, а
    /// временная папка вопроса — не папка сессии.
    /// </summary>
    private sealed class RunObserver(
        SessionStore store, SessionStore.ChatScope scope, RunMonitor? monitor, AgentRunRequest request, string runSessionId,
        RunFolder? folder, RunStatusMessage status) : IAgentRunObserver
    {
        public void SessionStarted(string sessionId) =>
            scope.RegisterSession(request.ProjectPath, request.Prompt, sessionId);

        public void Activity(RunActivity activity)
        {
            status.Report(activity);
            monitor?.Step(activity);
        }

        /// <summary>
        /// Пишем сразу, а не по итогу: прерванный запуск тоже мог увести сессию, и следующий
        /// продолжится уже там. Совпадение с ожидаемой папкой ничего не пишет — это каждый запуск.
        /// </summary>
        public void WorkingDirectoryChanged(string path)
        {
            if (folder is null || ProjectCatalog.Same(path, folder.Path)) return;

            folder.Path = path;
            store.RecordWorkingDirectory(runSessionId, path);
        }
    }

    private void Audit(QueuedPrompt prompt, string kind, string summary, string? session = null, string? outcome = null) =>
        audit.Write(AuditEvent.Now(kind, summary, prompt.User, prompt.Chat, store.For(prompt.Chat).ProjectPath, session, outcome));

    /// <summary>
    /// Подпись под ответом: id сессии, которой отвечал агент. По нему ответ находится
    /// в <c>/sessions</c> и <c>/status</c>, и по нему же сессию можно продолжить
    /// из терминала в той же папке. Модель и effort — чтобы по ответу было видно, кто
    /// и с каким усилием его дал: модель фактическая, со слов агента, а effort — переданный,
    /// агент его не сообщает. «Кэш до» — время, а не обратный отсчёт: сообщение не обновляется,
    /// и «ещё 50 мин» через полчаса читалось бы как неправда.
    /// </summary>
    private static string Footer(
        AgentRunKind kind, AgentRunResult result, string? model, string? effort, DateTimeOffset? cacheUntil, string? branch)
    {
        var parts = new List<string>();

        if (kind == AgentRunKind.Question) parts.Add("💬 вопрос");
        else if (result.SessionId is { Length: > 0 } session) parts.Add($"🧵 `{session}`");
        else return "";

        // В код-спане: «_» и «*» в имени ветки иначе ушли бы в курсив, а «`» закрыл бы спан.
        if (branch is not null) parts.Add($"🌿 `{branch.Replace('`', '\'')}`");

        // Разбивка контекста модель не звала — подписывать её моделью значило бы соврать.
        // Effort в скобках вплотную к модели: «opus-5-5(H)» — это одна характеристика ответа,
        // отдельным пунктом подпись длиннее, а буква без модели читается как загадка.
        if (kind != AgentRunKind.ContextReport)
        {
            var answered = result.Usage?.PrimaryModel ?? model ?? "";
            var level = effort.EffortShort is { Length: > 0 } letters ? $"({letters})" : "";
            if (answered.Length + level.Length > 0) parts.Add(answered + level);
        }

        if (result.Usage is { Turns: > 0 } usage) parts.Add($"{usage.Turns} х");
        parts.Add(result.Duration.Elapsed);

        if (cacheUntil is { } until)
            parts.Add(until > DateTimeOffset.UtcNow ? $"🧊 кэш до {until.Clock}" : "🧊 кэш остыл");

        return "\n\n" + string.Join(" · ", parts);
    }

    /// <summary>
    /// Ветка для подписи под ответом. Агент мог переключить её сам — тогда видно, откуда и куда:
    /// по одной конечной ветке не понять, что запуск начинался в другой.
    /// </summary>
    private static string? BranchCaption(GitHead? start, GitHead? end) => (start, end) switch
    {
        ({ } from, { } to) when (from.Branch, from.DetachedSha) != (to.Branch, to.DetachedSha) => $"{from.Label} → {to.Label}",
        (_, { } to) => to.Label,
        ({ } from, null) => from.Label,
        _ => null,
    };

    /// <summary>
    /// Снимает очередь полосы целиком: после лимита до сброса окна запускать нечего, после
    /// отказа в доверии каждая следующая задача ждала бы свою карточку до четверти часа. Задачи
    /// не переносим, а возвращаем пользователю — за время ожидания они могли устареть. Только
    /// свою полосу: у вопроса своя модель и, бывает, свой лимит, а общий лимит другая полоса
    /// проверит перед своим запуском сама. Полоса вопросов общая для всех тем — поэтому сказать
    /// надо каждому чату, чьё снято, а не только тому, на чьём запуске упёрлись.
    /// </summary>
    /// <param name="when">Когда прислать снова: «после сброса лимита».</param>
    private async Task DropQueueAsync(Lane lane, string when, CancellationToken ct)
    {
        var dropped = lane.Drain();
        var question = lane == _questions;

        // Очередь монитора общая для всех полос задач: снимаем столько, сколько снято здесь.
        if (!question) foreach (var queued in dropped) monitor.Dequeued(Text.Preview(queued.Text));

        if (dropped.Count == 0) return;

        logger.LogWarning("Очередь снята: {Count} шт., вопросы: {Question}, прислать {When}", dropped.Count, question, when);

        foreach (var chat in dropped.GroupBy(p => p.Chat))
        {
            var count = chat.Count();
            var what = question ? count.Count("вопрос", "вопроса", "вопросов") : $"{count} задач(и)";
            await SendPlainAsync(chat.Key, $"Очередь снята, не запущено: {what} — пришлите их снова {when}.", null, ct);
        }
    }

    private const string AfterLimitReset = "после сброса лимита";

    /// <summary>
    /// Ответ агента: markdown переводится в формат канала и режется под его лимит.
    /// Не влезающая порция уходит документом. <paramref name="replyTo"/> ставится на каждую
    /// часть, а не на первую: между частями может вклиниться ответ другого запуска.
    /// </summary>
    private async Task SendRenderedAsync(ChatId chat, string markdown, MessageRef? replyTo, CancellationToken ct)
    {
        foreach (var part in MarkdownRenderer.Render(markdown, channel.Limits.MessageLength))
        {
            try
            {
                await SendPartAsync(chat, part, replyTo, ct);
            }
            catch (ChannelRequestException ex)
            {
                // Следующие порции всё равно пробуем: отказ бывает и на одной, а выход
                // из цикла молча оставил бы пользователя с началом ответа.
                logger.LogError(ex, "Не удалось отправить часть ответа в чат {Chat}", chat.Key);
            }
        }
    }

    /// <summary>
    /// Длинный ответ идёт серией сообщений, и канал вправе притормозить посреди неё. Срок
    /// он называет сам: ждём один раз и повторяем ту же часть, иначе она выпала бы из ответа.
    /// </summary>
    private Task SendPartAsync(ChatId chat, OutgoingPart part, MessageRef? replyTo, CancellationToken ct) =>
        RateLimitRetry.OnceAsync(
            () => SendOnceAsync(chat, part, replyTo, ct),
            wait => logger.LogWarning("Канал просит подождать {Wait}, часть ответа будет отправлена повторно", wait),
            ct);

    private async Task SendOnceAsync(ChatId chat, OutgoingPart part, MessageRef? replyTo, CancellationToken ct)
    {
        if (part.DocumentText is { } document)
            await channel.SendFileAsync(chat, part.DocumentName ?? "fragment.txt", document, replyTo, ct);
        else if (part.Html is { Length: > 0 } html)
            await channel.SendAsync(chat, new OutgoingMessage(html, ReplyTo: replyTo), ct);
    }

    private async Task SendPlainAsync(ChatId chat, string text, MessageRef? replyTo, CancellationToken ct)
    {
        foreach (var chunk in Chunk(text, channel.Limits.MessageLength))
        {
            try
            {
                await channel.SendAsync(chat, new OutgoingMessage(chunk, Rich: false, ReplyTo: replyTo), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Не удалось отправить сообщение в чат {Chat}", chat.Key);
                return;
            }
        }
    }

    /// <summary>
    /// Удаляет служебное сообщение (статус, «⏳») и не бросает ничего, кроме отмены при остановке
    /// шлюза. Канал переводит в <see cref="ChannelRequestException"/> не всё: таймаут или обрыв сети
    /// вылетел бы из finally запуска, и ответ агента не дошёл бы до чата.
    /// </summary>
    private async Task DeleteQuietlyAsync(MessageRef? message, CancellationToken ct)
    {
        if (message is null) return;

        try
        {
            await channel.DeleteAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Не удалось удалить служебное сообщение");
        }
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        if (text.Length == 0) yield break;

        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}
