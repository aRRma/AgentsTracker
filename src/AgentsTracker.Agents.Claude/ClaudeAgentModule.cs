using System.Net;
using AgentsTracker.Agents.Claude.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;

namespace AgentsTracker.Agents.Claude;

/// <summary>
/// Claude Code как бэкенд шлюза: процесс <c>claude -p</c>, лимиты подписки, скиллы с диска
/// и MCP-эндпоинт подтверждений, который CLI зовёт через --permission-prompt-tool.
/// </summary>
public sealed class ClaudeAgentModule : IAgentBackendModule
{
    public string Id => ClaudeBackend.BackendId;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ClaudeOptions>(configuration.GetSection(ClaudeOptions.SectionName));

        services.AddSingleton<ClaudeCliLocator>();
        services.AddSingleton<McpConfigFile>();
        services.AddSingleton<IAgentBackend, ClaudeBackend>();
        services.AddSingleton<IAgentLimits, ClaudeLimits>();
        services.AddSingleton<IAgentSkillCatalog, ClaudeSkillCatalog>();

        // Клиент лимитов — через фабрику: обработчик ротируется, DNS не залипает на весь срок
        // жизни процесса. BaseAddress не задаём намеренно — полный URL в запросе.
        services.AddHttpClient(ClaudeLimits.HttpClientName, http =>
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd(ClaudeLimits.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var handler = new SocketsHttpHandler();
                if (sp.GetRequiredService<AgentHost>().Proxy is { Length: > 0 } proxy)
                {
                    handler.Proxy = new WebProxy(proxy);
                    handler.UseProxy = true;
                }
                return handler;
            })
            // Только таймаут, без ретраев и предохранителя: эндпоинт отвечает 429 на частый
            // опрос, и повтор внутри одного вызова — четыре запроса вместо одного. Ответ
            // кэшируется на 3 минуты, а любая ошибка пропускает проверку, не блокирует запуск.
            .AddResilienceHandler("claude-limits", pipeline => pipeline.AddTimeout(ClaudeLimits.RequestTimeout));

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<ClaudePermissionTool>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var mcp = endpoints.ServiceProvider.GetRequiredService<McpConfigFile>();

        // Файл с секретом не должен пережить процесс.
        endpoints.ServiceProvider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(mcp.Dispose);

        // Секрет — в заголовке, а не в пути: путь попадает в логи запросов, заголовок нет.
        endpoints.MapMcp(McpConfigFile.RoutePattern)
            .AddEndpointFilter(async (context, next) =>
                mcp.Authorizes(context.HttpContext.Request) ? await next(context) : Results.Unauthorized());
    }
}
