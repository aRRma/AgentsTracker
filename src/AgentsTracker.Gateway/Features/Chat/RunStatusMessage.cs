using System.Diagnostics;
using AgentsTracker.Gateway.Infrastructure.Chat;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>
/// Статусное сообщение идущего запуска: раз в несколько секунд правится — крутятся часы,
/// растёт время, под ними последние шаги агента. Без него долгий запуск не отличить
/// от зависшего шлюза, и /stop летит зря. Заодно держит индикатор «печатает».
/// </summary>
internal sealed class RunStatusMessage : IAsyncDisposable
{
    // Четыре секунды, а не пять: индикатор «печатает» гаснет через пять и на ровно пяти мигал бы.
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(4);
    private static readonly string[] Clock = ["🕐", "🕑", "🕒", "🕓", "🕔", "🕕", "🕖", "🕗", "🕘", "🕙", "🕚", "🕛"];
    private const int RecentSteps = 3;

    private readonly IChatChannel _channel;
    private readonly ChatId _chat;
    private readonly string _thread;
    private readonly ILogger _logger;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private readonly Queue<string> _recent = new();

    private MessageRef _message = null!;
    private int _toolCalls;
    private string _lastRendered = "";
    private Task _loop = Task.CompletedTask;

    private RunStatusMessage(IChatChannel channel, ChatId chat, string thread, ILogger logger)
    {
        _channel = channel;
        _chat = chat;
        _thread = thread;
        _logger = logger;
    }

    /// <summary>Отправленное сообщение: по нему ChatWorker удаляет статус после запуска.</summary>
    public MessageRef Message => _message;

    public static async Task<RunStatusMessage> StartAsync(
        IChatChannel channel, ChatId chat, string thread, ILogger logger, CancellationToken ct)
    {
        var status = new RunStatusMessage(channel, chat, thread, logger);
        var text = status.Render();
        status._message = await channel.SendAsync(chat, new OutgoingMessage(text, Rich: false), ct);
        status._lastRendered = text;
        status._loop = Task.Run(status.LoopAsync);
        return status;
    }

    /// <summary>Зовётся из потока чтения stdout — только запоминает, в сеть не ходит.</summary>
    public void Report(RunActivity activity)
    {
        lock (_lock)
        {
            _toolCalls++;
            _recent.Enqueue((activity.Nested ? "  ↳ " : "▸ ") + activity.Description);
            while (_recent.Count > RecentSteps) _recent.Dequeue();
        }
    }

    private string Render()
    {
        var elapsed = _elapsed.Elapsed;
        var frame = Clock[(int)(elapsed.TotalSeconds / Tick.TotalSeconds) % Clock.Length];
        var lines = new List<string> { $"{frame} Работаю… {elapsed.Elapsed} · 🧵 {_thread}" };

        lock (_lock)
        {
            if (_toolCalls > 0)
            {
                lines.Add($"🔧 {_toolCalls.Count("вызов", "вызова", "вызовов")}");
                lines.AddRange(_recent);
            }
        }

        return string.Join('\n', lines);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, _cts.Token);
            }
            catch (OperationCanceledException) { return; }

            // Порознь: сбой индикатора не должен задерживать текст статуса.
            await TryAsync(() => _channel.IndicateTypingAsync(_chat, _cts.Token));

            var text = Render();
            if (text == _lastRendered) continue;

            if (await TryAsync(() => _channel.EditAsync(_message, new OutgoingMessage(text, Rich: false), _cts.Token)))
                _lastRendered = text;
        }
    }

    /// <summary>
    /// Сетевой вызов с поглощением ошибок: сообщение удалили или канал просит подождать —
    /// пропускаем такт, но из цикла не выходим, иначе один сбой заморозит статус
    /// до конца запуска.
    /// </summary>
    private async Task<bool> TryAsync(Func<Task> call)
    {
        try
        {
            await call();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Статус запуска не обновлён");
            return false;
        }
    }

    /// <summary>
    /// Останавливает обновления и дожидается их, иначе правка догнала бы удаление
    /// сообщения. Ждём с потолком: зависший вызов не должен держать очередь чата.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) { _logger.LogDebug(ex, "Цикл статуса не завершился вовремя"); }
        _cts.Dispose();
    }
}
