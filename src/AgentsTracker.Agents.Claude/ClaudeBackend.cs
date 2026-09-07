using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentsTracker.Agents.Claude.Mcp;

namespace AgentsTracker.Agents.Claude;

/// <summary>
/// Запускает <c>claude -p</c> одним процессом на сообщение. Диалог держится на сессии:
/// id новой выдаёт хост (<c>--session-id</c>), следующий запуск продолжает её через
/// <c>--resume &lt;id&gt;</c>. Что делать с сессией дальше, решает хост по
/// <see cref="AgentRunResult"/>; здесь только процесс и разбор вывода.
/// </summary>
public sealed class ClaudeBackend(
    ClaudeCliLocator locator,
    McpConfigFile mcpConfig,
    ILogger<ClaudeBackend> logger) : IAgentBackend
{
    public const string BackendId = "claude";

    /// <summary>Сколько вывода показывать в логе на Error; полный дамп уходит на Debug.</summary>
    private const int ErrorDetailsLimit = 200;

    public string Id => BackendId;

    public string DisplayName => "Claude Code";

    public AgentCapabilities Capabilities => ClaudeCapabilities.Instance;

    public AgentProbe Probe() => new(locator.Resolve(), locator.TryGetVersion());

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentRunObserver observer, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();

        var resumedSessionId = request.ResumeSessionId is { Length: > 0 } resumed ? resumed : null;
        var sessionId = resumedSessionId ?? request.NewSessionId;

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var runToken = linkedCts.Token;

        var psi = new ProcessStartInfo
        {
            FileName = locator.Resolve(),
            WorkingDirectory = request.ProjectPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        // Кредиты («extra usage») агенту запрещены: с этой переменной CLI не показывает
        // и не выполняет /extra-usage, то есть включить их изнутри запуска нельзя.
        // Выключить их совсем может только владелец аккаунта в claude.ai.
        psi.Environment["DISABLE_EXTRA_USAGE_COMMAND"] = "1";

        foreach (var arg in BuildArguments(request, resumedSessionId, sessionId))
            psi.ArgumentList.Add(arg);

        // Промпт в лог целиком не пишем: это сообщение пользователя, хватит начала.
        logger.LogInformation("claude -p «{Prompt}» {Args}", Truncate(request.Prompt.ReplaceLineEndings(" "), 80),
            string.Join(' ', psi.ArgumentList.Skip(2)));

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось запустить claude");
            return AgentRunResult.Failure($"Не удалось запустить claude: {ex.Message}", started.Elapsed);
        }

        // Процесс поднялся — хост запоминает сессию сразу, не дожидаясь ответа: с этого
        // момента её можно продолжить, даже если запуск оборвут.
        if (resumedSessionId is null)
        {
            observer.SessionStarted(sessionId);
            logger.LogInformation("Новая сессия {SessionId} в {Project}", sessionId, request.ProjectPath);
        }

        // Промпт уже передан аргументом; stdin закрываем, иначе CLI ждёт данных.
        process.StandardInput.Close();

        // Оба потока читаем одновременно — иначе заполненный пайп заблокирует процесс.
        var stdoutTask = ReadStreamAsync(process.StandardOutput, observer);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(runToken);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { /* уже мёртв */ }
        }

        var output = await stdoutTask;
        var stderr = await stderrTask;

        if (cancelled)
        {
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

        return Parse(output, stderr, process.ExitCode, started.Elapsed, resumedSessionId, sessionId);
    }

    /// <summary>Что осталось от stdout для разбора итога.</summary>
    /// <param name="ResultLine">Последняя строка <c>"type":"result"</c>; null — CLI до итога не дошёл.</param>
    /// <param name="Noise">
    /// Не-JSON строки (баннер обновления, текст ошибки) плюс, если итога не было, последнее
    /// событие — единственная подсказка, на чём всё оборвалось.
    /// </param>
    private sealed record StreamOutput(string? ResultLine, string Noise);

    /// <summary>
    /// Читает stdout построчно: вызовы инструментов отдаёт наблюдателю, остальные события
    /// не копит — в долгом запуске их мегабайты, а ответу они не нужны.
    /// </summary>
    private async Task<StreamOutput> ReadStreamAsync(StreamReader stdout, IAgentRunObserver observer)
    {
        var noise = new StringBuilder();
        string? resultLine = null;
        string? lastEvent = null;

        while (await stdout.ReadLineAsync(CancellationToken.None) is { } line)
        {
            if (line.Length == 0) continue;

            var parsed = ClaudeStreamEvent.Classify(line);

            if (parsed.IsResult)
            {
                resultLine = line;
                continue;
            }

            if (!parsed.IsJson)
            {
                noise.AppendLine(line);
                continue;
            }

            lastEvent = line;

            foreach (var activity in parsed.ToolCalls)
            {
                // Наблюдатель — чужой код (статус в чате). Его исключение уронило бы чтение
                // stdout, пайп заполнился бы и процесс завис до таймаута.
                try { observer.Activity(activity); }
                catch (Exception ex) { logger.LogWarning(ex, "Обработчик шага запуска бросил исключение"); }
            }
        }

        if (resultLine is null && lastEvent is not null) noise.AppendLine(lastEvent);

        return new StreamOutput(resultLine, noise.ToString().TrimEnd());
    }

    private IEnumerable<string> BuildArguments(AgentRunRequest request, string? resumedSessionId, string sessionId)
    {
        yield return "-p";
        yield return request.Prompt;

        // Поток событий, а не один JSON в конце: по нему чат показывает, чем агент занят.
        // Итог приходит последней строкой той же формы, что у --output-format json.
        // --verbose обязателен: без него CLI не пишет stream-json в режиме -p.
        yield return "--output-format";
        yield return "stream-json";
        yield return "--verbose";

        // Либо продолжаем сессию, либо создаём новую с заранее выданным id — CLI принимает
        // его как есть. Вместе эти флаги передавать нельзя.
        if (resumedSessionId is not null)
        {
            yield return "--resume";
            yield return resumedSessionId;
        }
        else
        {
            yield return "--session-id";
            yield return sessionId;
        }

        yield return "--permission-prompt-tool";
        yield return McpConfigFile.PermissionToolName;

        // Без явного режима действует defaultMode из настроек пользователя, а при "auto"
        // карточки в чате не появляются вовсе.
        yield return "--permission-mode";
        yield return request.PermissionMode;

        yield return "--mcp-config";
        yield return mcpConfig.Path;

        if (request.Model is { Length: > 0 } model)
        {
            yield return "--model";
            yield return model;
        }

        if (request.Effort is { Length: > 0 } effort && EffortLevels.Resolve(effort) is { } level)
        {
            yield return "--effort";
            yield return level;
        }
    }

    private AgentRunResult Parse(
        StreamOutput output, string stderr, int exitCode, TimeSpan duration,
        string? resumedSessionId, string sessionId)
    {
        ClaudeCliJson? payload = null;
        if (output.ResultLine is { } resultLine)
        {
            try
            {
                payload = JsonSerializer.Deserialize<ClaudeCliJson>(resultLine);
            }
            catch (JsonException ex)
            {
                // Целиком — только на Debug: в строке может быть ответ с содержимым файлов.
                logger.LogWarning(ex, "Итог CLI не разобран как JSON: {Line}", Truncate(resultLine, ErrorDetailsLimit));
                logger.LogDebug("итог целиком: {Line}", Truncate(resultLine, 2000));
            }
        }

        if (payload is null)
        {
            var details = string.IsNullOrWhiteSpace(stderr) ? Truncate(output.Noise, 3000) : Truncate(stderr, 3000);
            logger.LogError("claude завершился с кодом {Code}: {Details}", exitCode, Truncate(details, ErrorDetailsLimit));
            logger.LogDebug("вывод целиком: {Details}", details);

            // Потерянной сессию объявляем только если CLI прямо сказал, что --resume её
            // не нашёл: при любом другом сбое сессия цела, и терять её контекст хуже,
            // чем повторить запуск.
            var lost = resumedSessionId is not null && LooksLikeMissingSession(details, resumedSessionId);

            return AgentRunResult.Failure(
                $"claude завершился с кодом {exitCode}.\n\n```\n{details}\n```",
                duration) with
            {
                SessionId = lost ? null : sessionId,
                SessionLost = lost,
                RateLimited = HitPlanLimit(details),
            };
        }

        // В stream-json текст ошибки часто уходит в stderr, а result приходит пустым (так
        // с «No conversation found» при битом --resume). Без stderr пользователь увидел бы
        // «ошибка без текста», а сброс сессии не сработал бы.
        var text = payload.Result;
        if (string.IsNullOrWhiteSpace(text))
            text = payload.IsError
                ? (string.IsNullOrWhiteSpace(stderr) ? "Агент завершился с ошибкой без текста ответа." : Truncate(stderr.Trim(), 3000))
                : "(пустой ответ)";

        var failed = payload.IsError || exitCode != 0;

        if (failed)
        {
            logger.LogWarning("Запуск завершился ошибкой ({Subtype}, код {Code})", payload.Subtype, exitCode);
            var suffix = payload.Subtype is { Length: > 0 } s ? $"\n\n_({s})_" : "";

            // Тот же случай, но в валидном JSON. На неудачном --resume CLI возвращает свой
            // session_id — отдавать его нельзя, иначе хост сделает битый id активным.
            var lost = resumedSessionId is not null && LooksLikeMissingSession(text + "\n" + stderr, resumedSessionId);

            return new AgentRunResult
            {
                Ok = false,
                Text = text + suffix,
                SessionId = lost ? null : payload.SessionId,
                SessionLost = lost,
                Duration = duration,
                Usage = payload.ToRunUsage(),
                RateLimited = HitPlanLimit(text) || HitPlanLimit(stderr) || HitPlanLimit(payload.Subtype),
            };
        }

        return new AgentRunResult
        {
            Ok = true,
            Text = text,
            SessionId = payload.SessionId,
            Duration = duration,
            Usage = payload.ToRunUsage(),
        };
    }

    /// <summary>
    /// Отдельного кода для «сессия не найдена» у CLI нет, узнаём по тексту
    /// («No conversation found with session ID …»). Кроме маркера требуем сам id: иначе
    /// ответ агента, где эти слова просто упомянуты, снёс бы живую сессию.
    /// </summary>
    private static bool LooksLikeMissingSession(string? text, string resumedSessionId)
    {
        if (text is not { Length: > 0 }) return false;
        if (!text.Contains(resumedSessionId, StringComparison.OrdinalIgnoreCase)) return false;

        string[] markers =
        [
            "no conversation found", "session not found", "could not find session",
            "unable to resume", "failed to resume", "invalid session",
        ];

        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Похоже ли на обрыв по лимиту тарифа. Признака у CLI нет — ни поля в JSON, ни своего
    /// кода возврата, остаётся только текст.
    /// </summary>
    private static bool HitPlanLimit(string? text)
    {
        if (text is not { Length: > 0 }) return false;

        string[] markers =
        [
            "usage limit reached", "usage limit", "usage credits", "extra usage",
            "weekly limit", "5-hour limit", "rate_limit",
        ];

        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось завершить процесс claude");
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
