using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentsTracker.Agents.Cursor;

/// <summary>
/// Cursor как бэкенд шлюза: процесс <c>agent acp</c>, скиллы с диска.
/// Подтверждения идут по stdin ACP, свой HTTP-эндпоинт не нужен.
/// </summary>
public sealed class CursorAgentModule : IAgentBackendModule
{
    public string Id => CursorBackend.BackendId;

    public IReadOnlyList<string> SecretKeys { get; } = [nameof(CursorOptions.ApiKey)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CursorOptions>(configuration.GetSection(CursorOptions.SectionName));
        services.AddSingleton<CursorCliLocator>();
        services.AddSingleton<IAgentBackend, CursorBackend>();
        services.AddSingleton<IAgentLimits, NoAgentLimits>();
        services.AddSingleton<IAgentSkillCatalog, CursorSkillCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
