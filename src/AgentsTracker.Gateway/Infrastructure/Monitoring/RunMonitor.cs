using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace AgentsTracker.Gateway.Infrastructure.Monitoring;

/// <summary>Шаг текущего запуска с моментом времени — монитор показывает их все, чат только три последних.</summary>
public sealed record StepRecord(DateTimeOffset At, string Description, bool Nested);

/// <summary>С чем стартовал запуск: то, что известно до ответа CLI.</summary>
public sealed record RunStart(
    string ProjectPath, string? SessionId, string PromptPreview, string? Model, string PermissionMode, string? Effort);

/// <summary>Идущий запуск. <paramref name="DroppedSteps"/> — сколько ранних шагов не поместилось в список.</summary>
public sealed record CurrentRun(
    RunStart Start, DateTimeOffset StartedUtc, int ToolCalls, IReadOnlyList<StepRecord> Steps, int DroppedSteps);

/// <summary>Карточка, которая ждёт нажатия в Telegram.</summary>
public sealed record PendingApproval(DateTimeOffset SinceUtc, string Tool, string Brief);

/// <summary>Всё живое состояние шлюза одним снимком — его получает страница монитора.</summary>
public sealed record LiveState(
    DateTimeOffset GatewayStartedUtc,
    string? CliVersion,
    CurrentRun? Run,
    PendingApproval? Approval,
    IReadOnlyList<string> Queue);

/// <summary>
/// Единственный источник «что шлюз делает сейчас». Фичи сюда пишут (очередь, запуск, шаги,
/// ожидание карточки), веб-монитор читает снимок и подписывается на изменения. Без него
/// текущий запуск живёт только в приватном RunStatusMessage, и снаружи виден лишь IsBusy.
/// </summary>
public sealed class RunMonitor
{
    /// <summary>Сколько шагов держать: полный ввод не хранится, но у долгого запуска их тысячи.</summary>
    private const int StepsKept = 300;

    private readonly Lock _gate = new();
    private readonly List<Channel<LiveState>> _subscribers = [];
    private readonly List<string> _queue = [];
    private readonly List<StepRecord> _steps = [];

    private RunStart? _run;
    private DateTimeOffset _runStartedUtc;
    private int _toolCalls;
    private int _droppedSteps;
    private PendingApproval? _approval;

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Версия CLI — ставит ValidateStartup, когда уже узнал её для лога.</summary>
    public string? CliVersion { get; set; }

    public LiveState Current
    {
        get { lock (_gate) return Build(); }
    }

    public void Enqueued(string preview) => Change(() => _queue.Add(preview));

    /// <summary>Задача взята из очереди в работу — превью уходит из списка ожидающих.</summary>
    public void Dequeued() => Change(() => { if (_queue.Count > 0) _queue.RemoveAt(0); });

    public void QueueCleared() => Change(_queue.Clear);

    public void RunStarted(RunStart start) => Change(() =>
    {
        _run = start;
        _runStartedUtc = DateTimeOffset.UtcNow;
        _toolCalls = 0;
        _droppedSteps = 0;
        _steps.Clear();
    });

    /// <summary>Зовётся из потока чтения stdout — только запоминает, сеть здесь не ходит.</summary>
    public void Step(RunActivity activity) => Change(() =>
    {
        _toolCalls++;
        _steps.Add(new StepRecord(DateTimeOffset.UtcNow, activity.Description, activity.Nested));

        if (_steps.Count <= StepsKept) return;
        _steps.RemoveAt(0);
        _droppedSteps++;
    });

    /// <summary>Снимает текущий запуск и возвращает его — ChatWorker кладёт число вызовов в историю.</summary>
    public CurrentRun? RunFinished()
    {
        CurrentRun? finished = null;
        Change(() =>
        {
            finished = BuildRun();
            _run = null;
            _steps.Clear();
        });
        return finished;
    }

    /// <summary>Отмечает ожидание карточки; Dispose снимает отметку, как бы ожидание ни кончилось.</summary>
    public IDisposable Approval(string tool, string brief)
    {
        Change(() => _approval = new PendingApproval(DateTimeOffset.UtcNow, tool, brief));
        return new Scope(() => Change(() => _approval = null));
    }

    /// <summary>
    /// Текущий снимок сразу, затем — по каждому изменению. Канал на одного подписчика с
    /// вытеснением: медленный браузер получит последнее состояние, а не очередь устаревших.
    /// </summary>
    public async IAsyncEnumerable<LiveState> Changes([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<LiveState>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        lock (_gate)
        {
            _subscribers.Add(channel);
            channel.Writer.TryWrite(Build());
        }

        try
        {
            await foreach (var state in channel.Reader.ReadAllAsync(ct))
                yield return state;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(channel);
        }
    }

    private void Change(Action change)
    {
        lock (_gate)
        {
            change();
            var state = Build();
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(state);
        }
    }

    private LiveState Build() => new(StartedUtc, CliVersion, BuildRun(), _approval, [.. _queue]);

    private CurrentRun? BuildRun() =>
        _run is { } run ? new CurrentRun(run, _runStartedUtc, _toolCalls, [.. _steps], _droppedSteps) : null;

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
