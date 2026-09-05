using System.Collections.Concurrent;
using System.Text;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentsTracker.Gateway.Features.Approvals;

public sealed record ChoiceOption(string Key, string Label);

/// <summary>Что нажали и кто: UserId нужен аудиту, чтобы записать, чьё это решение.</summary>
public sealed record ChoiceResult(string Key, long UserId);

/// <summary>
/// Мост между MCP-инструментом подтверждений и чатом: показывает карточку с кнопками
/// и блокирует вызывающий поток, пока пользователь не нажмёт кнопку (или не выйдет таймаут).
/// </summary>
public sealed class ApprovalBroker(
    ITelegramBotClient bot,
    IOptions<GatewayOptions> options,
    ILogger<ApprovalBroker> logger)
{
    private sealed record PendingChoice(
        TaskCompletionSource<ChoiceResult> Completion,
        long ChatId,
        int MessageId,
        string Html);

    private sealed record TextPrompt(TaskCompletionSource<string> Completion, long ChatId);

    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, PendingChoice> _choices = new();
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(options.Value.ApprovalTimeoutMinutes);

    private TextPrompt? _textPrompt;

    /// <summary>Чат, в который уходят карточки. Выставляет ChatWorker перед самым запуском.</summary>
    public long? ActiveChatId { get; set; }

    public bool HasPending => !_choices.IsEmpty || _textPrompt is not null;

    /// <summary>
    /// Показывает карточку с кнопками и ждёт выбора. Возвращает Key выбранной кнопки и того, кто нажал.
    /// </summary>
    /// <exception cref="TimeoutException">Пользователь не ответил за отведённое время.</exception>
    public async Task<ChoiceResult> AskChoiceAsync(string html, IReadOnlyList<ChoiceOption> buttons, CancellationToken ct)
    {
        var chatId = ActiveChatId
            ?? throw new InvalidOperationException("Нет активного чата — некому показать запрос.");

        var id = Guid.NewGuid().ToString("N")[..8];
        var keyboard = new InlineKeyboardMarkup(
            buttons.Chunk(2).Select(row => row.Select(b =>
                InlineKeyboardButton.WithCallbackData(b.Label, $"{id}:{b.Key}"))));

        var message = await bot.SendMessage(chatId, html, ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);

        var completion = new TaskCompletionSource<ChoiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _choices[id] = new PendingChoice(completion, chatId, message.MessageId, html);

        try
        {
            return await WaitAsync(completion.Task, ct);
        }
        catch (TimeoutException)
        {
            // Сначала закрываем ожидание и снимаем запись, потом правим карточку: пока идёт
            // сетевой вызов, поздний тап иначе прошёл бы через TrySetResult и нарисовал
            // «Разрешить» на запросе, который уже отклонён по таймауту.
            completion.TrySetCanceled();
            _choices.TryRemove(id, out _);
            await FinishCardAsync(chatId, message.MessageId, html, "⌛ Время ожидания истекло");
            throw;
        }
        finally
        {
            _choices.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Отправляет в активный чат текст файлом — полный вход инструмента, который в карточку
    /// не влез. Идёт перед карточкой: решение принимается по тому, что прочитано целиком.
    /// </summary>
    public async Task SendAttachmentAsync(string fileName, string content, CancellationToken ct)
    {
        var chatId = ActiveChatId
            ?? throw new InvalidOperationException("Нет активного чата — некому показать запрос.");

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await bot.SendDocument(chatId, InputFile.FromStream(stream, fileName), cancellationToken: ct);
    }

    /// <summary>Просит пользователя прислать свободный текст следующим сообщением.</summary>
    public async Task<string> AskTextAsync(string html, CancellationToken ct)
    {
        var chatId = ActiveChatId
            ?? throw new InvalidOperationException("Нет активного чата — некому показать запрос.");

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new TextPrompt(completion, chatId);
        if (Interlocked.CompareExchange(ref _textPrompt, prompt, null) is not null)
            throw new InvalidOperationException("Уже ожидается другой текстовый ответ.");

        await bot.SendMessage(chatId, html, ParseMode.Html, cancellationToken: ct);

        try
        {
            return await WaitAsync(completion.Task, ct);
        }
        finally
        {
            Interlocked.CompareExchange(ref _textPrompt, null, prompt);
        }
    }

    /// <summary>
    /// Отдаёт текст ожидающему запросу свободного ответа. Возвращает false, если никто не ждёт
    /// или ждут ответа из другого чата — тогда сообщение обрабатывается как обычный промпт.
    /// Проверка чата нужна при нескольких AllowedUserIds: чужое сообщение не должно
    /// становиться ответом на вопрос агента.
    /// </summary>
    public bool TryConsumeText(long chatId, string text)
    {
        var prompt = _textPrompt;
        return prompt is not null && prompt.ChatId == chatId && prompt.Completion.TrySetResult(text);
    }

    public async Task HandleCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        var data = query.Data ?? "";
        var separator = data.IndexOf(':');

        if (separator <= 0 || !_choices.TryGetValue(data[..separator], out var pending))
        {
            await SafeAnswerAsync(query.Id, "Запрос уже неактуален");
            return;
        }

        // Карточка адресована одному чату: нажатие из другого (второй разрешённый
        // пользователь) не должно одобрять действие, которого он не видел.
        if (query.Message?.Chat.Id != pending.ChatId)
        {
            await SafeAnswerAsync(query.Id, "Этот запрос адресован другому чату");
            return;
        }

        var key = data[(separator + 1)..];
        await SafeAnswerAsync(query.Id, null);

        if (!pending.Completion.TrySetResult(new ChoiceResult(key, query.From.Id))) return;

        var label = query.Message?.ReplyMarkup?.InlineKeyboard
            .SelectMany(row => row)
            .FirstOrDefault(b => b.CallbackData == data)?.Text ?? key;

        await FinishCardAsync(pending.ChatId, pending.MessageId, pending.Html, $"➡️ {label}");
    }

    /// <summary>Снимает все ожидания — например, когда пользователь дал /stop.</summary>
    public void CancelAll()
    {
        foreach (var (id, pending) in _choices)
        {
            if (!pending.Completion.TrySetCanceled()) continue;

            _choices.TryRemove(id, out _);
            // Кнопки надо убрать: иначе на снятой карточке остаётся живой выбор.
            // Ждать нечего — FinishCardAsync не бросает и не зависит от токена вызова.
            _ = FinishCardAsync(pending.ChatId, pending.MessageId, pending.Html, "🛑 Отменено");
        }

        Interlocked.Exchange(ref _textPrompt, null)?.Completion.TrySetCanceled();
    }

    /// <summary>
    /// Ждёт ответа, отличая таймаут от отмены: отменённый запуск должен бросать
    /// OperationCanceledException, а не выглядеть как «пользователь не ответил вовремя».
    /// </summary>
    private async Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct)
    {
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct);
        expiry.CancelAfter(_timeout);

        try
        {
            var completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, expiry.Token));
            if (completed == task) return await task;

            ct.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }
        finally
        {
            // Гасит таймер сразу после ответа — иначе он живёт до конца ApprovalTimeoutMinutes.
            expiry.Cancel();
        }
    }

    /// <summary>
    /// Убирает кнопки и дописывает к карточке принятое решение. Собственный таймаут вместо
    /// токена вызова: карточку нужно закрыть и тогда, когда запуск уже отменён.
    /// </summary>
    private async Task FinishCardAsync(long chatId, int messageId, string html, string verdict)
    {
        using var timeout = new CancellationTokenSource(NetworkTimeout);

        try
        {
            await bot.EditMessageText(
                chatId, messageId, $"{html}\n\n{TelegramFormatter.Escape(verdict)}",
                ParseMode.Html, replyMarkup: null, cancellationToken: timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось обновить карточку подтверждения");
        }
    }

    private async Task SafeAnswerAsync(string callbackQueryId, string? text)
    {
        using var timeout = new CancellationTokenSource(NetworkTimeout);

        try
        {
            await bot.AnswerCallbackQuery(callbackQueryId, text, cancellationToken: timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось ответить на callback");
        }
    }
}
