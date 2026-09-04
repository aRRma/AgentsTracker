namespace AgentsTracker.Gateway.Infrastructure.Modules;

/// <summary>
/// Модуль фичи: сам регистрирует свои сервисы и обработчики и сам монтирует свои эндпоинты.
/// Список модулей задаётся явно в Program.cs — порядок регистрации определяет порядок
/// опроса обработчиков текста.
/// </summary>
public interface IFeatureModule
{
    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
