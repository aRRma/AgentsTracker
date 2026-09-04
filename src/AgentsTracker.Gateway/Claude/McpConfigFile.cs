using System.Text.Json;
using AgentsTracker.Gateway.Configuration;
using AgentsTracker.Gateway.State;
using Microsoft.Extensions.Options;

namespace AgentsTracker.Gateway.Claude;

/// <summary>
/// Пишет --mcp-config для запусков CLI: единственный HTTP-сервер «tg» с инструментом подтверждений.
/// URL содержит секретный токен, сгенерированный при старте шлюза, — так посторонний процесс
/// на той же машине не сможет постучаться в эндпоинт подтверждений.
/// </summary>
public sealed class McpConfigFile
{
    public const string ServerName = "tg";
    public const string ToolName = "approve";
    public const string PermissionToolName = $"mcp__{ServerName}__{ToolName}";

    public McpConfigFile(IOptions<GatewayOptions> options, ILogger<McpConfigFile> logger)
    {
        Token = Guid.NewGuid().ToString("N");
        RoutePattern = $"/mcp/{Token}";
        Path = System.IO.Path.Combine(AppPaths.DataDirectory, "mcp-gateway.json");

        var config = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [ServerName] = new
                {
                    type = "http",
                    url = $"http://127.0.0.1:{options.Value.McpPort}{RoutePattern}",
                },
            },
        };

        File.WriteAllText(Path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        logger.LogInformation("MCP-конфиг для CLI: {Path}", Path);
    }

    /// <summary>Секрет в пути эндпоинта.</summary>
    public string Token { get; }

    /// <summary>Путь, на который нужно смонтировать MCP-эндпоинт.</summary>
    public string RoutePattern { get; }

    /// <summary>Путь к json-файлу для --mcp-config.</summary>
    public string Path { get; }
}
