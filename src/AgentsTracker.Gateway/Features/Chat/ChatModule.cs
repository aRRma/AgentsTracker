using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>
/// Очередь промптов и запуск агента. Регистрируется последним: его текстовый обработчик —
/// «поймать всё», что не забрали остальные.
/// </summary>
public sealed class ChatModule : IFeatureModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Один экземпляр и как сервис (IsBusy/Stop для меню и команд), и как hosted service.
        services.AddSingleton<ChatWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ChatWorker>());

        services.AddSingleton<IChatCommandHandler, ChatCommandHandler>();
        services.AddSingleton<IChatTextHandler, ChatEnqueueTextHandler>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
