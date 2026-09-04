using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentsTracker.Gateway.Infrastructure.Mcp;

/// <summary>
/// Пишет --mcp-config для запусков CLI: единственный HTTP-сервер «tg» с инструментом подтверждений.
/// Секретный токен генерируется при старте шлюза и уходит CLI заголовком Authorization — так
/// посторонний процесс на той же машине не сможет постучаться в эндпоинт подтверждений,
/// а сам токен не попадает в URL и в логи запросов.
///
/// Конструктор классический, а не primary: файл нужно записать ровно один раз при создании
/// singleton-а, до того как Program.cs смонтирует эндпоинт.
/// </summary>
public sealed class McpConfigFile : IDisposable
{
    public const string ServerName = "tg";
    public const string ToolName = "approve";
    public const string PermissionToolName = $"mcp__{ServerName}__{ToolName}";

    /// <summary>Путь эндпоинта. Статичный: секрет теперь в заголовке, а не в URL.</summary>
    public const string RoutePattern = "/mcp";

    private readonly ILogger<McpConfigFile> _logger;

    public McpConfigFile(IOptions<GatewayOptions> options, ILogger<McpConfigFile> logger)
    {
        _logger = logger;
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Path = System.IO.Path.Combine(AppPaths.DataDirectory, "mcp-gateway.json");

        var config = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [ServerName] = new
                {
                    type = "http",
                    url = $"http://127.0.0.1:{options.Value.McpPort}{RoutePattern}",
                    headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
                },
            },
        };

        File.WriteAllText(Path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        logger.LogInformation("MCP-конфиг для CLI: {Path}", Path);
    }

    /// <summary>Секрет, который CLI присылает в заголовке Authorization.</summary>
    public string Token { get; }

    /// <summary>Путь к json-файлу для --mcp-config.</summary>
    public string Path { get; }

    /// <summary>Проверяет заголовок Authorization запроса к MCP-эндпоинту. Сравнение — постоянное по времени.</summary>
    public bool Authorizes(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(header[prefix.Length..]), Encoding.UTF8.GetBytes(Token));
    }

    /// <summary>Файл с секретом не должен переживать процесс: следующий старт запишет новый.</summary>
    public void Dispose()
    {
        try { File.Delete(Path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Не удалось удалить {Path}", Path); }
    }
}
