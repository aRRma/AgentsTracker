using System.Diagnostics;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>
/// Статусное сообщение идущего запуска. Раз в несколько секунд редактируется: часы
/// на циферблате крутятся, время растёт, под ними — последние шаги агента. Без этого
/// долгий запуск неотличим от зависшего шлюза, и пользователь шлёт /stop зря.
/// Заодно держит индикатор «печатает» — Telegram гасит его через пять секунд.
/// </summary>
internal sealed class RunStatusMessage : IAsyncDisposable
{
    // Четыре, а не пять секунд: индикатор «печатает» Telegram гасит через пять, и на ровно
    // пяти он мигал бы.
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(4);
    private static readonly string[] Clock = ["🕐", "🕑", "🕒", "🕓", "🕔", "🕕", "🕖", "🕗", "🕘", "🕙", "🕚", "🕛"];
    private const int RecentSteps = 3;

    private readonly ITelegramBotClient _bot;
    private readonly long _chatId;
    private readonly string _thread;
    private readonly ILogger _logger;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private readonly Queue<string> _recent = new();

    private int _messageId;
    private int _toolCalls;
    private string _lastRendered = "";
    private Task _loop = Task.CompletedTask;

    private RunStatusMessage(ITelegramBotClient bot, long chatId, string thread, ILogger logger)
    {
        _bot = bot;
        _chatId = chatId;
        _thread = thread;
        _logger = logger;
    }

    public int MessageId => _messageId;

    public static async Task<RunStatusMessage> StartAsync(
        ITelegramBotClient bot, long chatId, string thread, ILogger logger, CancellationToken ct)
    {
        var status = new RunStatusMessage(bot, chatId, thread, logger);
        var text = status.Render();
        var message = await bot.SendMessage(chatId, text, cancellationToken: ct);
        status._messageId = message.MessageId;
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

            // Два вызова — по отдельности: сбой индикатора не должен задерживать текст статуса.
            await TryAsync(() => _bot.SendChatAction(_chatId, ChatAction.Typing, cancellationToken: _cts.Token));

            var text = Render();
            if (text == _lastRendered) continue;

            if (await TryAsync(() => _bot.EditMessageText(_chatId, _messageId, text, cancellationToken: _cts.Token)))
                _lastRendered = text;
        }
    }

    /// <summary>
    /// Сетевой вызов с поглощением ошибок: сообщение удалили или Telegram просит подождать —
    /// пропускаем такт, не выходим из цикла, иначе один сбой сети оставит статус замершим
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
    /// Останавливает обновления и дожидается их: иначе правка догнала бы удаление сообщения.
    /// Ждём ограниченно — зависший HTTP-вызов не должен держать очередь чата.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) { _logger.LogDebug(ex, "Цикл статуса не завершился вовремя"); }
        _cts.Dispose();
    }
}
