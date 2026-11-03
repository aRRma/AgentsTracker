using AgentsTracker.Gateway.Features.Settings;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;
using AgentsTracker.Gateway.Infrastructure.Modules;

namespace AgentsTracker.Gateway.Features.Topics;

/// <summary>
/// Темы лички: экран «Тип темы» и <c>/topic</c>. Регистрируется после вопросов и перед ChatModule:
/// ожидаемый вопрос из новой темы — всё-таки вопрос, а текст новой темы не должен уйти в очередь.
/// </summary>
public sealed class TopicsModule : IFeatureModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ISettingsScreen, TopicScreen>();

        services.AddSingleton<TopicHandler>();
        services.AddSingleton<IChatCommandHandler>(sp => sp.GetRequiredService<TopicHandler>());
        services.AddSingleton<IChatTextHandler>(sp => sp.GetRequiredService<TopicHandler>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
