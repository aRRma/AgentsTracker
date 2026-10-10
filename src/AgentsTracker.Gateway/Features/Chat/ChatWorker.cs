using System.Threading.Channels;
using AgentsTracker.Gateway.Features.Approvals;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat;
using AgentsTracker.Gateway.Infrastructure.Monitoring;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>
/// Обрабатывает сообщения двумя полосами: задачи по одному и вопросы по одному, а задача
/// с вопросом — параллельно. Иначе вопрос «вне сессии» ждал бы конца долгой задачи, ради
/// которой его и задают отдельно. Больше двух процессов агента разом не бывает. Сессии тоже
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
    IOptions<GatewayOptions> options,
    IAuditLog audit,
    RunMonitor monitor,
    ILogger<ChatWorker> logger) : BackgroundService
{
    private readonly Lane _tasks = new();
    private readonly Lane _questions = new();

    /// <summary>Очередь и идущий запуск одной полосы.</summary>
    private sealed class Lane
    {
        // Без SingleReader: с ним канал становится SingleConsumerUnboundedChannel, у которого
        // Reader.Count бросает NotSupportedException, и /status падает на QueueLength.
        public Channel<QueuedPrompt> Queue { get; } = Channel.CreateUnbounded<QueuedPrompt>();

        public CancellationTokenSource? RunCts { get; set; }

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

        /// <summary>Снимает очередь полосы; возвращает, сколько снято.</summary>
        public int Drain()
        {
            var dropped = 0;
            while (Queue.Reader.TryRead(out _)) dropped++;
            return dropped;
        }
    }

    /// <param name="Text">
    /// Задача; у команд контекста — только подпись для журнала и монитора, промпт подставит бэкенд.
    /// </param>
    /// <param name="ReplyTo">Сообщение, ответом на которое уходят статус и ответ; null — просто в чат.</param>
    private sealed record QueuedPrompt(ChatId Chat, UserId User, string Text, AgentRunKind Kind, MessageRef? ReplyTo);

    /// <summary>Идёт задача. Вопрос сюда не входит: меню, смена сессии и «Сжать» его не ждут.</summary>
    public bool IsBusy => _tasks.RunCts is not null;

    /// <summary>Задачи в очереди, без вопросов.</summary>
    public int QueueLength => _tasks.Queue.Reader.Count;

    /// <summary>Идёт вопрос — с момента, как его взяли из очереди.</summary>
    public bool IsAnswering => Volatile.Read(ref _questions.InFlight) > 0;

    /// <summary>Вопросы, ждущие своей очереди за идущим вопросом.</summary>
    public int QuestionQueueLength => _questions.Queue.Reader.Count;

    public void Enqueue(
        ChatId chat, UserId user, string text, AgentRunKind kind = AgentRunKind.Task, MessageRef? replyTo = null)
    {
        var question = kind == AgentRunKind.Question;
        (question ? _questions : _tasks).Queue.Writer.TryWrite(new QueuedPrompt(chat, user, text, kind, replyTo));

        // Монитор показывает очередь задач: вопрос в ней стоял бы, хотя ждёт не их.
        if (!question) monitor.Enqueued(Text.Preview(text));
    }

    /// <summary>
    /// Прерывает идущие запуски — и задачу, и вопрос — и снимает висящие запросы
    /// подтверждений. false — прерывать было нечего.
    /// </summary>
    public bool Stop()
    {
        // Карточка доверия висит до запуска, когда прерывать ещё нечего: без неё в ответе
        // /stop сказал бы «ничего не выполняется», сняв её.
        var cards = broker.HasPending;
        broker.CancelAll();

        // Не «||»: короткое замыкание оставило бы вопрос идти после остановленной задачи.
        return _tasks.Cancel() | _questions.Cancel() | cards;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Картинки прошлых суток переживают перезапуск: чистим их до первого сообщения.
        inbox.SweepStale();

        await Task.WhenAll(ConsumeAsync(_tasks, stoppingToken), ConsumeAsync(_questions, stoppingToken));
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

        if (!question) monitor.Dequeued();

        // Доверие — первым: карточка ждёт до четверти часа, и модель, лимит и режим, посчитанные
        // до неё, к запуску устарели бы. Вопрос идёт в пустой временной папке — настроек агента
        // там нет. Проект задачи проверяем снова после карточки: пока она ждала, /project мог
        // смениться, а доверие спрашивали про прежний.
        var project = question ? QuestionDirectory() : store.ProjectPath;
        while (!question)
        {
            switch (await trust.EnsureAsync(prompt.Chat, prompt.ReplyTo, project, stoppingToken))
            {
                case ProjectTrustGate.Outcome.Declined:
                    await DropQueueAsync(lane, prompt.Chat, "после решения о доверии к проекту", stoppingToken);
                    return;
                case ProjectTrustGate.Outcome.Cancelled:
                    return;
            }

            var current = store.ProjectPath;
            if (ProjectCatalog.Same(current, project)) break;
            project = current;
        }

        // Вопрос идёт своей моделью из конфига, а не выбранной в меню: ради него и заведён
        // отдельный запуск — не тратить лимит тяжёлой модели на простой ответ.
        var model = question ? options.Value.Question.Model : store.EffectiveModel;
        var effort = question ? options.Value.Question.Effort : store.EffectiveEffort;
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
            await DropQueueAsync(lane, prompt.Chat, AfterLimitReset, stoppingToken);
            return;
        }

        var session = question ? null : store.SessionId;

        // Пока команда ждала в очереди, /new мог снять сессию: разбирать и сжимать нечего,
        // а запуск без сессии создал бы новую ради одной команды.
        if (kind is AgentRunKind.ContextReport or AgentRunKind.Compact && session is null)
        {
            await SendPlainAsync(prompt.Chat, "Активной сессии нет — контекст пуст.", prompt.ReplyTo, stoppingToken);
            return;
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var thread = question ? "вопрос" : session is { Length: > 0 } ? session.ShortId : "новая сессия";

        Audit(prompt, AuditKinds.RunStart,
            $"{(question ? "вопрос, " : "")}{modelLabel ?? "модель по умолчанию"}, {store.EffectivePermissionMode}, effort {effort ?? "—"}",
            session);

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
            PermissionMode: PermissionMode(),
            Timeout: TimeSpan.FromMinutes(options.Value.RunTimeoutMinutes),
            // Папка чата, а не проекта: пока сообщение ждало очереди, /project мог смениться,
            // а путь к картинке в промпте уже записан. Вопросу картинки не передаются —
            // команда /ask с картинкой картинку не сохраняет.
            AttachmentsPath: question ? null : inbox.ChatDirectory(prompt.Chat),
            Kind: kind);

        // Тот же id нужен после запуска: активной станет только сессия, с которой он шёл,
        // иначе итог перетёр бы /new или смену сессии по ходу работы.
        var runSessionId = session is { Length: > 0 } ? session : request.NewSessionId;

        var status = await RunStatusMessage.StartAsync(channel, prompt.Chat, thread, prompt.ReplyTo, logger, stoppingToken);
        var startedUtc = DateTimeOffset.UtcNow;

        // Всё, что после статусного сообщения, — внутри try: сбой записи state.json или монитора
        // оставил бы статус тикать в чате, а «идёт запуск» в state.json — до ложного «прервано»
        // после перезапуска.
        IDisposable? registration = null;
        AgentRunResult result;
        try
        {
            if (!question) monitor.RunStarted(new RunStart(project, session, preview, modelLabel, store.EffectivePermissionMode, effort));

            // В state.json, а не только в памяти: если шлюз убьют посреди запуска, следующему
            // экземпляру нужно знать, кому и про что сказать «прервано».
            store.BeginRun(new ActiveRun
            {
                ChatKey = prompt.Chat.Key,
                UserKey = prompt.User.Key,
                StartedUtc = startedUtc,
                ProjectPath = project,
                SessionId = session,
                Model = modelLabel,
                Prompt = preview,
                Question = question,
            });

            // Карточки и файлы запуска — в его чат, а не туда, где идёт другой. Проект для
            // send_file у вопроса — проект чата, как было, пока вопрос шёл общей очередью.
            registration = broker.BeginRun(new RunScope(
                request.RunId, prompt.Chat, prompt.ReplyTo,
                question ? store.ProjectPath : project,
                question ? null : runSessionId));

            lane.RunCts = runCts;

            result = await agent.RunAsync(
                request, new RunObserver(store, question ? null : monitor, request, status), runCts.Token);
        }
        finally
        {
            lane.RunCts = null;
            store.EndRun(question);
            if (!question) monitor.RunFinished();
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
        var note = question ? "" : SettleSession(prompt.Chat, project, session, runSessionId, result);

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

        await SendRenderedAsync(
            prompt.Chat, Answer(kind, result) + note + Footer(kind, result, modelLabel, effort, cacheUntil), prompt.ReplyTo, stoppingToken);

        // Упёрлись в лимит — следующие задачи упрутся в него же; очередь чистим,
        // чтобы не жечь её на отказах.
        if (result.RateLimited) await DropQueueAsync(lane, prompt.Chat, AfterLimitReset, stoppingToken);
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
    private string QuestionDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "AgentsTracker-question");
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Папка вопроса не очищена: {Path}", path);
        }

        Directory.CreateDirectory(path);

        // Не очистилась — запускать в ней нельзя: настройки в ней выполнились бы без вопроса.
        if (agent.InspectProject(path) is not null)
            throw new InvalidOperationException($"в папке вопроса {path} остались настройки агента — удалите её вручную");

        return path;
    }

    /// <summary>
    /// Режим из /mode, иначе из конфига. Значение из state.json проверяем: файл правят
    /// руками, а неизвестный режим уронил бы каждый запуск.
    /// </summary>
    private string PermissionMode() =>
        store.PermissionMode is { Length: > 0 } mode && agent.Capabilities.PermissionMode.IsValid(mode)
            ? mode
            : options.Value.PermissionMode;

    /// <summary>
    /// Судьба активной сессии проекта по итогу запуска; возвращает приписку к ответу.
    /// Сбрасываем только если агент прямо сказал «не нашёл»: битый id переживёт перезапуск
    /// в state.json и будет валить каждый запуск. Пишем в проект запуска и только когда
    /// сессия там не менялась по ходу — /new и выбор из меню важнее.
    /// </summary>
    private string SettleSession(ChatId chat, string project, string? resumed, string runSessionId, AgentRunResult result)
    {
        if (result.SessionLost && resumed is { Length: > 0 })
        {
            if (!store.TrySetSessionId(project, null, onlyIfActive: resumed)) return "";

            // Контекст потерян — картинки той сессии агенту больше не понадобятся.
            inbox.ClearSession(chat, project);

            logger.LogWarning("Сессия {SessionId} сброшена: агент не нашёл её", resumed);
            audit.Write(AuditEvent.Now(AuditKinds.SessionReset, "агент не нашёл сессию", project: project, session: resumed));
            return "\n\n_Сессия сброшена — следующее сообщение начнёт новую._";
        }

        if (result.SessionId is { Length: > 0 } reported
            && !store.TrySetSessionId(project, reported, onlyIfActive: runSessionId))
        {
            logger.LogInformation(
                "Сессия {SessionId} не сделана активной: пользователь сменил сессию во время запуска", reported);
        }

        return "";
    }

    /// <summary>
    /// Мост между бэкендом и шлюзом на время запуска: новая сессия — в state.json,
    /// шаги — в статусное сообщение и монитор. <paramref name="monitor"/> null у вопроса:
    /// текущий запуск монитора — задача, и шаги вопроса вписались бы в неё.
    /// </summary>
    private sealed class RunObserver(
        SessionStore store, RunMonitor? monitor, AgentRunRequest request, RunStatusMessage status) : IAgentRunObserver
    {
        public void SessionStarted(string sessionId) =>
            store.RegisterSession(request.ProjectPath, request.Prompt, sessionId);

        public void Activity(RunActivity activity)
        {
            status.Report(activity);
            monitor?.Step(activity);
        }
    }

    private void Audit(QueuedPrompt prompt, string kind, string summary, string? session = null, string? outcome = null) =>
        audit.Write(AuditEvent.Now(kind, summary, prompt.User, prompt.Chat, store.ProjectPath, session, outcome));

    /// <summary>
    /// Подпись под ответом: id сессии, которой отвечал агент. По нему ответ находится
    /// в <c>/sessions</c> и <c>/status</c>, и по нему же сессию можно продолжить
    /// из терминала в той же папке. Модель и effort — чтобы по ответу было видно, кто
    /// и с каким усилием его дал: модель фактическая, со слов агента, а effort — переданный,
    /// агент его не сообщает. «Кэш до» — время, а не обратный отсчёт: сообщение не обновляется,
    /// и «ещё 50 мин» через полчаса читалось бы как неправда.
    /// </summary>
    private static string Footer(AgentRunKind kind, AgentRunResult result, string? model, string? effort, DateTimeOffset? cacheUntil)
    {
        var parts = new List<string>();

        if (kind == AgentRunKind.Question) parts.Add("💬 вопрос");
        else if (result.SessionId is { Length: > 0 } session) parts.Add($"🧵 `{session}`");
        else return "";

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
    /// Снимает очередь полосы целиком: после лимита до сброса окна запускать нечего, после
    /// отказа в доверии каждая следующая задача ждала бы свою карточку до четверти часа. Задачи
    /// не переносим, а возвращаем пользователю — за время ожидания они могли устареть. Только
    /// свою полосу: у вопроса своя модель и, бывает, свой лимит, а общий лимит другая полоса
    /// проверит перед своим запуском сама.
    /// </summary>
    /// <param name="when">Когда прислать снова: «после сброса лимита».</param>
    private async Task DropQueueAsync(Lane lane, ChatId chat, string when, CancellationToken ct)
    {
        var dropped = lane.Drain();
        var question = lane == _questions;
        if (!question) monitor.QueueCleared();

        if (dropped == 0) return;

        logger.LogWarning("Очередь снята: {Count} шт., вопросы: {Question}, прислать {When}", dropped, question, when);

        var what = question ? dropped.Count("вопрос", "вопроса", "вопросов") : $"{dropped} задач(и)";

        await SendPlainAsync(chat, $"Очередь снята, не запущено: {what} — пришлите их снова {when}.", null, ct);
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

    private async Task DeleteQuietlyAsync(MessageRef message, CancellationToken ct)
    {
        try
        {
            await channel.DeleteAsync(message, ct);
        }
        catch (ChannelRequestException ex)
        {
            logger.LogDebug(ex, "Не удалось удалить статусное сообщение");
        }
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        if (text.Length == 0) yield break;

        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}
