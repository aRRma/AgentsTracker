using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentsTracker.Agents;

/// <summary>
/// Что хост даёт бэкенду, не раскрывая своего конфига: папка данных (для файлов, которые
/// должны пережить только процесс), порт локального HTTP (для канала подтверждений) и прокси.
/// Регистрируется хостом до <see cref="IAgentBackendModule.AddServices"/>.
/// </summary>
public sealed record AgentHost(string DataDirectory, int LocalPort, string? Proxy);

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
