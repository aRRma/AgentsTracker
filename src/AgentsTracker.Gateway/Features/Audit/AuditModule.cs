using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;

namespace AgentsTracker.Gateway.Features.Audit;

/// <summary>Просмотр журнала действий из чата. Сам журнал — в Infrastructure, им пишут все фичи.</summary>
public sealed class AuditModule : IFeatureModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<ITelegramCommandHandler, AuditCommandHandler>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
