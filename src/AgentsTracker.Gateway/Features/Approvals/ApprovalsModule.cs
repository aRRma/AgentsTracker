using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Подтверждения действий агента: карточки с кнопками в чате, свободные ответы, правила
/// «всегда» и <see cref="IOperatorConsole"/>, через который бэкенд всё это спрашивает.
/// Канал, по которому агент доставляет запрос (у Claude — MCP-эндпоинт), — дело бэкенда.
/// </summary>
public sealed class ApprovalsModule : IFeatureModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ApprovalBroker>();
        services.AddSingleton<IOperatorConsole, OperatorConsole>();
        services.AddSingleton<ITelegramCallbackHandler, ApprovalCallbackHandler>();
        services.AddSingleton<ITelegramTextHandler, ApprovalTextHandler>();
        services.AddSingleton<ITelegramCommandHandler, RulesCommandHandler>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
