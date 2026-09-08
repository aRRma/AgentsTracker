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
    private long _usedTokens;
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

    public string Stderr => _stderr.ToString();

    public static CursorAcpClient Start(
        string executable,
        string projectPath,
        string? model,
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

        // Ключ только аргументом: в лог командной строки его не пишем.
        if (apiKey is { Length: > 0 })
        {
            psi.ArgumentList.Add("--api-key");
            psi.ArgumentList.Add(apiKey);
        }

        if (model is { Length: > 0 })
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
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

        logger.LogInformation("agent acp{Model}", model is { Length: > 0 } ? $" --model {model}" : "");

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
            await SendAsync("authenticate", new { methodId = "cursor_login" }, ct);
        }
        catch (CursorAcpException ex)
        {
            // Без входа дальше session/new всё равно отвалится — сразу понятный текст.
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
            await SendAsync("session/load", new { sessionId, cwd, mcpServers = Array.Empty<object>() }, ct);
        }
        catch (CursorAcpException ex) when (LooksLost(ex.Message, sessionId))
        {
            throw new CursorSessionLostException(sessionId, ex);
        }
        catch (CursorAcpException ex) when (LooksUnsupported(ex.Message, "session/load"))
        {
            // Часть CLI отдаёт только session/resume без переигрывания истории.
            try
            {
                await SendAsync("session/resume", new { sessionId, cwd, mcpServers = Array.Empty<object>() }, ct);
            }
            catch (CursorAcpException resumeEx) when (LooksLost(resumeEx.Message, sessionId))
            {
                throw new CursorSessionLostException(sessionId, resumeEx);
            }
        }

        await TrySetModeAsync(sessionId, permissionMode, ct);
    }

    public async Task<CursorPromptResult> PromptAsync(string sessionId, string text, CancellationToken ct)
    {
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

            return new CursorPromptResult(stop, _answer.ToString().Trim(), _turns, _usedTokens, _stderr.ToString());
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
        catch (Exception ex)
        {
            // Старые CLI без set_mode всё равно работают в agent; plan/ask тогда не включатся —
            // лучше предупредить, чем уронить запуск.
            _logger.LogWarning(ex, "Не удалось поставить режим ACP {Mode}", mode);
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
                if (_stderr.Length < 8000)
                {
                    _stderr.AppendLine(line);
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
            // Ответ с карточки не должен стопорить разбор stdout: иначе пайп заполнится.
            _ = HandleRequestAsync(method, id, parameters);
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
            tcs.TrySetException(new CursorAcpException(message));
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
                _ => throw new CursorAcpException($"неизвестный метод {method}"),
            };

            Write(new { jsonrpc = "2.0", id = RawId(id), result });
        }
        catch (OperationCanceledException)
        {
            Write(new { jsonrpc = "2.0", id = RawId(id), result = new { outcome = new { outcome = "cancelled" } } });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ACP-запрос {Method} не обработан", method);
            Write(new
            {
                jsonrpc = "2.0",
                id = RawId(id),
                error = new { code = -32603, message = ex.Message },
            });
        }
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
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

            case "tool_call":
                _turns++;
                Report(DescribeTool(update));
                break;

            case "usage_update":
                if (update.TryGetProperty("used", out var used) && used.TryGetInt64(out var tokens))
                    _usedTokens = tokens;
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

        if (!decision.Allowed)
            return Selected(HasOption(parameters, "reject-once") ? "reject-once" : "reject_once");

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
                ids.Add(new QuestionMap(id, optionIds));
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
            var selected = ids[i].Options
                .Where(o => o.Label.Equals(answer, StringComparison.OrdinalIgnoreCase))
                .Select(o => o.Id)
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
        JsonValueKind.String when long.TryParse(id.GetString(), out var n) => n,
        _ => null,
    };

    private static object RawId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number when id.TryGetInt64(out var n) => n,
        JsonValueKind.String => id.GetString() ?? "",
        _ => id.ToString(),
    };

    private static bool LooksUnsupported(string text, string method) =>
        text.Contains(method, StringComparison.OrdinalIgnoreCase)
        && (text.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unknown method", StringComparison.OrdinalIgnoreCase)
            || text.Contains("method not found", StringComparison.OrdinalIgnoreCase));

    internal static bool LooksLost(string text, string sessionId)
    {
        if (!text.Contains(sessionId, StringComparison.OrdinalIgnoreCase)
            && !text.Contains("session", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] markers =
        [
            "not found", "unknown session", "no session", "failed to load",
            "unable to load", "invalid session", "does not exist",
        ];

        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record QuestionMap(string Id, IReadOnlyList<(string Id, string Label)> Options);
}

internal sealed record CursorPromptResult(
    string StopReason, string Text, int Turns, long UsedTokens, string Stderr);

internal sealed class CursorAcpException : Exception
{
    public CursorAcpException(string message) : base(message) { }

    public CursorAcpException(string message, Exception inner) : base(message, inner) { }
}

internal sealed class CursorSessionLostException : CursorAcpException
{
    public CursorSessionLostException(string sessionId, Exception inner)
        : base($"сессия {sessionId} не найдена", inner)
    {
    }
}
