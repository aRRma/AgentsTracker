using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;
using AgentsTracker.Gateway.Infrastructure.Modules;

namespace AgentsTracker.Gateway.Features.Question;

/// <summary>
/// Вопрос вне сессии: <c>/ask</c> и ожидание текста после кнопки «Вопрос». Регистрируется
/// раньше ChatModule: иначе ожидаемый вопрос забрала бы очередь задач.
/// </summary>
public sealed class QuestionModule : IFeatureModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<QuestionLauncher>();
        services.AddSingleton<IChatCommandHandler, QuestionCommandHandler>();
        services.AddSingleton<IChatTextHandler, QuestionTextHandler>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
