using System.Collections.Concurrent;

namespace AgentsTracker.Gateway.Features.Approvals;

public sealed record ChoiceOption(string Key, string Label);

/// <summary>Что нажали и кто: аудиту нужно записать, чьё это решение.</summary>
public sealed record ChoiceResult(string Key, UserId User);

/// <summary>
/// Запуск глазами карточек: куда их показывать и что писать в журнал. Задача и вопрос идут
/// параллельно, и одно «текущее» значение на шлюз отправило бы карточку одного в чат другого.
/// </summary>
/// <param name="ReplyTo">Сообщение, ответом на которое идут карточки и файлы; у вопроса — сам вопрос.</param>
/// <param name="ProjectPath">
/// Проект для путей на карточке и для проверки <c>send_file</c>. У вопроса — проект чата, а не
/// его пустая рабочая папка: права вопроса остаются теми же, что были в общей очереди.
/// </param>
/// <param name="SessionId">Сессия для журнала; у вопроса null.</param>
public sealed record RunScope(string RunId, ChatId Chat, MessageRef? ReplyTo, string ProjectPath, string? SessionId);

/// <summary>Что стало с текстом из чата, пока кто-то ждёт свободного ответа.</summary>
public enum TextDelivery
{
    /// <summary>В этом чате никто не ждёт — текст идёт дальше обычным путём.</summary>
    NotAwaited,

    /// <summary>Текст отдан ожиданию.</summary>
    Delivered,

    /// <summary>Ждут несколько, а текст не ответ (reply) ни на одну просьбу — кому он, неизвестно.</summary>
    Ambiguous,
}

