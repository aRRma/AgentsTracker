using AgentsTracker.Gateway.Features.Settings.Screens;
using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Меню настроек и его текстовые команды.</summary>
public sealed class SettingsModule : IFeatureModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ISettingsScreen, RootScreen>();
        services.AddSingleton<ISettingsScreen, ProjectScreen>();
        services.AddSingleton<ISettingsScreen, ModelScreen>();
        services.AddSingleton<ISettingsScreen, EffortScreen>();
        services.AddSingleton<ISettingsScreen, ModeScreen>();
        services.AddSingleton<ISettingsScreen, SessionsScreen>();
        services.AddSingleton<ISettingsScreen, UsageScreen>();
        services.AddSingleton<ISettingsScreen, SkillsScreen>();

        services.AddSingleton<SettingsMenuCoordinator>();
        services.AddSingleton<ITelegramCallbackHandler, SettingsCallbackHandler>();
        services.AddSingleton<ITelegramCommandHandler, SettingsCommandHandler>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
