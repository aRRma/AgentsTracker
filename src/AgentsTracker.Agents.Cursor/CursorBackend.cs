using System.Diagnostics;

namespace AgentsTracker.Agents.Cursor;

/// <summary>
/// Один запуск — один процесс <c>agent acp</c>. Сессию выдаёт Cursor; хост её только
/// запоминает. Потерянный <c>session/load</c> обязан вернуть <see cref="AgentRunResult.SessionLost"/>,
/// иначе битый id переживёт перезапуск и будет валить каждый следующий запуск.
/// </summary>
public sealed class CursorBackend(
    CursorCliLocator locator,
    IOperatorConsole console,
    IOptions<CursorOptions> options,
    AgentHost host,
    ILogger<CursorBackend> logger) : IAgentBackend
{
    public const string BackendId = "cursor";

    public string Id => BackendId;

    public string DisplayName => "Cursor";

    public AgentCapabilities Capabilities => CursorCapabilities.Instance;

    public AgentProbe Probe() => new(locator.Resolve(), locator.TryGetVersion());

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentRunObserver observer, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var cwd = Path.GetFullPath(request.ProjectPath);
        var resumed = request.ResumeSessionId is { Length: > 0 } id ? id : null;

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        CursorAcpClient? client = null;
        string? sessionId = resumed;

        try
        {
            client = CursorAcpClient.Start(
                locator.Resolve(), cwd, request.Model, options.Value.ApiKey, host.Proxy,
                request.PermissionMode, console, observer, logger);

            await client.HandshakeAsync(linkedCts.Token);

            if (resumed is null)
            {
                sessionId = await client.NewSessionAsync(cwd, request.PermissionMode, linkedCts.Token);
                observer.SessionStarted(sessionId);
                logger.LogInformation("Новая сессия {SessionId} в {Project}", sessionId, cwd);
            }
            else
            {
                await client.LoadSessionAsync(resumed, cwd, request.PermissionMode, linkedCts.Token);
            }

            var prompt = await client.PromptAsync(sessionId!, request.Prompt, linkedCts.Token);
            return Map(prompt, sessionId, started.Elapsed, cancelled: false);
        }
        catch (CursorSessionLostException ex)
        {
            logger.LogWarning(ex, "Сессия {SessionId} не найдена", resumed);
            return new AgentRunResult
            {
                Ok = false,
                Text = "Cursor не нашёл эту сессию. Следующее сообщение начнёт новую.",
                SessionId = null,
                SessionLost = true,
                Duration = started.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            if (sessionId is { Length: > 0 } && client is not null)
                client.Cancel(sessionId);

            var reason = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested
                ? $"Превышен лимит в {request.Timeout.TotalMinutes:0} мин — процесс остановлен."
                : "Остановлено.";

            return new AgentRunResult
            {
                Ok = false,
                Cancelled = true,
                Text = $"{reason} Незавершённый ход продолжится со следующим сообщением.",
                SessionId = sessionId,
                Duration = started.Elapsed,
            };
        }
        catch (CursorAcpException ex)
        {
            logger.LogWarning(ex, "ACP оборвался");
            var details = Combine(ex.Message, client?.Stderr);
            return AgentRunResult.Failure(details, started.Elapsed) with
            {
                SessionId = sessionId,
                RateLimited = HitPlanLimit(details),
            };
        }
        catch (InvalidOperationException ex)
        {
            return AgentRunResult.Failure(ex.Message, started.Elapsed);
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();
        }
    }

    private static AgentRunResult Map(CursorPromptResult prompt, string? sessionId, TimeSpan duration, bool cancelled)
    {
        var text = prompt.Text.Length > 0 ? prompt.Text : "(пустой ответ)";
        var limited = HitPlanLimit(prompt.Text) || HitPlanLimit(prompt.Stderr) || HitPlanLimit(prompt.StopReason);
        var usage = new RunUsage
        {
            Turns = prompt.Turns,
            DurationMs = (long)duration.TotalMilliseconds,
            InputTokens = prompt.UsedTokens,
        };

        if (prompt.StopReason is "cancelled" || cancelled)
        {
            return new AgentRunResult
            {
                Ok = false,
                Cancelled = true,
                Text = "Остановлено. Незавершённый ход продолжится со следующим сообщением.",
                SessionId = sessionId,
                Duration = duration,
                Usage = usage,
            };
        }

        if (prompt.StopReason is "end_turn")
        {
            return new AgentRunResult
            {
                Ok = true,
                Text = text,
                SessionId = sessionId,
                Duration = duration,
                Usage = usage,
            };
        }

        return new AgentRunResult
        {
            Ok = false,
            Text = $"{text}\n\n_({prompt.StopReason})_",
            SessionId = sessionId,
            Duration = duration,
            Usage = usage,
            RateLimited = limited || prompt.StopReason is "max_tokens" or "max_turn_requests",
        };
    }

    private static string Combine(string message, string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return message;
        var tail = stderr.Length <= 1500 ? stderr.Trim() : stderr[..1500] + "…";
        return $"{message}\n\n```\n{tail}\n```";
    }

    /// <summary>Признака лимита у ACP нет — узнаём по тексту, иначе шлюз жег бы очередь кредитами.</summary>
    internal static bool HitPlanLimit(string? text)
    {
        if (text is not { Length: > 0 }) return false;

        string[] markers =
        [
            "429", "usage limit", "usage limit exceeded", "rate limit", "rate_limit",
            "too many requests", "quota",
        ];

        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
