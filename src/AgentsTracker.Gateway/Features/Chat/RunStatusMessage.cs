using System.Diagnostics;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
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
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
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
            _toolCalls = activity.ToolCalls;
            _recent.Enqueue((activity.Nested ? "  ↳ " : "▸ ") + activity.Description);
            while (_recent.Count > RecentSteps) _recent.Dequeue();
        }
    }

    private string Render()
    {
        var frame = Clock[(int)(_elapsed.Elapsed.TotalSeconds / Tick.TotalSeconds) % Clock.Length];
        var lines = new List<string> { $"{frame} Работаю… {_elapsed.Elapsed.Elapsed} · 🧵 {_thread}" };

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
                await _bot.SendChatAction(_chatId, ChatAction.Typing, cancellationToken: _cts.Token);

                var text = Render();
                if (text == _lastRendered) continue;

                await _bot.EditMessageText(_chatId, _messageId, text, cancellationToken: _cts.Token);
                _lastRendered = text;
            }
            catch (OperationCanceledException) { return; }
            catch (ApiRequestException ex)
            {
                // Сообщение удалили или Telegram просит подождать — пропускаем такт, не выходим:
                // иначе один сбой сети оставит статус замершим до конца запуска.
                _logger.LogDebug(ex, "Статус запуска не обновлён");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Обновление статуса запуска прервано");
                return;
            }
        }
    }

    /// <summary>Останавливает обновления и дожидается их: иначе правка догнала бы удаление сообщения.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        try { await _loop; } catch { /* цикл сам гасит свои ошибки */ }
        _cts.Dispose();
    }
}
