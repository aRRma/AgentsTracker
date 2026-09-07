using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentsTracker.Agents;

/// <summary>
/// Что хост даёт бэкенду, не раскрывая своего конфига: папку данных, порт локального HTTP
/// (для канала подтверждений), прокси и срок ожидания подтверждения. Регистрируется хостом
/// до <see cref="IAgentBackendModule.AddServices"/>.
/// </summary>
/// <param name="ApprovalTimeout">
/// Сколько <see cref="IOperatorConsole"/> ждёт человека, прежде чем ответить отказом. Свои
/// таймауты бэкенд обязан ставить не короче: иначе агент оборвёт ожидание раньше хоста,
/// и нажатая кнопка уйдёт в никуда.
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
