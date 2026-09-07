using AgentsTracker.Gateway.Features.Settings.Screens;
using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Меню настроек и его текстовые команды.</summary>
public sealed class SettingsModule : IFeatureModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ISettingsScreen, RootScreen>();
        services.AddSingleton<ISettingsScreen, StatusScreen>();
        services.AddSingleton<ISettingsScreen, SessionsScreen>();
        services.AddSingleton<ISettingsScreen, AgentScreen>();
        services.AddSingleton<ISettingsScreen, SkillsScreen>();
        services.AddSingleton<ISettingsScreen, ProjectScreen>();
        services.AddSingleton<ISettingsScreen, UsageScreen>();

        services.AddSingleton<SettingsMenuCoordinator>();
        services.AddSingleton<SkillLauncher>();
        // Раньше ChatModule: текст после кнопки «С аргументами» — аргументы скилла, а не промпт.
        services.AddSingleton<IChatTextHandler, SkillArgumentsTextHandler>();
        services.AddSingleton<IChatButtonHandler, SettingsCallbackHandler>();
        services.AddSingleton<IChatCommandHandler, SettingsCommandHandler>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
