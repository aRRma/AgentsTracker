using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Help;

public sealed class HelpModule : IFeatureModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<IChatCommandHandler, HelpCommandHandler>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
