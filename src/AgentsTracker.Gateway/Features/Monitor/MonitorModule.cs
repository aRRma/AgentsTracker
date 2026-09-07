using System.Globalization;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Monitoring;

namespace AgentsTracker.Gateway.Features.Monitor;

/// <summary>
/// Веб-монитор: страница состояния и статистики на отдельном порту, без авторизации, только
/// 127.0.0.1. Ничего не меняет — все мутирующие действия остаются в чате, потому что
/// у страницы нет ни токена, ни пользователя.
/// </summary>
public sealed class MonitorModule : IFeatureModule
{
    /// <summary>Пауза между пустыми кадрами SSE: без них браузер не отличит тишину от упавшего шлюза.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Lazy<byte[]> Page = new(LoadPage);

    public void AddServices(IServiceCollection services, IConfiguration configuration) { }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<GatewayOptions>>().Value;
        if (options.MonitorPort <= 0) return;

        // Kestrel гонит оба порта через один конвейер: без фильтра страница открылась бы и на
        // порту MCP. Фильтр по локальному порту соединения, а не по Host — тот приходит любым.
        var monitorPort = options.MonitorPort;
        var api = endpoints.MapGroup("")
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.Connection.LocalPort == monitorPort ? await next(context) : Results.NotFound());

        api.MapGet("/", () => Results.Bytes(Page.Value, "text/html; charset=utf-8"));

        api.MapGet("/api/snapshot", (RunMonitor monitor, SessionStore store, ChatWorker worker, IAgentBackend agent) =>
            Results.Json(Snapshot(monitor.Current, store, worker, agent), Json));

        api.MapGet("/api/events", (RunMonitor monitor, SessionStore store, ChatWorker worker, IAgentBackend agent, CancellationToken ct) =>
            TypedResults.ServerSentEvents(Events(monitor, store, worker, agent, ct)));

        api.MapGet("/api/limits", async (IAgentLimits limits, SessionStore store, CancellationToken ct) =>
        {
            var snapshot = await limits.GetAsync(ct);
            return Results.Json(new
            {
                snapshot.Error,
                snapshot.FetchedUtc,
                ExtraUsageEnabled = snapshot.ExtraUsage?.IsEnabled,
                Model = store.EffectiveModel,
                Windows = snapshot.Windows.Select(w => new { w.Key, w.Used, w.ResetsAt }),
            }, Json);
        });

        api.MapGet("/api/runs", (SessionStore store, string? project, int? limit) =>
        {
            var runs = store.RecentRuns().AsEnumerable();
            if (project is { Length: > 0 })
                runs = runs.Where(r => ProjectCatalog.Same(r.ProjectPath, project));
            return Results.Json(runs.Take(Math.Clamp(limit ?? 200, 1, 200)), Json);
        });

        api.MapGet("/api/stats", (SessionStore store) => Results.Json(Stats(store), Json));

        api.MapGet("/api/stats.csv", (SessionStore store) =>
            Results.Bytes(Csv(store.Snapshot().Usage), "text/csv; charset=utf-8", "agents-tracker-usage.csv"));

        api.MapGet("/api/audit", (IAuditLog audit, int? count) =>
            Results.Json(audit.Tail(Math.Clamp(count ?? 100, 1, 1000)), Json));

        api.MapGet("/api/log", (RingBufferLog log, int? count, string? level) =>
            Results.Json(log.Tail(Math.Clamp(count ?? 200, 1, 500), RingBufferLog.Level(level ?? "Information")), Json));
    }

    private static object Snapshot(LiveState live, SessionStore store, ChatWorker worker, IAgentBackend agent) => new
    {
        At = DateTimeOffset.UtcNow,
        live.GatewayStartedUtc,
        Agent = agent.DisplayName,
        live.CliVersion,
        Project = store.ProjectPath,
        Session = store.SessionId,
        Model = store.EffectiveModel,
        PermissionMode = store.EffectivePermissionMode,
        Effort = store.EffectiveEffort,
        worker.IsBusy,
        live.Run,
        live.Approvals,
        live.Queue,
    };

    /// <summary>Снимок при подключении, затем по каждому изменению; между ними — пустой кадр раз в 5 с.</summary>
    private static async IAsyncEnumerable<SseItem<string>> Events(
        RunMonitor monitor, SessionStore store, ChatWorker worker, IAgentBackend agent,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var changes = monitor.Changes(ct).GetAsyncEnumerator(ct);
        var next = changes.MoveNextAsync().AsTask();

        while (!ct.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(next, Task.Delay(Heartbeat, ct));

            if (completed != next)
            {
                yield return new SseItem<string>("", "ping");
                continue;
            }

            if (!await next) yield break;

            yield return new SseItem<string>(JsonSerializer.Serialize(Snapshot(changes.Current, store, worker, agent), Json), "state");
            next = changes.MoveNextAsync().AsTask();
        }
    }

    private static object Stats(SessionStore store)
    {
        var state = store.Snapshot();

        return new
        {
            state.Usage.SinceUtc,
            state.Usage.Total,
            ByDay = state.Usage.ByDay.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { Day = p.Key, p.Value.Runs, p.Value.Turns, p.Value.InputTokens, p.Value.OutputTokens, p.Value.CacheReadTokens, p.Value.CacheWriteTokens, p.Value.DurationMs }),
            ByModel = state.Usage.ByModel.OrderByDescending(p => p.Value.Runs).Select(p => new { Model = p.Key, p.Value.Runs, p.Value.InputTokens, p.Value.OutputTokens, p.Value.CacheReadTokens, p.Value.CacheWriteTokens }),
            Skills = state.SkillUsage.OrderByDescending(p => p.Value).Select(p => new { Command = p.Key, Count = p.Value }),
            Sessions = state.Sessions.OrderByDescending(s => s.LastActivityUtc).Select(s => new
            {
                s.Id, s.ProjectPath, s.Title, s.CreatedUtc, s.LastActivityUtc, s.Turns,
                Active = string.Equals(state.ActiveSessions.GetValueOrDefault(s.ProjectPath), s.Id, StringComparison.Ordinal),
            }),
            Rules = state.AlwaysAllowByProject.Select(p => new { Project = p.Key, Rules = p.Value }),
        };
    }

    /// <summary>
    /// По дням, «;», BOM и десятичная запятая — так файл открывается в Excel на русской локали
    /// без мастера импорта, и дробные читаются числами, а не текстом.
    /// </summary>
    private static byte[] Csv(UsageStats usage)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var sb = new StringBuilder();
        sb.AppendLine("Дата;Запусков;Ходов;Входные токены;Выходные токены;Кэш чтение;Кэш запись;Минут");

        foreach (var (day, t) in usage.ByDay.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(day).Append(';')
              .Append(t.Runs).Append(';')
              .Append(t.Turns).Append(';')
              .Append(t.InputTokens).Append(';')
              .Append(t.OutputTokens).Append(';')
              .Append(t.CacheReadTokens).Append(';')
              .Append(t.CacheWriteTokens).Append(';')
              .Append((t.DurationMs / 60000.0).ToString("0.#", ru))
              .AppendLine();
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>Страница вшита в сборку: publish не зависит от папки wwwroot рядом с exe.</summary>
    private static byte[] LoadPage()
    {
        var assembly = typeof(MonitorModule).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("index.html", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
