using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentsTracker.Agents.Cursor;

/// <summary>
/// Клиент <c>agent acp</c>: JSON-RPC по строкам stdin/stdout. Без ответа на
/// <c>session/request_permission</c> инструмент зависает навсегда.
/// </summary>
internal sealed class CursorAcpClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Process _process;
    private readonly IOperatorConsole _console;
    private readonly IAgentRunObserver _observer;
    private readonly ILogger _logger;
    private readonly bool _autoAllow;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly StringBuilder _answer = new();
    private readonly StringBuilder _stderr = new();
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private readonly CancellationTokenSource _readCts = new();
    private long _nextId;
    private bool _collectText;
    private int _turns;
    private long _contextTokens;
    private long _contextWindow;
    private int _disposed;
    private string? _lastActivity;

    private CursorAcpClient(
        Process process, IOperatorConsole console, IAgentRunObserver observer, ILogger logger, bool autoAllow)
    {
        _process = process;
        _console = console;
        _observer = observer;
        _logger = logger;
        _autoAllow = autoAllow;
        _stdoutTask = ReadStdoutAsync(_readCts.Token);
        _stderrTask = ReadStderrAsync();
    }

    /// <summary>Под замком: читатель stderr может дописывать, пока запуск уже разбирает сбой.</summary>
    public string Stderr
    {
        get { lock (_stderr) return _stderr.ToString(); }
    }

    public static CursorAcpClient Start(
        string executable,
        string projectPath,
        string? model,
        string? attachmentsPath,
        string? apiKey,
        string? proxy,
        string permissionMode,
        IOperatorConsole console,
        IAgentRunObserver observer,
        ILogger logger)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = projectPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        // Ключ только в окружении: в списке процессов и в логе командной строки его не видно.
        if (apiKey is { Length: > 0 })
            psi.Environment["CURSOR_API_KEY"] = apiKey;

        if (model is { Length: > 0 })
        {
            // Конфиг и старый state.json идут мимо Resolve — проверяем ещё раз перед запуском.
            if (!CursorCapabilities.IsSafeModel(model))
                throw new InvalidOperationException($"Недопустимое имя модели «{model}»: выберите модель заново в /model.");

            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }

        // Картинки из чата лежат вне рабочей папки: без этого Read до них не дотянется.
        if (attachmentsPath is { Length: > 0 } && Directory.Exists(attachmentsPath))
        {
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(attachmentsPath);
        }

        psi.ArgumentList.Add("acp");

        // Тот же прокси, что и у канала: иначе за шлюзом с Gateway:Proxy агент не достучится
        // до api.cursor.com, а HttpClient шлюза при этом работал бы.
        if (proxy is { Length: > 0 })
        {
            psi.Environment["HTTP_PROXY"] = proxy;
            psi.Environment["HTTPS_PROXY"] = proxy;
            psi.Environment["NODE_USE_ENV_PROXY"] = "1";
        }

        logger.LogInformation(
            "agent acp{Model}{AddDir}",
            model is { Length: > 0 } ? $" --model {model}" : "",
            attachmentsPath is { Length: > 0 } && Directory.Exists(attachmentsPath) ? " --add-dir" : "");

        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"Не удалось запустить agent: {ex.Message}", ex);
        }

        process.StandardInput.AutoFlush = true;
        return new CursorAcpClient(process, console, observer, logger, CursorPermissionModes.AutoAllow(permissionMode));
    }

    public async Task HandshakeAsync(CancellationToken ct)
    {
        await SendAsync("initialize", new
        {
            protocolVersion = 1,
            clientCapabilities = new
            {
                fs = new { readTextFile = false, writeTextFile = false },
                terminal = false,
            },
            clientInfo = new { name = "AgentsTracker", version = "1.0.0" },
        }, ct);

        try
        {
            // Браузерный login из headless зависает навсегда — 20 с хватит понять, что входа нет.
            using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            authCts.CancelAfter(TimeSpan.FromSeconds(20));
            await SendAsync("authenticate", new { methodId = "cursor_login" }, authCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CursorAcpException(
                "Cursor CLI не ответил на вход. В PowerShell выполните «agent login» "
                + "или задайте Gateway:Cursor:ApiKey / CURSOR_API_KEY.");
        }
        catch (CursorAcpException ex) when (AlreadyLoggedIn(ex.Message))
        {
            // Повторный authenticate на уже вошедшем CLI не должен валить запуск.
        }
        catch (CursorAcpException ex)
        {
            throw new CursorAcpException(
                "Cursor CLI не вошёл в аккаунт. В PowerShell выполните «agent login» "
                + "или задайте Gateway:Cursor:ApiKey / CURSOR_API_KEY.",
                ex);
        }
    }

    public async Task<string> NewSessionAsync(string cwd, string permissionMode, CancellationToken ct)
    {
        var result = await SendAsync("session/new", new { cwd, mcpServers = Array.Empty<object>() }, ct);
        var sessionId = RequiredString(result, "sessionId");
        await TrySetModeAsync(sessionId, permissionMode, ct);
        return sessionId;
    }

    public async Task LoadSessionAsync(string sessionId, string cwd, string permissionMode, CancellationToken ct)
    {
        try
        {
            try
            {
                await SendAsync("session/load", new { sessionId, cwd, mcpServers = Array.Empty<object>() }, ct);
            }
            catch (CursorRpcException ex) when (LooksUnsupported(ex.Message, "session/load"))
            {
                // Часть CLI отдаёт только session/resume без переигрывания истории.
                await SendAsync("session/resume", new { sessionId, cwd, mcpServers = Array.Empty<object>() }, ct);
            }
        }
        catch (CursorRpcException ex)
        {
            // Любой отказ CLI — битый id: иначе он переживёт перезапуск и будет валить каждый
            // запуск. Упавший процесс сюда не попадает — из-за сбоя историю не выбрасываем.
            throw new CursorSessionLostException(sessionId, ex);
        }

        await TrySetModeAsync(sessionId, permissionMode, ct);
    }

    public async Task<CursorPromptResult> PromptAsync(string sessionId, string text, CancellationToken ct)
    {
        _answer.Clear();
        _turns = 0;
        _contextTokens = 0;
        _contextWindow = 0;
        _collectText = true;
        try
        {
            var result = await SendAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text } },
            }, ct);

            var stop = result.TryGetProperty("stopReason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString() ?? "end_turn"
                : "end_turn";

            return new CursorPromptResult(stop, _answer.ToString().Trim(), _turns, _contextTokens, _contextWindow);
        }
        finally
        {
            _collectText = false;
        }
    }

    public void Cancel(string sessionId)
    {
        try { Notify("session/cancel", new { sessionId }); }
        catch (Exception ex) { _logger.LogDebug(ex, "session/cancel не ушёл"); }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось завершить процесс agent");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _readCts.Cancel();
        Kill();

        try { await _process.WaitForExitAsync(CancellationToken.None); } catch { /* уже мёртв */ }

        FailAll("процесс agent остановлен");
        try { await Task.WhenAll(_stdoutTask, _stderrTask); } catch { /* чтение закрыто */ }

        _readCts.Dispose();
        _process.Dispose();
    }

    private async Task TrySetModeAsync(string sessionId, string permissionMode, CancellationToken ct)
    {
        var mode = CursorPermissionModes.AcpMode(permissionMode);
        try
        {
            await SendAsync("session/set_mode", new { sessionId, modeId = mode }, ct);
        }
        catch (CursorRpcException ex) when (mode == "agent")
        {
            // Полный режим у CLI и так по умолчанию: старый agent без set_mode работать может.
            _logger.LogWarning(ex, "Не удалось поставить режим ACP {Mode}", mode);
        }
        catch (CursorRpcException ex)
        {
            // Человек выбрал «только читает» — молча отработать в полном режиме нельзя.
            throw new CursorAcpException($"Cursor CLI не включил режим {mode} — обновите agent.", ex);
        }
    }

    private async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        Write(new { jsonrpc = "2.0", id, method, @params = parameters });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

        try
        {
            return await tcs.Task.WaitAsync(linked.Token);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void Notify(string method, object? parameters) =>
        Write(new { jsonrpc = "2.0", method, @params = parameters });

    private void Write(object payload)
    {
        var line = JsonSerializer.Serialize(payload, Json);
        lock (_process)
        {
            if (_process.HasExited)
                throw new CursorAcpException("процесс agent уже завершился");

            _process.StandardInput.WriteLine(line);
        }
    }

    private async Task ReadStdoutAsync(CancellationToken ct)
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;

                JsonElement root;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    root = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    _logger.LogDebug("Не-JSON от agent: {Line}", Text.Preview(line, 200));
                    continue;
                }

                Dispatch(root);
            }
        }
        catch (OperationCanceledException)
        {
            // Остановка шлюза или Dispose.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Чтение stdout agent оборвалось");
        }

        FailAll("stdout agent закрылся");
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                lock (_stderr)
                {
                    if (_stderr.Length < 8000) _stderr.AppendLine(line);
                }

                _logger.LogDebug("agent stderr: {Line}", Text.Preview(line, 200));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Чтение stderr agent оборвалось");
        }
    }

    private void Dispatch(JsonElement root)
    {
        var hasMethod = root.TryGetProperty("method", out var methodEl)
                        && methodEl.ValueKind == JsonValueKind.String;
        var hasId = root.TryGetProperty("id", out var idEl)
                    && idEl.ValueKind is JsonValueKind.Number or JsonValueKind.String;

        if (hasMethod && hasId)
        {
            var method = methodEl.GetString()!;
            var id = idEl.Clone();
            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;

            // Без ответа CLI ждёт вечно. Неизвестное — сразу -32601; карточки — не в этом потоке.
            if (method is not ("session/request_permission" or "cursor/ask_question"
                or "cursor/create_plan" or "cursor/generate_image"))
            {
                WriteMethodNotFound(id, method);
                return;
            }

            _ = Task.Run(() => HandleRequestAsync(method, id, parameters));
            return;
        }

        if (hasMethod)
        {
            HandleNotification(methodEl.GetString()!, root.TryGetProperty("params", out var p) ? p : default);
            return;
        }

        if (!hasId) return;

        var key = IdKey(idEl);
        if (key is null || !_pending.TryRemove(key.Value, out var tcs)) return;

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                ? msg.GetString() ?? "ошибка ACP"
                : "ошибка ACP";
            tcs.TrySetException(new CursorRpcException(message));
            return;
        }

        tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
    }

    private async Task HandleRequestAsync(string method, JsonElement id, JsonElement parameters)
    {
        try
        {
            object result = method switch
            {
                "session/request_permission" => await DecidePermissionAsync(parameters),
                "cursor/ask_question" => await AnswerQuestionAsync(parameters),
                "cursor/create_plan" => await DecidePlanAsync(parameters),
                "cursor/generate_image" => await SendGeneratedFileAsync(parameters),
                _ => throw new CursorAcpException($"неизвестный метод {method}"),
            };

            TryWrite(new { jsonrpc = "2.0", id = RawId(id), result });
        }
        catch (OperationCanceledException)
        {
            TryWrite(new { jsonrpc = "2.0", id = RawId(id), result = new { outcome = new { outcome = "cancelled" } } });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ACP-запрос {Method} не обработан", method);
            TryWrite(new
            {
                jsonrpc = "2.0",
                id = RawId(id),
                error = new { code = -32603, message = ex.Message },
            });
        }
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        // generate_image в доке Cursor то запрос, то уведомление: без ответа на запрос CLI
        // блокируется, уведомление уходит в чат тем же SendFileAsync, ответа не ждёт.
        if (method is "cursor/generate_image")
        {
            _ = Task.Run(() => SendGeneratedFileAsync(parameters));
            return;
        }

        if (method is not "session/update") return;
        if (parameters.ValueKind != JsonValueKind.Object) return;
        if (!parameters.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
            return;

        var kind = update.TryGetProperty("sessionUpdate", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

        switch (kind)
        {
            case "agent_message_chunk" when _collectText:
                if (TryText(update, out var chunk)) _answer.Append(chunk);
                break;

            case "tool_call" when _collectText:
                _turns++;
                Report(DescribeTool(update));
                break;

            // По спеке ACP used/size — заполнение окна контекста, а не потраченные токены:
            // в InputTokens они раздули бы статистику. cost не читаем — денег в шлюзе нет.
            case "usage_update":
                if (update.TryGetProperty("used", out var used) && used.TryGetInt64(out var tokens))
                    _contextTokens = tokens;
                if (update.TryGetProperty("size", out var size) && size.TryGetInt64(out var window))
                    _contextWindow = window;
                break;
        }
    }

    private async Task<object> DecidePermissionAsync(JsonElement parameters)
    {
        var tool = parameters.TryGetProperty("toolCall", out var call) ? call : default;
        var title = StringProp(tool, "title") ?? StringProp(tool, "kind") ?? "Tool";
        var input = tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("rawInput", out var raw)
            ? raw.Clone()
            : (JsonElement?)null;

        Report(title);

        if (_autoAllow)
            return Selected("allow-once");

        var suggested = AlwaysOption(parameters);
        var decision = await _console.ApproveAsync(new ApprovalRequest(title, input, suggested), CancellationToken.None);

        // Без варианта отказа — «cancelled» по спеке ACP: выдуманный id CLI мог бы понять как угодно.
        if (!decision.Allowed)
            return HasOption(parameters, "reject-once") ? Selected("reject-once") : new { outcome = new { outcome = "cancelled" } };

        if (decision.PersistRules && HasOption(parameters, "allow-always"))
            return Selected("allow-always");

        return Selected("allow-once");
    }

    private async Task<object> AnswerQuestionAsync(JsonElement parameters)
    {
        var header = StringProp(parameters, "title");
        var questions = new List<AgentQuestion>();
        var ids = new List<QuestionMap>();

        if (parameters.TryGetProperty("questions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var id = StringProp(item, "id") ?? Guid.NewGuid().ToString("N");
                var prompt = StringProp(item, "prompt") ?? "";
                var multi = item.TryGetProperty("allowMultiple", out var m) && m.ValueKind == JsonValueKind.True;
                var options = new List<QuestionOption>();
                var optionIds = new List<(string Id, string Label)>();

                if (item.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in opts.EnumerateArray())
                    {
                        var optionId = StringProp(option, "id") ?? "";
                        var label = StringProp(option, "label") ?? optionId;
                        options.Add(new QuestionOption(label, null));
                        optionIds.Add((optionId, label));
                    }
                }

                questions.Add(new AgentQuestion(prompt, header, multi, options));
                ids.Add(new QuestionMap(id, optionIds, multi));
            }
        }

        if (questions.Count == 0)
            return new { outcome = new { outcome = "skipped", reason = "пустой вопрос" } };

        var result = await _console.AskAsync(questions, CancellationToken.None);
        if (result.Answers is null)
            return new { outcome = new { outcome = "skipped", reason = result.Refusal ?? "нет ответа" } };

        var answers = new JsonArray();
        for (var i = 0; i < result.Answers.Count && i < ids.Count; i++)
        {
            var answer = result.Answers[i].Answer;

            // Множественный выбор приходит текстом «Вариант A, Вариант B» (см. подсказку
            // в OperatorConsole.AskOneAsync) — без разбивки по запятой ни один вариант не
            // совпал бы с целой строкой и вопрос всегда уходил бы agent'у как «skipped».
            var labels = ids[i].Multi
                ? answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [answer];

            var selected = labels
                .SelectMany(label => ids[i].Options.Where(o => o.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
                .Select(o => o.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (selected.Length == 0)
                return new { outcome = new { outcome = "skipped", reason = answer } };

            answers.Add(new JsonObject
            {
                ["questionId"] = ids[i].Id,
                ["selectedOptionIds"] = new JsonArray(selected.Select(id => JsonValue.Create(id)).ToArray()),
            });
        }

        return new { outcome = new { outcome = "answered", answers } };
    }

    private async Task<object> DecidePlanAsync(JsonElement parameters)
    {
        var name = StringProp(parameters, "name") ?? "План";
        var overview = StringProp(parameters, "overview");
        var plan = StringProp(parameters, "plan") ?? "";
        var input = JsonSerializer.SerializeToElement(new { name, overview, plan }, Json);

        if (_autoAllow)
            return new { outcome = new { outcome = "accepted" } };

        var decision = await _console.ApproveAsync(new ApprovalRequest(name, input, null), CancellationToken.None);
        return decision.Allowed
            ? new { outcome = new { outcome = "accepted" } }
            : new { outcome = new { outcome = "rejected", reason = decision.Reason ?? "отклонено" } };
    }

    /// <summary>
    /// Картинка или файл из ACP — тот же <see cref="IOperatorConsole.SendFileAsync"/>, что
    /// Claude зовёт через MCP: политику папки и типа хост уже знает, дублировать её здесь нельзя.
    /// </summary>
    private async Task<object> SendGeneratedFileAsync(JsonElement parameters)
    {
        var path = StringProp(parameters, "filePath");
        var caption = StringProp(parameters, "description");
        if (path is not { Length: > 0 })
            return new { outcome = new { outcome = "rejected", reason = "нет filePath" } };

        try
        {
            var result = await _console.SendFileAsync(new FileSendRequest(path, caption, AsDocument: false), CancellationToken.None);
            if (!result.Sent)
            {
                _logger.LogWarning("Cursor не отправил файл {Path}: {Reason}", path, result.Reason);
                return new { outcome = new { outcome = "rejected", reason = result.Reason } };
            }

            return new { outcome = new { outcome = "generated", filePath = path } };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cursor не отправил файл {Path}", path);
            return new { outcome = new { outcome = "rejected", reason = ex.Message } };
        }
    }

    private void Report(string description)
    {
        if (description.Length == 0) return;
        if (string.Equals(_lastActivity, description, StringComparison.Ordinal)) return;
        _lastActivity = description;

        try { _observer.Activity(new RunActivity(description)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Обработчик шага запуска бросил исключение"); }
    }

    private void FailAll(string message)
    {
        foreach (var item in _pending)
        {
            if (_pending.TryRemove(item.Key, out var tcs))
                tcs.TrySetException(new CursorAcpException(message));
        }
    }

    private static object Selected(string optionId) =>
        new { outcome = new { outcome = "selected", optionId } };

    private static IReadOnlyList<PersistentRule>? AlwaysOption(JsonElement parameters)
    {
        if (!HasOption(parameters, "allow-always")) return null;

        // Сырой элемент отдаём хосту как есть: форму правила знает агент, не шлюз.
        using var doc = JsonDocument.Parse("""{"id":"allow-always"}""");
        return [new PersistentRule("allow-always", doc.RootElement.Clone())];
    }

    private static bool HasOption(JsonElement parameters, string optionId)
    {
        if (!parameters.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var option in options.EnumerateArray())
        {
            if (StringProp(option, "optionId") == optionId) return true;
        }

        return false;
    }

    private static bool TryText(JsonElement update, out string text)
    {
        text = "";
        if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
            return false;
        if (!content.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        text = value.GetString() ?? "";
        return text.Length > 0;
    }

    private static string DescribeTool(JsonElement update)
    {
        var title = StringProp(update, "title");
        if (title is { Length: > 0 }) return title;

        var kind = StringProp(update, "kind") ?? "tool";
        if (update.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in locations.EnumerateArray())
            {
                var path = StringProp(location, "path");
                if (path is { Length: > 0 })
                    return $"{kind} {Path.GetFileName(path)}";
            }
        }

        return kind;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = StringProp(element, name);
        return value is { Length: > 0 } ? value : throw new CursorAcpException($"в ответе нет {name}");
    }

    private static string? StringProp(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? IdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number when id.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(id.GetString(), CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    private static object RawId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number when id.TryGetInt64(out var n) => n,
        JsonValueKind.String => id.GetString() ?? "",
        _ => id.ToString(),
    };

    private void WriteMethodNotFound(JsonElement id, string method) =>
        TryWrite(new
        {
            jsonrpc = "2.0",
            id = RawId(id),
            error = new { code = -32601, message = $"Method not found: {method}" },
        });

    private void TryWrite(object payload)
    {
        try { Write(payload); }
        catch (Exception ex) { _logger.LogDebug(ex, "Ответ ACP не ушёл: процесс уже мёртв"); }
    }

    /// <summary>Не путать с «not authenticated»: там тоже есть слово authenticated.</summary>
    private static bool AlreadyLoggedIn(string text)
    {
        string[] markers = ["already authenticated", "already logged in", "not required", "no authentication"];
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksUnsupported(string text, string method) =>
        text.Contains(method, StringComparison.OrdinalIgnoreCase)
        && (text.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unknown method", StringComparison.OrdinalIgnoreCase)
            || text.Contains("method not found", StringComparison.OrdinalIgnoreCase));

    private sealed record QuestionMap(string Id, IReadOnlyList<(string Id, string Label)> Options, bool Multi);
}

internal sealed record CursorPromptResult(
    string StopReason, string Text, int Turns, long ContextTokens, long ContextWindow);

internal class CursorAcpException : Exception
{
    public CursorAcpException(string message) : base(message) { }

    public CursorAcpException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Ответ CLI с полем <c>error</c> — в отличие от сбоя транспорта (процесс умер, stdout закрылся).</summary>
internal sealed class CursorRpcException(string message) : CursorAcpException(message);

internal sealed class CursorSessionLostException : CursorAcpException
{
    public CursorSessionLostException(string sessionId, Exception inner)
        : base($"сессия {sessionId} не найдена", inner)
    {
    }
}
