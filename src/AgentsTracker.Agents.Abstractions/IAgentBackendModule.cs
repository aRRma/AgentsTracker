using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentsTracker.Agents;

/// <summary>
/// Что хост даёт бэкенду, не раскрывая своего конфига: папка данных (для файлов, которые
/// должны пережить только процесс), порт локального HTTP (для канала подтверждений), прокси
/// и сколько хост держит открытым один запрос подтверждения.
/// Регистрируется хостом до <see cref="IAgentBackendModule.AddServices"/>.
/// </summary>
/// <param name="ApprovalTimeout">
/// Сколько <see cref="IOperatorConsole"/> ждёт ответа человека, прежде чем ответить отказом.
/// Бэкенд обязан выставить свои таймауты не короче: иначе агент оборвёт ожидание раньше,
/// чем хост, и нажатая кнопка уйдёт в никуда.
/// </param>
public sealed record AgentHost(string DataDirectory, int LocalPort, string? Proxy, TimeSpan ApprovalTimeout);

/// <summary>
/// Модуль бэкенда: регистрирует <see cref="IAgentBackend"/>, <see cref="IAgentLimits"/>,
/// <see cref="IAgentSkillCatalog"/> и всё своё, монтирует свои эндпоинты (канал подтверждений).
/// Хост выбирает один модуль по <c>Gateway:Agent</c> и больше ничего о нём не знает.
/// </summary>
public interface IAgentBackendModule
{
    /// <summary>Совпадает с <see cref="IAgentBackend.Id"/>.</summary>
    string Id { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
