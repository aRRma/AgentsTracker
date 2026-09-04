using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AgentsTracker.Gateway.Approvals;
using AgentsTracker.Gateway.Claude;
using AgentsTracker.Gateway.State;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentsTracker.Gateway.Telegram;

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
    ILogger<ChatWorker> logger) : BackgroundService
{
    // SingleReader здесь ставить нельзя: канал становится SingleConsumerUnboundedChannel,
    // у него Reader.CanCount == false, и QueueLength для /status падает с NotSupportedException.
    // Читатель и так один — ExecuteAsync; выигрыш от оптимизации на десятке сообщений нулевой.
    private readonly Channel<QueuedPrompt> _queue = Channel.CreateUnbounded<QueuedPrompt>();

    private CancellationTokenSource? _runCts;

    private sealed record QueuedPrompt(long ChatId, string Text);

    public bool IsBusy => _runCts is not null;

    public int QueueLength => _queue.Reader.Count;

    public void Enqueue(long chatId, string text) => _queue.Writer.TryWrite(new QueuedPrompt(chatId, text));

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
        // Лимиты проверяем здесь, а не при постановке в очередь: пока сообщение ждало,
        // предыдущие запуски могли выбрать и бюджет, и тарифное окно.
        if (OverBudget() is { } refusal)
        {
            await SendPlainAsync(prompt.ChatId, refusal, stoppingToken);
            return;
        }

        // Тариф кончился — запускать нечего: CLI продолжил бы за кредиты, а это запрещено.
        if (await limits.RefusalAsync(store.EffectiveModel, stoppingToken) is { } exhausted)
        {
            await SendPlainAsync(prompt.ChatId, exhausted, stoppingToken);
            await DropQueueAsync(prompt.ChatId, stoppingToken);
            return;
        }

        broker.ActiveChatId = prompt.ChatId;

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _runCts = runCts;

        // Сразу говорим, в какой ветке пойдёт работа: продолжаем известную сессию или начинаем новую.
        var thread = store.SessionId is { Length: > 0 } active ? Short(active) : "новая сессия";

        var status = await bot.SendMessage(
            prompt.ChatId, $"⏳ Работаю… 🧵 {thread}", cancellationToken: stoppingToken);
        using var typing = new TypingIndicator(bot, prompt.ChatId, logger);

        ClaudeRunResult result;
        try
        {
            result = await runner.RunAsync(prompt.Text, runCts.Token);
        }
        finally
        {
            _runCts = null;
            typing.Dispose();
            // Процесс завершён — отвечать на висящие карточки уже некому.
            broker.CancelAll();
            await DeleteQuietlyAsync(prompt.ChatId, status.MessageId, stoppingToken);
        }

        var text = result.Ok ? result.Text : $"⚠️ {result.Text}";
        await SendRenderedAsync(prompt.ChatId, text + Footer(result), stoppingToken);

        // Запуск упёрся в лимит тарифа: следующие задачи упрутся в тот же лимит, а платить
        // за них кредитами шлюзу запрещено — очередь чистим, чтобы не жечь её на отказах.
        if (result.RateLimited) await DropQueueAsync(prompt.ChatId, stoppingToken);
    }

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
        parts.Add(Elapsed(result.Duration));

        return "\n\n" + string.Join(" · ", parts);
    }

    /// <summary>Первые восемь символов id: достаточно, чтобы узнать ветку в статусной строке.</summary>
    private static string Short(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];

    private static string Elapsed(TimeSpan span)
    {
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} ч {span.Minutes} мин";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} мин {span.Seconds} с";
        return $"{(int)span.TotalSeconds} с";
    }

    /// <summary>
    /// Снимает очередь целиком: пока тарифное окно не сбросится, запускать нечего.
    /// Задачи не переносим, а возвращаем пользователю — за время ожидания они могли устареть.
    /// </summary>
    private async Task DropQueueAsync(long chatId, CancellationToken ct)
    {
        var dropped = 0;
        while (_queue.Reader.TryRead(out _)) dropped++;

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

        return $"💳 Дневной бюджет исчерпан: потрачено ${spent.ToString("0.00", CultureInfo.InvariantCulture)} " +
               $"из ${budget.ToString("0.00", CultureInfo.InvariantCulture)}. " +
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

    /// <summary>Держит в чате индикатор «печатает», пока агент работает.</summary>
    private sealed class TypingIndicator : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public TypingIndicator(ITelegramBotClient bot, long chatId, ILogger logger)
        {
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: _cts.Token);
                        await Task.Delay(TimeSpan.FromSeconds(4), _cts.Token);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Индикатор набора текста прерван");
                        return;
                    }
                }
            });
        }

        public void Dispose()
        {
            if (!_cts.IsCancellationRequested) _cts.Cancel();
        }
    }
}
