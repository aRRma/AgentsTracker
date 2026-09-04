using AgentsTracker.Gateway.Infrastructure.Mcp;
using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Подтверждения действий агента: MCP-инструмент, который зовёт CLI, карточки с кнопками
/// в чате, свободные ответы и правила «всегда». Единственный модуль с HTTP-эндпоинтом.
/// </summary>
public sealed class ApprovalsModule : IFeatureModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ApprovalBroker>();
        services.AddSingleton<ITelegramCallbackHandler, ApprovalCallbackHandler>();
        services.AddSingleton<ITelegramTextHandler, ApprovalTextHandler>();
        services.AddSingleton<ITelegramCommandHandler, RulesCommandHandler>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<PermissionTool>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var mcp = endpoints.ServiceProvider.GetRequiredService<McpConfigFile>();

        // Секрет — в заголовке, а не в пути: путь попадает в логи запросов, заголовок нет.
        endpoints.MapMcp(McpConfigFile.RoutePattern)
            .AddEndpointFilter(async (context, next) =>
                mcp.Authorizes(context.HttpContext.Request) ? await next(context) : Results.Unauthorized());
    }
}