/// <summary>
/// Мост между агентом и чатом: показывает карточку с кнопками и держит вызывающего,
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
        IReadOnlyList<ChoiceOption> Buttons,
        string RunId);

    /// <param name="Message">Сообщение-просьба: ответ (reply) на него адресует текст именно сюда.</param>
    private sealed record TextPrompt(TaskCompletionSource<string> Completion, ChatId Chat, string RunId, MessageRef Message);

    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, PendingChoice> _choices = new();
    private readonly ConcurrentDictionary<string, RunScope> _runs = new(StringComparer.Ordinal);

    /// <summary>
    /// Ожидания свободного текста по id. Не одно на шлюз: задача и вопрос идут параллельно, а у
    /// задачи ещё и сабагенты — второе ожидание иначе получало бы отказ, а текст уходил не тому.
    /// </summary>
    private readonly ConcurrentDictionary<string, TextPrompt> _textPrompts = new(StringComparer.Ordinal);

    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(options.Value.ApprovalTimeoutMinutes);

    public bool HasPending => !_choices.IsEmpty || !_textPrompts.IsEmpty;

    /// <summary>
    /// Регистрирует запуск перед стартом процесса. Dispose снимает его вместе с его
    /// карточками — процесс завершён, отвечать на них некому, — но не трогает карточки
    /// другого запуска.
    /// </summary>
    public IDisposable BeginRun(RunScope run)
    {
        _runs[run.RunId] = run;
        return new Registration(() =>
        {
            _runs.TryRemove(run.RunId, out _);
            Cancel(run.RunId);
        });
    }

    /// <summary>Запуск по id; null — он уже завершён или id никому не выдавался.</summary>
    public RunScope? Run(string runId) =>
        runId.Length > 0 && _runs.TryGetValue(runId, out var run) ? run : null;

    /// <summary>Показывает карточку и ждёт выбора: Key нажатой кнопки и того, кто нажал.</summary>
    /// <exception cref="TimeoutException">Пользователь не ответил за отведённое время.</exception>
    public async Task<ChoiceResult> AskChoiceAsync(
        RunScope run, string html, IReadOnlyList<ChoiceOption> buttons, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var keyboard = new Keyboard(
        [
            .. buttons.Chunk(2).Select(row => (IReadOnlyList<KeyboardButton>)
                [.. row.Select(b => new KeyboardButton(Text.Clip(b.Label, channel.Limits.ButtonLabelLength), $"{id}:{b.Key}"))]),
        ]);

        var message = await channel.SendAsync(run.Chat, new OutgoingMessage(html, Keyboard: keyboard, ReplyTo: run.ReplyTo), ct);

        var completion = new TaskCompletionSource<ChoiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _choices[id] = new PendingChoice(completion, message, html, buttons, run.RunId);

        // Запуск мог закончиться, пока карточка уходила в канал: его снятие её ещё не видело,
        // и без этой проверки она висела бы с живыми кнопками до таймаута.
        if (Run(run.RunId) is null) Cancel(run.RunId);

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
    public async Task SendAttachmentAsync(RunScope run, string fileName, string content, CancellationToken ct) =>
        await channel.SendFileAsync(run.Chat, fileName, content, run.ReplyTo, ct);

    /// <summary>
    /// Файл агента в чат запуска: документом или фото. Перед каждой попыткой поток
    /// перематывается: после 429 повтор с середины отправил бы обрезанный документ.
    /// </summary>
    public Task SendFileAsync(
        RunScope run, string fileName, Stream content, string? caption, bool asPhoto, CancellationToken ct) =>
        RateLimitRetry.OnceAsync(
            () =>
            {
                content.Position = 0;
                return asPhoto
                    ? channel.SendPhotoAsync(run.Chat, fileName, content, caption, run.ReplyTo, ct)
                    : channel.SendDocumentAsync(run.Chat, fileName, content, caption, run.ReplyTo, ct);
            },
            wait => logger.LogWarning("Канал просит подождать {Wait} перед отправкой файла", wait),
            ct);

    /// <summary>
    /// Просит пользователя прислать свободный текст следующим сообщением. Ожиданий может быть
    /// несколько сразу (задача и вопрос, параллельные сабагенты): какое из них получит текст,
    /// решает <see cref="DeliverText"/>.
    /// </summary>
    public async Task<string> AskTextAsync(RunScope run, string html, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var message = await channel.SendAsync(run.Chat, new OutgoingMessage(html, ReplyTo: run.ReplyTo), ct);
            _textPrompts[id] = new TextPrompt(completion, run.Chat, run.RunId, message);

            // Та же гонка, что у карточки: запуск закончился, пока уходила просьба.
            if (Run(run.RunId) is null) Cancel(run.RunId);

            return await WaitAsync(completion.Task, ct);
        }
        finally
        {
            _textPrompts.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Отдаёт текст из чата одному из ожиданий. Ответ (reply) на сообщение-просьбу адресует его
    /// точно; без ответа текст берёт единственное ожидание в этом чате. Несколько ожиданий и
    /// никакого ответа — <see cref="TextDelivery.Ambiguous"/>: угадывать нельзя, причина отказа
    /// ушла бы не той карточке. Чат сверяется, когда разрешённых пользователей несколько:
    /// чужое сообщение не должно стать ответом агенту.
    /// </summary>
    public TextDelivery DeliverText(ChatId chat, MessageRef? inReplyTo, string text)
    {
        var waiting = _textPrompts.Values.Where(p => p.Chat == chat).ToList();
        if (waiting.Count == 0) return TextDelivery.NotAwaited;

        var target = waiting.FirstOrDefault(p => inReplyTo is not null && p.Message == inReplyTo)
            ?? (waiting.Count == 1 ? waiting[0] : null);

        if (target is null) return TextDelivery.Ambiguous;

        // Ожидание могли снять между выбором и записью — тогда текст уйдёт дальше обычным путём.
        return target.Completion.TrySetResult(text) ? TextDelivery.Delivered : TextDelivery.NotAwaited;
    }

    /// <summary>
    /// Ждёт ли что-то свободного ответа из этого чата. Нужно тому, кто разбирает сообщение
    /// до <see cref="DeliverText"/>: картинку в ответ агенту не передать, но и пропускать
    /// её дальше нельзя — карточка осталась бы висеть, а запуск стоять.
    /// </summary>
    public bool IsAwaitingText(ChatId chat) => _textPrompts.Values.Any(p => p.Chat == chat);

    public async Task HandlePressAsync(ButtonPress press, CancellationToken ct)
    {
        var data = press.Data;
        var separator = data.IndexOf(':');

        if (separator <= 0 || !_choices.TryGetValue(data[..separator], out var pending))
        {
            await SafeAnswerAsync(press, "Карточка уже неактуальна");
            return;
        }

        // Карточка адресована одному чату: второй разрешённый пользователь не должен
        // одобрять действие, которого не видел.
        if (press.Chat != pending.Message.Chat)
        {
            await SafeAnswerAsync(press, "Эта карточка адресована другому чату");
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

    /// <summary>Снимает все ожидания всех запусков — когда пользователь дал /stop.</summary>
    public void CancelAll() => Cancel(null);

    /// <summary>Снимает ожидания одного запуска; null — всех.</summary>
    private void Cancel(string? runId)
    {
        foreach (var (id, pending) in _choices)
        {
            if (runId is not null && pending.RunId != runId) continue;
            if (!pending.Completion.TrySetCanceled()) continue;

            _choices.TryRemove(id, out _);
            // Кнопки надо убрать, иначе на снятой карточке остаётся живой выбор. Ждать
            // нечего: FinishCardAsync не бросает и не зависит от токена вызова.
            _ = FinishCardAsync(pending.Message, pending.Html, "🛑 Отменено");
        }

        foreach (var (id, prompt) in _textPrompts)
        {
            if (runId is not null && prompt.RunId != runId) continue;
            if (_textPrompts.TryRemove(id, out _)) prompt.Completion.TrySetCanceled();
        }
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

    /// <summary>Снятие запуска ровно один раз, сколько бы раз ни позвали Dispose.</summary>
    private sealed class Registration(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
