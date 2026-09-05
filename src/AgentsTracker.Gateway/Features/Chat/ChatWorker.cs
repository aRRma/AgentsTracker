using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AgentsTracker.Gateway.Features.Approvals;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Claude;
using AgentsTracker.Gateway.Infrastructure.Monitoring;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>
/// Обрабатывает сообщения строго по одному: пока идёт запуск claude, новые сообщения
/// копятся в очереди, а не запускают второй процесс.
/// </summary>
public sealed partial class ChatWorker(
    ITelegramBotClient bot,
    ClaudeRunner runner,
    ClaudeLimits limits,
    ApprovalBroker broker,
    SessionStore store,
    IAuditLog audit,
    RunMonitor monitor,
    ILogger<ChatWorker> logger) : BackgroundService
{
    // SingleReader здесь ставить нельзя: канал становится SingleConsumerUnboundedChannel,
    // у него Reader.CanCount == false, и QueueLength для /status падает с NotSupportedException.
    // Читатель и так один — ExecuteAsync; выигрыш от оптимизации на десятке сообщений нулевой.
    private readonly Channel<QueuedPrompt> _queue = Channel.CreateUnbounded<QueuedPrompt>();

    private CancellationTokenSource? _runCts;

    private sealed record QueuedPrompt(long ChatId, long UserId, string Text);

    public bool IsBusy => _runCts is not null;

    public int QueueLength => _queue.Reader.Count;

    public void Enqueue(long chatId, long userId, string text)
    {
        _queue.Writer.TryWrite(new QueuedPrompt(chatId, userId, text));
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
                await SendPlainAsync(prompt.ChatId, $"Внутренняя ошибка шлюза: {ex.Message}", stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(QueuedPrompt prompt, CancellationToken stoppingToken)
    {
        monitor.Dequeued();

        // Лимиты проверяем здесь, а не при постановке в очередь: пока сообщение ждало,
        // предыдущие запуски могли выбрать и бюджет, и тарифное окно.
        if (OverBudget() is { } refusal)
        {
            Audit(prompt, AuditKinds.BudgetRefused, "дневной бюджет");
            await SendPlainAsync(prompt.ChatId, refusal, stoppingToken);
            return;
        }

        // Тариф кончился — запускать нечего: CLI продолжил бы за кредиты, а это запрещено.
        if (await limits.RefusalAsync(store.EffectiveModel, stoppingToken) is { } exhausted)
        {
            Audit(prompt, AuditKinds.BudgetRefused, "лимит тарифа");
            await SendPlainAsync(prompt.ChatId, exhausted, stoppingToken);
            await DropQueueAsync(prompt.ChatId, stoppingToken);
            return;
        }

        // Выставляем здесь, а не при получении сообщения: иначе карточки уже идущего запуска
        // ушли бы в чат другого пользователя.
        broker.ActiveChatId = prompt.ChatId;

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _runCts = runCts;

        // Сразу говорим, в какой ветке пойдёт работа: продолжаем известную сессию или начинаем новую.
        var session = store.SessionId;
        var thread = session is { Length: > 0 } ? session.ShortId : "новая сессия";

        Audit(prompt, AuditKinds.RunStart,
            $"{store.EffectiveModel ?? "модель по умолчанию"}, {store.EffectivePermissionMode}, effort {store.EffectiveEffort ?? "—"}",
            session);

        var status = await RunStatusMessage.StartAsync(bot, prompt.ChatId, thread, logger, stoppingToken);

        var startedUtc = DateTimeOffset.UtcNow;
        var project = store.ProjectPath;
        var model = store.EffectiveModel;
        var preview = Text.Preview(prompt.Text);
        monitor.RunStarted(new RunStart(project, session, preview, model, store.EffectivePermissionMode, store.EffectiveEffort));

        ClaudeRunResult result;
        CurrentRun? finished;
        try
        {
            result = await runner.RunAsync(
                prompt.Text,
                activity => { status.Report(activity); monitor.Step(activity); },
                runCts.Token);
        }
        finally
        {
            _runCts = null;
            finished = monitor.RunFinished();
            await status.DisposeAsync();
            // Процесс завершён — отвечать на висящие карточки уже некому.
            broker.CancelAll();
            await DeleteQuietlyAsync(prompt.ChatId, status.MessageId, stoppingToken);
        }

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
            CostUsd = result.Usage?.CostUsd ?? result.CostUsd ?? 0m,
            InputTokens = result.Usage?.InputTokens ?? 0,
            OutputTokens = result.Usage?.OutputTokens ?? 0,
        });

        var text = result.Ok ? result.Text : $"⚠️ {result.Text}";
        await SendRenderedAsync(prompt.ChatId, text + Footer(result), stoppingToken);

        // Запуск упёрся в лимит тарифа: следующие задачи упрутся в тот же лимит, а платить
        // за них кредитами шлюзу запрещено — очередь чистим, чтобы не жечь её на отказах.
        if (result.RateLimited) await DropQueueAsync(prompt.ChatId, stoppingToken);
    }

    private void Audit(QueuedPrompt prompt, string kind, string summary, string? session = null, string? outcome = null) =>
        audit.Write(AuditEvent.Now(kind, summary, prompt.UserId, prompt.ChatId, store.ProjectPath, session, outcome));

    /// <summary>
    /// Подпись под ответом: id сессии, которой отвечал агент. По нему ответ узнаётся в
    /// <c>/sessions</c> и <c>/status</c>, а из терминала сессия продолжается
    /// как <c>claude --resume &lt;id&gt;</c> в той же папке.
    /// </summary>
    private static string Footer(ClaudeRunResult result)
    {
        if (result.SessionId is not { Length: > 0 } session) return "";

        var parts = new List<string> { $"🧵 `{session}`" };

        if (result.Usage is { Turns: > 0 } usage) parts.Add($"{usage.Turns} х");
        parts.Add(result.Duration.Elapsed);

        return "\n\n" + string.Join(" · ", parts);
    }

    /// <summary>
    /// Снимает очередь целиком: пока тарифное окно не сбросится, запускать нечего.
    /// Задачи не переносим, а возвращаем пользователю — за время ожидания они могли устареть.
    /// </summary>
    private async Task DropQueueAsync(long chatId, CancellationToken ct)
    {
        var dropped = 0;
        while (_queue.Reader.TryRead(out _)) dropped++;
        monitor.QueueCleared();

        if (dropped == 0) return;

        logger.LogWarning("Очередь снята после лимита тарифа: задач {Count}", dropped);

        await SendPlainAsync(
            chatId,
            $"Очередь снята: {dropped} задач(и) не запущены — пришлите их снова после сброса лимита.",
            ct);
    }

    /// <summary>Текст отказа, если дневной бюджет из конфига исчерпан, иначе null.</summary>
    private string? OverBudget()
    {
        if (store.DailyBudgetUsd is not { } budget) return null;

        var spent = store.SpentToday();
        if (spent < budget) return null;

        // Единственное место, где суммы ещё уместны: этот предохранитель включают вручную,
        // и без цифр непонятно, во что упёрлись.
        return $"💳 Дневной бюджет исчерпан: потрачено {spent.Money} из {budget.Money}. " +
               "Изменить — ключ Gateway:DailyBudgetUsd в appsettings.Local.json.";
    }

    private async Task SendRenderedAsync(long chatId, string markdown, CancellationToken ct)
    {
        foreach (var part in TelegramFormatter.Render(markdown))
        {
            if (part.DocumentText is { } document)
            {
                await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));
                await bot.SendDocument(
                    chatId, InputFile.FromStream(stream, part.DocumentName ?? "fragment.txt"),
                    cancellationToken: ct);
                continue;
            }

            if (part.Html is not { Length: > 0 } html) continue;

            try
            {
                await bot.SendMessage(chatId, html, ParseMode.Html, cancellationToken: ct);
            }
            catch (ApiRequestException ex)
            {
                // Разметка могла не пережить конвертацию — лучше отправить как есть, чем ничего.
                logger.LogWarning(ex, "Telegram отверг HTML, отправляю без разметки");
                await SendPlainAsync(chatId, StripTags(html), ct);
            }
        }
    }

    private async Task SendPlainAsync(long chatId, string text, CancellationToken ct)
    {
        foreach (var chunk in Chunk(text, TelegramFormatter.MaxMessageLength))
        {
            try
            {
                await bot.SendMessage(chatId, chunk, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Не удалось отправить сообщение в чат {ChatId}", chatId);
                return;
            }
        }
    }

    private async Task DeleteQuietlyAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            await bot.DeleteMessage(chatId, messageId, ct);
        }
        catch (ApiRequestException ex)
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

    /// <summary>
    /// Снимает всю разметку, а не перечисленные вручную теги: иначе &lt;a href=…&gt; и
    /// &lt;code class="language-x"&gt; уезжают пользователю как есть. Сущности разворачиваем
    /// после удаления тегов, иначе экранированный текст сам стал бы разметкой.
    /// </summary>
    private static string StripTags(string html) => TagRegex().Replace(html, "")
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagRegex();
}
