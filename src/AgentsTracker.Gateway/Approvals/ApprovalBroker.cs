using System.Collections.Concurrent;
using AgentsTracker.Gateway.Configuration;
using AgentsTracker.Gateway.Telegram;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentsTracker.Gateway.Approvals;

public sealed record ChoiceOption(string Key, string Label);

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
        TaskCompletionSource<string> Completion,
        long ChatId,
        int MessageId,
        string Html);

    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, PendingChoice> _choices = new();
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(options.Value.ApprovalTimeoutMinutes);

    private TaskCompletionSource<string>? _textPrompt;

    /// <summary>Чат, в который уходят карточки. Выставляется при обработке сообщения пользователя.</summary>
    public long? ActiveChatId { get; set; }

    public bool HasPending => !_choices.IsEmpty || _textPrompt is not null;

    /// <summary>
    /// Показывает карточку с кнопками и ждёт выбора. Возвращает Key выбранной кнопки.
    /// </summary>
    /// <exception cref="TimeoutException">Пользователь не ответил за отведённое время.</exception>
    public async Task<string> AskChoiceAsync(string html, IReadOnlyList<ChoiceOption> buttons, CancellationToken ct)
    {
        var chatId = ActiveChatId
            ?? throw new InvalidOperationException("Нет активного чата — некому показать запрос.");

        var id = Guid.NewGuid().ToString("N")[..8];
        var keyboard = new InlineKeyboardMarkup(
            buttons.Chunk(2).Select(row => row.Select(b =>
                InlineKeyboardButton.WithCallbackData(b.Label, $"{id}:{b.Key}"))));

        var message = await bot.SendMessage(chatId, html, ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _choices[id] = new PendingChoice(completion, chatId, message.MessageId, html);

        try
        {
            return await WaitAsync(completion.Task, ct);
        }
        catch (TimeoutException)
        {
            await FinishCardAsync(chatId, message.MessageId, html, "⌛ Время ожидания истекло");
            throw;
        }
        finally
        {
            _choices.TryRemove(id, out _);
        }
    }

    /// <summary>Просит пользователя прислать свободный текст следующим сообщением.</summary>
    public async Task<string> AskTextAsync(string html, CancellationToken ct)
    {
        var chatId = ActiveChatId
            ?? throw new InvalidOperationException("Нет активного чата — некому показать запрос.");

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _textPrompt, completion, null) is not null)
            throw new InvalidOperationException("Уже ожидается другой текстовый ответ.");

        await bot.SendMessage(chatId, html, ParseMode.Html, cancellationToken: ct);

        try
        {
            return await WaitAsync(completion.Task, ct);
        }
        finally
        {
            Interlocked.CompareExchange(ref _textPrompt, null, completion);
        }
    }

    /// <summary>
    /// Отдаёт текст ожидающему запросу свободного ответа. Возвращает false, если никто не ждёт —
    /// тогда сообщение обрабатывается как обычный промпт.
    /// </summary>
    public bool TryConsumeText(string text)
    {
        var prompt = _textPrompt;
        return prompt is not null && prompt.TrySetResult(text);
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

        var key = data[(separator + 1)..];
        await SafeAnswerAsync(query.Id, null);

        if (!pending.Completion.TrySetResult(key)) return;

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

        Interlocked.Exchange(ref _textPrompt, null)?.TrySetCanceled();
    }

    /// <summary>
    /// Ждёт ответа, отличая таймаут от отмены: отменённый запуск должен бросать
    /// OperationCanceledException, а не выглядеть как «пользователь не ответил вовремя».
    /// </summary>
    private async Task<string> WaitAsync(Task<string> task, CancellationToken ct)
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
