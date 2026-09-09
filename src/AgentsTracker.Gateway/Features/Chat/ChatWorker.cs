using System.Threading.Channels;
using AgentsTracker.Gateway.Features.Approvals;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat;
using AgentsTracker.Gateway.Infrastructure.Monitoring;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>
/// Обрабатывает сообщения по одному: пока идёт запуск, новые копятся в очереди, а не
/// поднимают второй процесс. Сессии тоже здесь: бэкенд лишь сообщает, что сессия началась
/// или потерялась, а помнит их шлюз.
/// </summary>
public sealed class ChatWorker(
    IChatChannel channel,
    IAgentBackend agent,
    IAgentLimits limits,
    ApprovalBroker broker,
    AttachmentInbox inbox,
    SessionStore store,
    IOptions<GatewayOptions> options,
    IAuditLog audit,
    RunMonitor monitor,
    ILogger<ChatWorker> logger) : BackgroundService
{
    // Без SingleReader: с ним канал становится SingleConsumerUnboundedChannel, у которого
    // Reader.Count бросает NotSupportedException, и /status падает на QueueLength.
    private readonly Channel<QueuedPrompt> _queue = Channel.CreateUnbounded<QueuedPrompt>();

    private CancellationTokenSource? _runCts;

    private sealed record QueuedPrompt(ChatId Chat, UserId User, string Text);

    public bool IsBusy => _runCts is not null;

    public int QueueLength => _queue.Reader.Count;

    public void Enqueue(ChatId chat, UserId user, string text)
    {
        _queue.Writer.TryWrite(new QueuedPrompt(chat, user, text));
        monitor.Enqueued(Text.Preview(text));
    }

    /// <summary>Прерывает текущий запуск и снимает висящие запросы подтверждений.</summary>
    public bool Stop()
    {
        broker.CancelAll();

        var cts = _runCts;
        if (cts is null) return false;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return false; }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var prompt in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(prompt, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при обработке сообщения");
                await SendPlainAsync(prompt.Chat, $"Внутренняя ошибка шлюза: {ex.Message}", stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(QueuedPrompt prompt, CancellationToken stoppingToken)
    {
        monitor.Dequeued();

        // Лимит проверяем здесь, а не при постановке в очередь: пока сообщение ждало,
        // предыдущие запуски могли выбрать окно. На исчерпанном тарифе CLI ушёл бы
        // на кредиты, а это запрещено.
        if (await limits.RefusalAsync(store.EffectiveModel, stoppingToken) is { } exhausted)
        {
            Audit(prompt, AuditKinds.LimitRefused, "лимит тарифа");
            await SendPlainAsync(prompt.Chat, exhausted, stoppingToken);
            await DropQueueAsync(prompt.Chat, stoppingToken);
            return;
        }

        // Здесь, а не при получении сообщения: иначе карточки идущего запуска ушли бы
        // в чат другого пользователя.
        broker.ActiveChat = prompt.Chat;

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var session = store.SessionId;
        var thread = session is { Length: > 0 } ? session.ShortId : "новая сессия";

        Audit(prompt, AuditKinds.RunStart,
            $"{store.EffectiveModel ?? "модель по умолчанию"}, {store.EffectivePermissionMode}, effort {store.EffectiveEffort ?? "—"}",
            session);

        var status = await RunStatusMessage.StartAsync(channel, prompt.Chat, thread, logger, stoppingToken);

        var startedUtc = DateTimeOffset.UtcNow;
        var project = store.ProjectPath;
        var model = store.EffectiveModel;
        var preview = Text.Preview(prompt.Text);
        monitor.RunStarted(new RunStart(project, session, preview, model, store.EffectivePermissionMode, store.EffectiveEffort));

        // В state.json, а не только в памяти: если шлюз убьют посреди запуска, следующему
        // экземпляру нужно знать, кому и про что сказать «прервано».
        store.BeginRun(new ActiveRun
        {
            ChatKey = prompt.Chat.Key,
            UserKey = prompt.User.Key,
            StartedUtc = startedUtc,
            ProjectPath = project,
            SessionId = session,
            Model = model,
            Prompt = preview,
        });

        // Сессию и папку фиксируем до запуска: переключение из меню посреди работы иначе
        // разведёт каталог процесса и проект, которому запишется сессия. Id новой сессии
        // выдаём сами — так /stop или падение первого запуска не теряют ветку.
        var request = new AgentRunRequest(
            prompt.Text, project,
            ResumeSessionId: session,
            NewSessionId: Guid.NewGuid().ToString(),
            Model: model,
            Effort: store.EffectiveEffort,
            PermissionMode: PermissionMode(),
            Timeout: TimeSpan.FromMinutes(options.Value.RunTimeoutMinutes),
            // Папка чата, а не проекта: пока сообщение ждало очереди, /project мог смениться,
            // а путь к картинке в промпте уже записан.
            AttachmentsPath: inbox.ChatDirectory(prompt.Chat));

        // Тот же id нужен после запуска: активной станет только сессия, с которой он шёл,
        // иначе итог перетёр бы /new или смену сессии по ходу работы.
        var runSessionId = session is { Length: > 0 } ? session : request.NewSessionId;

        // «Занят» ставим прямо перед запуском: снимает его только finally ниже, а сбой выше
        // (отправка статусного сообщения) оставил бы шлюз занятым до перезапуска.
        _runCts = runCts;

        AgentRunResult result;
        CurrentRun? finished;
        try
        {
            result = await agent.RunAsync(request, new RunObserver(store, monitor, request, status), runCts.Token);
        }
        finally
        {
            _runCts = null;
            store.EndRun();
            finished = monitor.RunFinished();
            await status.DisposeAsync();
            // Процесс завершён — отвечать на висящие карточки уже некому.
            broker.CancelAll();
            await DeleteQuietlyAsync(status.Message, stoppingToken);
        }

        // Неудачный запуск тоже расходует тариф, поэтому пишем и его — если агент успел сказать.
        if (result.Usage is { } usage)
            store.RecordRun(project, prompt.Text, result.SessionId, usage);

        var note = SettleSession(project, session, runSessionId, result);

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
            Model = model,
            Outcome = outcome,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            Turns = result.Usage?.Turns ?? 0,
            ToolCalls = finished?.ToolCalls ?? 0,
            InputTokens = result.Usage?.InputTokens ?? 0,
            OutputTokens = result.Usage?.OutputTokens ?? 0,
        });

        var text = result.Ok ? result.Text : $"⚠️ {result.Text}";
        await SendRenderedAsync(prompt.Chat, text + note + Footer(result), stoppingToken);

        // Упёрлись в лимит — следующие задачи упрутся в него же; очередь чистим,
        // чтобы не жечь её на отказах.
        if (result.RateLimited) await DropQueueAsync(prompt.Chat, stoppingToken);
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
    private string SettleSession(string project, string? resumed, string runSessionId, AgentRunResult result)
    {
        if (result.SessionLost && resumed is { Length: > 0 })
        {
            if (!store.TrySetSessionId(project, null, onlyIfActive: resumed)) return "";

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
    /// шаги — в статусное сообщение и монитор.
    /// </summary>
    private sealed class RunObserver(
        SessionStore store, RunMonitor monitor, AgentRunRequest request, RunStatusMessage status) : IAgentRunObserver
    {
        public void SessionStarted(string sessionId) =>
            store.RegisterSession(request.ProjectPath, request.Prompt, sessionId);

        public void Activity(RunActivity activity)
        {
            status.Report(activity);
            monitor.Step(activity);
        }
    }

    private void Audit(QueuedPrompt prompt, string kind, string summary, string? session = null, string? outcome = null) =>
        audit.Write(AuditEvent.Now(kind, summary, prompt.User, prompt.Chat, store.ProjectPath, session, outcome));

    /// <summary>
    /// Подпись под ответом: id сессии, которой отвечал агент. По нему ответ находится
    /// в <c>/sessions</c> и <c>/status</c>, и по нему же сессию можно продолжить
    /// из терминала в той же папке.
    /// </summary>
    private static string Footer(AgentRunResult result)
    {
        if (result.SessionId is not { Length: > 0 } session) return "";

        var parts = new List<string> { $"🧵 `{session}`" };

        if (result.Usage is { Turns: > 0 } usage) parts.Add($"{usage.Turns} х");
        parts.Add(result.Duration.Elapsed);

        return "\n\n" + string.Join(" · ", parts);
    }

    /// <summary>
    /// Снимает очередь целиком: до сброса окна запускать нечего. Задачи не переносим,
    /// а возвращаем пользователю — за время ожидания они могли устареть.
    /// </summary>
    private async Task DropQueueAsync(ChatId chat, CancellationToken ct)
    {
        var dropped = 0;
        while (_queue.Reader.TryRead(out _)) dropped++;
        monitor.QueueCleared();

        if (dropped == 0) return;

        logger.LogWarning("Очередь снята после лимита тарифа: задач {Count}", dropped);

        await SendPlainAsync(
            chat,
            $"Очередь снята: {dropped} задач(и) не запущены — пришлите их снова после сброса лимита.",
            ct);
    }

    /// <summary>
    /// Ответ агента: markdown переводится в формат канала и режется под его лимит.
    /// Не влезающая порция уходит документом.
    /// </summary>
    private async Task SendRenderedAsync(ChatId chat, string markdown, CancellationToken ct)
    {
        foreach (var part in MarkdownRenderer.Render(markdown, channel.Limits.MessageLength))
        {
            try
            {
                await SendPartAsync(chat, part, ct);
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
    private Task SendPartAsync(ChatId chat, OutgoingPart part, CancellationToken ct) =>
        RateLimitRetry.OnceAsync(
            () => SendOnceAsync(chat, part, ct),
            wait => logger.LogWarning("Канал просит подождать {Wait}, часть ответа будет отправлена повторно", wait),
            ct);

    private async Task SendOnceAsync(ChatId chat, OutgoingPart part, CancellationToken ct)
    {
        if (part.DocumentText is { } document)
            await channel.SendFileAsync(chat, part.DocumentName ?? "fragment.txt", document, ct);
        else if (part.Html is { Length: > 0 } html)
            await channel.SendAsync(chat, new OutgoingMessage(html), ct);
    }

    private async Task SendPlainAsync(ChatId chat, string text, CancellationToken ct)
    {
        foreach (var chunk in Chunk(text, channel.Limits.MessageLength))
        {
            try
            {
                await channel.SendAsync(chat, new OutgoingMessage(chunk, Rich: false), ct);
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
