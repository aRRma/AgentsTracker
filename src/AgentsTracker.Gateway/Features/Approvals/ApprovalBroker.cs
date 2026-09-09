using System.Collections.Concurrent;

namespace AgentsTracker.Gateway.Features.Approvals;

public sealed record ChoiceOption(string Key, string Label);

/// <summary>Что нажали и кто: аудиту нужно записать, чьё это решение.</summary>
public sealed record ChoiceResult(string Key, UserId User);

/// <summary>
/// Мост между запросом агента и чатом: показывает карточку с кнопками и держит вызывающего,
/// пока не нажмут кнопку или не выйдет таймаут.
/// </summary>
public sealed class ApprovalBroker(
    IChatChannel channel,
    IOptions<GatewayOptions> options,
    ILogger<ApprovalBroker> logger)
{
    /// <summary>
    /// Ожидающая карточка. Подписи кнопок храним сами: вместе с нажатием канал клавиатуру
    /// не возвращает, а текст кнопки нужен и в карточке, и в аудите.
    /// </summary>
    private sealed record PendingChoice(
        TaskCompletionSource<ChoiceResult> Completion,
        MessageRef Message,
        string Html,
        IReadOnlyList<ChoiceOption> Buttons);

    private sealed record TextPrompt(TaskCompletionSource<string> Completion, ChatId Chat);

    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, PendingChoice> _choices = new();
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(options.Value.ApprovalTimeoutMinutes);

    private TextPrompt? _textPrompt;

    /// <summary>Чат, куда уходят карточки. Выставляет ChatWorker перед запуском.</summary>
    public ChatId? ActiveChat { get; set; }

    public bool HasPending => !_choices.IsEmpty || _textPrompt is not null;

    /// <summary>Показывает карточку и ждёт выбора: Key нажатой кнопки и того, кто нажал.</summary>
    /// <exception cref="TimeoutException">Пользователь не ответил за отведённое время.</exception>
    public async Task<ChoiceResult> AskChoiceAsync(string html, IReadOnlyList<ChoiceOption> buttons, CancellationToken ct)
    {
        var chat = ActiveChat
            ?? throw new InvalidOperationException("Нет активного чата — некому показать запрос.");

        var id = Guid.NewGuid().ToString("N")[..8];
        var keyboard = new Keyboard(
        [
            .. buttons.Chunk(2).Select(row => (IReadOnlyList<KeyboardButton>)
                [.. row.Select(b => new KeyboardButton(Text.Clip(b.Label, channel.Limits.ButtonLabelLength), $"{id}:{b.Key}"))]),
        ]);

        var message = await channel.SendAsync(chat, new OutgoingMessage(html, Keyboard: keyboard), ct);

        var completion = new TaskCompletionSource<ChoiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _choices[id] = new PendingChoice(completion, message, html, buttons);

        try
        {
            return await WaitAsync(completion.Task, ct);
        }
        catch (TimeoutException)
        {
            // Сначала закрываем ожидание и снимаем запись, потом правим карточку: иначе
            // поздний тап во время сетевого вызова нарисовал бы «Разрешить» на запросе,
            // уже отклонённом по таймауту.
            completion.TrySetCanceled();
            _choices.TryRemove(id, out _);
            await FinishCardAsync(message, html, "⌛ Время ожидания истекло");
            throw;
        }
        finally
        {
            _choices.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Файл с полным входом инструмента, не влезшим в карточку. Уходит перед карточкой:
    /// решение принимают по прочитанному целиком.
    /// </summary>
    public async Task SendAttachmentAsync(string fileName, string content, CancellationToken ct)
    {
        var chat = ActiveChat
            ?? throw new InvalidOperationException("Нет активного чата — некому показать запрос.");

        await channel.SendFileAsync(chat, fileName, content, ct);
    }

    /// <summary>
    /// Файл агента в активный чат: документом или фото. Перед каждой попыткой поток
    /// перематывается: после 429 повтор с середины отправил бы обрезанный документ.
    /// </summary>
    public Task SendFileAsync(string fileName, Stream content, string? caption, bool asPhoto, CancellationToken ct)
    {
        var chat = ActiveChat
            ?? throw new InvalidOperationException("Нет активного чата — некому отправить файл.");

        return RateLimitRetry.OnceAsync(
            () =>
            {
                content.Position = 0;
                return asPhoto
                    ? channel.SendPhotoAsync(chat, fileName, content, caption, ct)
                    : channel.SendDocumentAsync(chat, fileName, content, caption, ct);
            },
            wait => logger.LogWarning("Канал просит подождать {Wait} перед отправкой файла", wait),
            ct);
    }

    /// <summary>Просит пользователя прислать свободный текст следующим сообщением.</summary>
    public async Task<string> AskTextAsync(string html, CancellationToken ct)
    {
        var chat = ActiveChat
            ?? throw new InvalidOperationException("Нет активного чата — некому показать запрос.");

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new TextPrompt(completion, chat);
        if (Interlocked.CompareExchange(ref _textPrompt, prompt, null) is not null)
            throw new InvalidOperationException("Уже ожидается другой текстовый ответ.");

        await channel.SendAsync(chat, new OutgoingMessage(html), ct);

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
    /// Отдаёт текст ожидающему вопросу. false — никто не ждёт или ждут из другого чата,
    /// тогда сообщение уйдёт обычным промптом. Проверка чата нужна, когда разрешённых
    /// пользователей несколько: чужое сообщение не должно стать ответом агенту.
    /// </summary>
    public bool TryConsumeText(ChatId chat, string text)
    {
        var prompt = _textPrompt;
        return prompt is not null && prompt.Chat == chat && prompt.Completion.TrySetResult(text);
    }

    public async Task HandlePressAsync(ButtonPress press, CancellationToken ct)
    {
        var data = press.Data;
        var separator = data.IndexOf(':');

        if (separator <= 0 || !_choices.TryGetValue(data[..separator], out var pending))
        {
            await SafeAnswerAsync(press, "Запрос уже неактуален");
            return;
        }

        // Карточка адресована одному чату: второй разрешённый пользователь не должен
        // одобрять действие, которого не видел.
        if (press.Chat != pending.Message.Chat)
        {
            await SafeAnswerAsync(press, "Этот запрос адресован другому чату");
            return;
        }

        var key = data[(separator + 1)..];
        await SafeAnswerAsync(press, null);

        if (!pending.Completion.TrySetResult(new ChoiceResult(key, press.User))) return;

        // Режем так же, как подпись кнопки: длинный вариант увёл бы правку карточки
        // за предел сообщения, и карточка осталась бы с живыми кнопками.
        var chosen = pending.Buttons.FirstOrDefault(b => b.Key == key)?.Label ?? key;
        var label = Text.Clip(chosen, channel.Limits.ButtonLabelLength);

        await FinishCardAsync(pending.Message, pending.Html, $"➡️ {label}");
    }

    /// <summary>Снимает все ожидания — например, когда пользователь дал /stop.</summary>
    public void CancelAll()
    {
        foreach (var (id, pending) in _choices)
        {
            if (!pending.Completion.TrySetCanceled()) continue;

            _choices.TryRemove(id, out _);
            // Кнопки надо убрать, иначе на снятой карточке остаётся живой выбор. Ждать
            // нечего: FinishCardAsync не бросает и не зависит от токена вызова.
            _ = FinishCardAsync(pending.Message, pending.Html, "🛑 Отменено");
        }

        Interlocked.Exchange(ref _textPrompt, null)?.Completion.TrySetCanceled();
    }

    /// <summary>
    /// Ждёт ответа, отличая таймаут от отмены: отменённый запуск должен бросить
    /// OperationCanceledException, а не выглядеть как «не ответили вовремя».
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
            // Гасим таймер сразу: иначе он висит до конца ApprovalTimeoutMinutes.
            expiry.Cancel();
        }
    }

    /// <summary>
    /// Убирает кнопки и дописывает решение. Свой таймаут вместо токена вызова: закрыть
    /// карточку нужно и после отмены запуска.
    /// </summary>
    private async Task FinishCardAsync(MessageRef message, string html, string verdict)
    {
        using var timeout = new CancellationTokenSource(NetworkTimeout);

        try
        {
            await channel.EditAsync(
                message, new OutgoingMessage($"{html}\n\n{ChatHtml.Escape(verdict)}"), timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось обновить карточку подтверждения");
        }
    }

    private async Task SafeAnswerAsync(ButtonPress press, string? toast)
    {
        using var timeout = new CancellationTokenSource(NetworkTimeout);

        try
        {
            await channel.AcknowledgeAsync(press, toast, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось ответить на нажатие");
        }
    }
}
