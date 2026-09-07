using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Audit;

/// <summary>Просмотр журнала действий из чата. Сам журнал — в Infrastructure, им пишут все фичи.</summary>
public sealed class AuditModule : IFeatureModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<IChatCommandHandler, AuditCommandHandler>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
