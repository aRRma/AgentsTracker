using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentsTracker.Gateway.Configuration;
using AgentsTracker.Gateway.State;
using Microsoft.Extensions.Options;

namespace AgentsTracker.Gateway.Claude;

/// <summary>
/// Запускает <c>claude -p</c> одним процессом на сообщение. Непрерывность диалога держится
/// на сессии: id новой шлюз выдаёт сам (<c>--session-id</c>) и сохраняет в SessionStore сразу
/// после старта процесса, следующий запуск продолжает её через <c>--resume &lt;id&gt;</c>.
/// </summary>
public sealed class ClaudeRunner(
    IOptions<GatewayOptions> options,
    ClaudeCliLocator locator,
    SessionStore store,
    McpConfigFile mcpConfig,
    ILogger<ClaudeRunner> logger)
{
    private readonly GatewayOptions _options = options.Value;

    public async Task<ClaudeRunResult> RunAsync(string prompt, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();

        // Фиксируем id один раз: тот же id нужен при разборе ответа, чтобы понять,
        // что упавший запуск шёл с --resume.
        var resumedSessionId = store.SessionId;

        // Новой сессии id выдаём сами и передаём в --session-id. Иначе id известен только из
        // ответа CLI, и /stop, таймаут или падение первого запуска теряют ветку целиком:
        // продолжать нечего, следующее сообщение начинает разговор заново.
        var sessionId = resumedSessionId is { Length: > 0 } ? resumedSessionId : Guid.NewGuid().ToString();

        // И папку тоже: переключение репозитория из меню посреди запуска не должно
        // развести рабочий каталог процесса и проект, которому запишется сессия.
        var projectPath = store.ProjectPath;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_options.RunTimeoutMinutes));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var runToken = linkedCts.Token;

        var psi = new ProcessStartInfo
        {
            FileName = locator.Resolve(),
            WorkingDirectory = projectPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        // Кредиты («extra usage») агенту запрещены: с этой переменной CLI не показывает и не
        // выполняет /extra-usage, так что включить их изнутри запуска нельзя. Совсем снять их
        // может только владелец аккаунта в claude.ai — переменная закрывает путь через агента.
        psi.Environment["DISABLE_EXTRA_USAGE_COMMAND"] = "1";

        foreach (var arg in BuildArguments(prompt, resumedSessionId, sessionId))
            psi.ArgumentList.Add(arg);

        logger.LogInformation("claude {Args}", string.Join(' ', psi.ArgumentList.Skip(2)));

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось запустить claude");
            return ClaudeRunResult.Failure($"Не удалось запустить claude: {ex.Message}", started.Elapsed);
        }

        // Процесс живёт — запоминаем сессию сразу, не дожидаясь ответа: с этого момента её
        // можно продолжить, даже если запуск оборвут.
        if (resumedSessionId is not { Length: > 0 })
        {
            store.RegisterSession(projectPath, prompt, sessionId);
            logger.LogInformation("Новая сессия {SessionId} в {Project}", sessionId, projectPath);
        }

        // Промпт уже передан аргументом; stdin закрываем, иначе CLI ждёт данных.
        process.StandardInput.Close();

        // Оба потока читаем одновременно — иначе заполненный пайп заблокирует процесс.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
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

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (cancelled)
        {
            var reason = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested
                ? $"Превышен лимит в {_options.RunTimeoutMinutes} мин — процесс остановлен."
                : "Остановлено.";

            return new ClaudeRunResult
            {
                Ok = false,
                Cancelled = true,
                Text = $"{reason} Незавершённый ход продолжится со следующим сообщением.",
                SessionId = sessionId,
                Duration = started.Elapsed,
            };
        }

        var result = Parse(stdout, stderr, process.ExitCode, started.Elapsed, resumedSessionId, sessionId);

        // Неудачный запуск тоже стоит денег, поэтому пишем расход и по нему — лишь бы CLI
        // успел его сообщить.
        if (result.Usage is { } usage)
            store.RecordRun(projectPath, prompt, result.SessionId, usage);

        return result;
    }

    private IEnumerable<string> BuildArguments(string prompt, string? resumedSessionId, string sessionId)
    {
        yield return "-p";
        yield return prompt;

        yield return "--output-format";
        yield return "json";

        // Продолжаем известную сессию либо создаём новую с заранее выданным id: CLI принимает
        // его как есть и возвращает тем же. Оба флага вместе передавать нельзя.
        if (resumedSessionId is { Length: > 0 })
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

        // Без явного режима действует defaultMode из настроек пользователя: при "auto"
        // решения принимает классификатор и карточки в чате не появляются.
        yield return "--permission-mode";
        yield return PermissionMode;

        yield return "--mcp-config";
        yield return mcpConfig.Path;

        var model = store.Model ?? _options.Model;
        if (model is { Length: > 0 })
        {
            yield return "--model";
            yield return model;
        }

        if (store.Effort is { Length: > 0 } effort && EffortLevels.Resolve(effort) is { } level)
        {
            yield return "--effort";
            yield return level;
        }

        // Предел стоимости одного запуска: из конфига, но не больше остатка дневного бюджета,
        // иначе один запуск мог бы перескочить лимит целиком.
        if (store.RunBudgetUsd is { } budget)
        {
            yield return "--max-budget-usd";
            yield return budget.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Уровень доступа: выбранный командой /mode, иначе из конфига. Значение из state.json
    /// проверяем — файл правится руками, а неизвестный режим уронил бы каждый запуск.
    /// </summary>
    private string PermissionMode =>
        store.PermissionMode is { Length: > 0 } mode
        && PermissionModes.All.Contains(mode, StringComparer.Ordinal)
            ? mode
            : _options.PermissionMode;

    private ClaudeRunResult Parse(
        string stdout, string stderr, int exitCode, TimeSpan duration,
        string? resumedSessionId, string sessionId)
    {
        ClaudeCliJson? payload = null;
        if (stdout.Length > 0)
        {
            try
            {
                payload = JsonSerializer.Deserialize<ClaudeCliJson>(stdout);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Ответ CLI не разобран как JSON. stdout: {Stdout}", Truncate(stdout, 2000));
            }
        }

        if (payload is null)
        {
            var details = string.IsNullOrWhiteSpace(stderr) ? Truncate(stdout, 3000) : Truncate(stderr, 3000);
            logger.LogError("claude завершился с кодом {Code}: {Details}", exitCode, details);

            // Битый id сессии переживает перезапуск в state.json и валит каждый следующий
            // запуск одинаково. Сбрасываем его, чтобы диалог продолжился с чистой сессии.
            // Только что созданную сессию не трогаем: CLI мог успеть поработать до падения,
            // и терять эту ветку хуже, чем один раз получить отказ на --resume.
            var note = "";
            if (resumedSessionId is { Length: > 0 })
            {
                store.SetSessionId(null);
                note = "\n\n_Сессия сброшена — следующее сообщение начнёт новую._";
                logger.LogWarning("Сессия {SessionId} сброшена после сбоя запуска", resumedSessionId);
            }

            return ClaudeRunResult.Failure(
                $"claude завершился с кодом {exitCode}.\n\n```\n{details}\n```{note}",
                duration) with
            {
                SessionId = resumedSessionId is { Length: > 0 } ? null : sessionId,
                RateLimited = HitPlanLimit(details),
            };
        }

        if (payload.SessionId is { Length: > 0 })
            store.SetSessionId(payload.SessionId);

        var text = payload.Result;
        if (string.IsNullOrWhiteSpace(text))
            text = payload.IsError ? "Агент завершился с ошибкой без текста ответа." : "(пустой ответ)";

        if (payload.IsError || exitCode != 0)
        {
            logger.LogWarning("Запуск завершился ошибкой ({Subtype}, код {Code})", payload.Subtype, exitCode);
            var suffix = payload.Subtype is { Length: > 0 } s ? $"\n\n_({s})_" : "";
            return new ClaudeRunResult
            {
                Ok = false,
                Text = text + suffix,
                SessionId = payload.SessionId,
                CostUsd = payload.TotalCostUsd,
                Duration = duration,
                Usage = payload.ToRunUsage(),
                RateLimited = HitPlanLimit(text) || HitPlanLimit(payload.Subtype),
            };
        }

        return new ClaudeRunResult
        {
            Ok = true,
            Text = text,
            SessionId = payload.SessionId,
            CostUsd = payload.TotalCostUsd,
            Duration = duration,
            Usage = payload.ToRunUsage(),
        };
    }

    /// <summary>
    /// Похоже ли на обрыв по лимиту тарифа. Отдельного признака CLI не даёт — ни поля в JSON,
    /// ни своего exit code, — остаётся текст, которым он это объявляет пользователю.
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
