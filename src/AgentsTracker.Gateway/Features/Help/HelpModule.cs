using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;

namespace AgentsTracker.Gateway.Features.Help;

public sealed class HelpModule : IFeatureModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<ITelegramCommandHandler, HelpCommandHandler>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
