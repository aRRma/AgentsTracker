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
    ClaudeProcessJob job,
    ILogger<ClaudeBackend> logger) : IAgentBackend
{
    public const string BackendId = "claude";

    /// <summary>Сколько вывода показывать в логе на Error; полный дамп уходит на Debug.</summary>
    private const int ErrorDetailsLimit = 200;

    public string Id => BackendId;

    public string DisplayName => "Claude Code";

    public AgentCapabilities Capabilities => ClaudeCapabilities.Instance;

    public AgentProbe Probe() => new(locator.Resolve(), locator.TryGetVersion());

    public ProjectSettings? InspectProject(string projectPath) => ClaudeProjectSettings.Inspect(projectPath);

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentRunObserver observer, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();

        // Вопрос не продолжает сессию и не оставляет её: разовый id хосту не отдаём,
        // иначе он стал бы активной сессией проекта.
        var persistent = request.Kind != AgentRunKind.Question;
        var resumedSessionId = persistent && request.ResumeSessionId is { Length: > 0 } resumed ? resumed : null;
        var sessionId = resumedSessionId ?? request.NewSessionId;
        var prompt = PromptFor(request);

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

        // Конфиг живёт ровно столько, сколько процесс: в нём токен MCP.
        using var mcp = mcpConfig.ForRun(request.RunId);

        foreach (var arg in BuildArguments(request, prompt, resumedSessionId, sessionId, mcp.Path))
            psi.ArgumentList.Add(arg);

        // Промпт в лог целиком не пишем: это сообщение пользователя, хватит начала.
        logger.LogInformation("claude -p «{Prompt}» {Args}", Text.Preview(prompt),
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

        job.Assign(process);

        // Процесс поднялся — хост запоминает сессию сразу, не дожидаясь ответа: с этого
        // момента её можно продолжить, даже если запуск оборвут.
        if (persistent && resumedSessionId is null)
        {
            observer.SessionStarted(sessionId);
            logger.LogInformation("Новая сессия {SessionId} в {Project}", sessionId, request.ProjectPath);
        }

        // Промпт уже передан аргументом; stdin закрываем, иначе CLI ждёт данных.
        process.StandardInput.Close();

        // Оба потока читаем одновременно — иначе заполненный пайп заблокирует процесс.
        var stream = new StreamCollector(observer, logger, DateTimeOffset.UtcNow);
        var stdoutTask = PumpAsync(process.StandardOutput, stream.Feed);
        var stderrTask = PumpAsync(process.StandardError, stream.FeedError);

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

        // Канал вывода может держать фоновый процесс, которому агент его передал: конец потока
        // придёт только с ним, а ни /stop, ни таймаут запуска сюда уже не дотягиваются. Канал не
        // закрываем: фоновый процесс (чей-то dev-сервер) упал бы на первой же записи. Чтение
        // идёт дальше вхолостую и кончится вместе с ним.
        if (!await DrainedAsync(stdoutTask, stderrTask))
            logger.LogWarning(
                "claude завершился, а его вывод не закрылся за {Grace} с — канал держит фоновый процесс агента; беру прочитанное",
                DrainGrace.TotalSeconds);

        var output = stream.Result();
        var stderr = output.Errors;

        if (cancelled)
        {
            var reason = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested
                ? $"Превышен предел в {request.Timeout.TotalMinutes:0} мин — процесс остановлен."
                : "Остановлено.";

            return new AgentRunResult
            {
                Ok = false,
                Cancelled = true,
                Text = persistent ? reason + Resumable(request.Kind, output.Subagents) : reason,
                SessionId = persistent ? sessionId : null,
                Duration = started.Elapsed,
                // Расхода прерванный запуск не сообщает, а кэш его запросы продлили: без срока
                // «Статус» писал бы «остыл» про живой кэш, и его зря сжимали бы.
                LastRequestUtc = output.LastRequestUtc,
                CacheTtl = output.CacheTtl,
            };
        }

        var result = Parse(output, stderr, process.ExitCode, started.Elapsed, resumedSessionId, sessionId);

        return result with
        {
            SessionId = persistent ? result.SessionId : null,
            Text = request.Kind == AgentRunKind.ContextReport && result.Ok ? ContextSummary(result.Text) : result.Text,
            Usage = result.Usage is { } usage
                ? usage with
                {
                    ContextTokens = output.Compaction?.After ?? output.ContextTokens ?? 0,
                    FirstCacheRead = output.FirstCacheRead,
                }
                : null,
            Compaction = output.Compaction,
            LastRequestUtc = output.LastRequestUtc,
            CacheTtl = output.CacheTtl,
        };
    }

    /// <summary>
    /// Что осталось после обрыва. Без этого обрыв читается как «всё пропало», и задачу начинают
    /// заново — а правки уже на диске, и новая попытка наткнётся на них. Итог сабагента теряется
    /// вместе с процессом, но его шаги CLI пишет на диск по ходу (<c>subagents\agent-*.jsonl</c>
    /// рядом с файлом сессии) — агент может их прочитать.
    /// </summary>
    private static string Resumable(AgentRunKind kind, bool subagents)
    {
        if (kind != AgentRunKind.Task) return " Незавершённый ход продолжится со следующим сообщением.";

        var text = " Правки в файлах сохранились. Напишите «продолжи» — агент доделает задачу в той же сессии.";
        return subagents
            // Отчитался ли сабагент, по потоку не видно, поэтому подсказка условная, а не «не успели».
            ? text + "\nВ запуске работали сабагенты. Если кто-то не успел отчитаться, его шаги сохранены: напишите «прочитай шаги сабагентов и продолжи»."
            : text;
    }

    /// <summary>
    /// Промпт запуска. Команды контекста — свои у Claude Code, хост о них не знает. В режиме
    /// <c>-p</c> они работают (проверено на CLI 2.1.261): /context модель не вызывает,
    /// /compact пишет событие compact_boundary.
    /// </summary>
    private static string PromptFor(AgentRunRequest request) => request.Kind switch
    {
        AgentRunKind.ContextReport => "/context",
        AgentRunKind.Compact => "/compact",
        _ => request.Prompt,
    };

    /// <summary>
    /// Из ответа /context — только заголовок и разбивка по категориям. Дальше идут таблицы
    /// по каждому MCP-инструменту и скиллу: сотни строк, которые в чате разойдутся на
    /// несколько сообщений и заслонят главное.
    /// </summary>
    private static string ContextSummary(string text)
    {
        var categories = text.IndexOf("### Estimated usage by category", StringComparison.Ordinal);
        if (categories < 0) return text;

        var next = text.IndexOf("\n### ", categories + 1, StringComparison.Ordinal);
        return next < 0 ? text : text[..next].TrimEnd();
    }

    /// <summary>Из stdout только нужное разбору итога: строка <c>result</c> и всё, что ею не было.</summary>
    /// <param name="ResultLine">Последняя строка <c>"type":"result"</c>; null — CLI до итога не дошёл.</param>
    /// <param name="Noise">
    /// Не-JSON строки (баннер обновления, текст ошибки) плюс, если итога не было, последнее
    /// событие — единственная подсказка, на чём всё оборвалось.
    /// </param>
    /// <param name="ContextTokens">Контекст последнего хода основной ветки; null — ходов не было.</param>
    /// <param name="Compaction">Сжатие, если оно случилось в запуске: после него контекст — это его итог.</param>
    /// <param name="MainModel">Модель последнего хода основной ветки; null — ходов не было.</param>
    /// <param name="LastRequestUtc">Начало последнего запроса основной ветки; null — после него было сжатие или ходов не было.</param>
    /// <param name="CacheTtl">Срок последней записи основной ветки в кэш; null — записей не было.</param>
    /// <param name="FirstCacheRead">Чтение из кэша первого запроса основной ветки; null — ходов не было.</param>
    /// <param name="Subagents">В запуске работали сабагенты: при обрыве их итог теряется.</param>
    /// <param name="Errors">stderr: туда CLI часто кладёт текст ошибки, оставляя итог пустым.</param>
    private sealed record StreamOutput(
        string? ResultLine, string Noise, long? ContextTokens, ContextCompaction? Compaction, string? MainModel,
        DateTimeOffset? LastRequestUtc, TimeSpan? CacheTtl, long? FirstCacheRead, bool Subagents, string Errors);

    /// <summary>
    /// Сколько ждать конца stdout и stderr после выхода процесса. Фоновый процесс агента
    /// (dev-сервер, watch) наследует его каналы вывода, и конец потока приходит только с ним.
    /// </summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Построчно до конца потока. Может пережить сам запуск — если канал держит фоновый процесс
    /// агента, — поэтому ошибка чтения глушится здесь, а не всплывает необработанной в пустоту.
    /// </summary>
    private async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line) onLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Чтение вывода claude оборвалось");
        }
    }

    /// <summary>true — оба потока дочитаны; false — не закрылись за <see cref="DrainGrace"/>.</summary>
    private static async Task<bool> DrainedAsync(Task stdout, Task stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainGrace);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Разбирает stdout построчно и копит stderr: вызовы инструментов отдаёт наблюдателю,
    /// остальные события не копит — в долгом запуске их мегабайты, а ответу они не нужны.
    /// Накопитель, а не результат чтения: если канал не закрылся после выхода процесса, итог
    /// собирается из уже прочитанного, а чтение бросается.
    /// </summary>
    /// <param name="started">Старт процесса — до него первый запрос к модели начаться не мог.</param>
    private sealed class StreamCollector(IAgentRunObserver observer, ILogger logger, DateTimeOffset started)
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _noise = new();
        private readonly StringBuilder _errors = new();
        private string? _resultLine;
        private string? _lastEvent;
        private long? _context;
        private ContextCompaction? _compaction;
        private string? _mainModel;

        // Поток отдаёт ход целиком, когда ответ уже готов, а начало запроса не сообщает. Запрос
        // начинается после последнего ввода основной ветки — время этого ввода и берём. Оно раньше
        // настоящего начала, и «кэш до» выходит раньше настоящего: лучше ответить с запасом, чем
        // поверить в живой кэш, который уже остыл.
        private DateTimeOffset _requestStart = started;
        private DateTimeOffset? _lastRequest;
        private TimeSpan? _cacheTtl;
        private long? _firstCacheRead;
        private bool _subagents;
        private bool _closed;

        public void Feed(string line)
        {
            // Без замка: после закрытия сюда идёт вывод фонового процесса, иногда сутками, и
            // разбирать его JSON ради того, чтобы выбросить, незачем. Опоздавшую строку отсечёт
            // проверка под замком.
            if (line.Length == 0 || Volatile.Read(ref _closed)) return;

            var parsed = ClaudeStreamEvent.Classify(line);

            lock (_gate)
            {
                // Итог уже собран — опоздавшая строка ничего не меняет и наблюдателю не нужна.
                if (_closed) return;

                if (parsed.IsResult)
                {
                    _resultLine = line;
                    return;
                }

                if (!parsed.IsJson)
                {
                    _noise.AppendLine(line);
                    return;
                }

                _lastEvent = line;

                // Ход после сжатия снова даёт размер контекста — он и есть последний, сжатие
                // остаётся в силе только до него.
                if (parsed.ContextTokens is { } tokens)
                {
                    _context = tokens;
                    _compaction = null;
                }

                if (parsed.MainInput) _requestStart = DateTimeOffset.UtcNow;

                // Сжатие заменяет начало сессии пересказом: кэш прежнего начала к ней больше не
                // относится. Если после сжатия был ход, он запишет новое — и срок снова известен.
                if (parsed.Compaction is { } compacted)
                {
                    _compaction = compacted;
                    _lastRequest = null;
                }

                if (parsed.MainModel is { } model)
                {
                    _mainModel = model;
                    _lastRequest = _requestStart;
                    if (parsed.CacheTtl is { } ttl) _cacheTtl = ttl;
                    _firstCacheRead ??= parsed.CacheRead;
                }

                foreach (var activity in parsed.ToolCalls) _subagents |= activity.Nested;
            }

            foreach (var activity in parsed.ToolCalls)
            {
                // Наблюдатель — чужой код (статус в чате). Его исключение уронило бы чтение
                // stdout, пайп заполнился бы и процесс завис до таймаута.
                try { observer.Activity(activity); }
                catch (Exception ex) { logger.LogWarning(ex, "Обработчик шага запуска бросил исключение"); }
            }
        }

        /// <summary>Строка stderr. После итога не копится: фоновый процесс писал бы туда сутками.</summary>
        public void FeedError(string line)
        {
            lock (_gate)
                if (!_closed) _errors.AppendLine(line);
        }

        /// <summary>
        /// Итог из того, что успело прийти; дальше строки не принимаются — их шлёт уже не
        /// <c>claude</c>, а фоновый процесс, унаследовавший канал.
        /// </summary>
        public StreamOutput Result()
        {
            lock (_gate)
            {
                _closed = true;

                var noise = new StringBuilder().Append(_noise);
                if (_resultLine is null && _lastEvent is not null) noise.AppendLine(_lastEvent);

                return new StreamOutput(_resultLine, noise.ToString().TrimEnd(), _context, _compaction, _mainModel,
                    _lastRequest, _cacheTtl, _firstCacheRead, _subagents, _errors.ToString());
            }
        }
    }

    private IEnumerable<string> BuildArguments(
        AgentRunRequest request, string prompt, string? resumedSessionId, string sessionId, string mcpConfigPath)
    {
        yield return "-p";
        yield return prompt;

        // Поток событий, а не один JSON в конце: по нему чат показывает, чем агент занят.
        // Итог приходит последней строкой той же формы, что у --output-format json.
        // --verbose обязателен: без него CLI не пишет stream-json в режиме -p.
        yield return "--output-format";
        yield return "stream-json";
        yield return "--verbose";

        // Либо продолжаем сессию, либо создаём новую с заранее выданным id — CLI принимает
        // его как есть. Вместе эти флаги передавать нельзя.
        if (request.Kind == AgentRunKind.Question)
        {
            // Вопрос не пишется на диск: в списке сессий он был бы мусором, который
            // не продолжить. Встроенные инструменты — только поиск в сети: файлы машины
            // вопросу ни к чему, а без Bash и Edit ему нечего и спрашивать через карточки.
            // Чужие MCP-серверы из настроек пользователя не грузим — их описания съели бы
            // десятки тысяч токенов контекста на каждый вопрос.
            yield return "--no-session-persistence";
            yield return "--tools";
            yield return "WebSearch,WebFetch";
            yield return "--strict-mcp-config";
        }
        else if (resumedSessionId is not null)
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
        yield return mcpConfigPath;

        // Отправка файла без карточки: папку, тип и размер проверяет сам шлюз, а карточка
        // «разрешить tg:send_file?» лишь дублировала бы его же проверку лишним нажатием.
        yield return "--allowedTools";
        yield return McpConfigFile.SendFileToolFullName;

        // Картинки из чата лежат вне рабочей папки: без этого Read до них не дотянется.
        // Проверка существования обязательна — у чата без вложений папки нет.
        if (request.AttachmentsPath is { Length: > 0 } attachments && Directory.Exists(attachments))
        {
            yield return "--add-dir";
            yield return attachments;
        }

        if (request.Model is { Length: > 0 } model)
        {
            yield return "--model";
            yield return ClaudeCapabilities.CliModel(model);
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
                Usage = payload.ToRunUsage(output.MainModel),
                RateLimited = HitPlanLimit(text) || HitPlanLimit(stderr) || HitPlanLimit(payload.Subtype),
            };
        }

        return new AgentRunResult
        {
            Ok = true,
            Text = text,
            SessionId = payload.SessionId,
            Duration = duration,
            Usage = payload.ToRunUsage(output.MainModel),
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
