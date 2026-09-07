using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AgentsTracker.Agents.Claude.Mcp;

/// <summary>
/// Пишет --mcp-config для запусков CLI: единственный HTTP-сервер «tg» с инструментом подтверждений.
/// Секретный токен генерируется при старте шлюза и уходит CLI заголовком Authorization — так
/// посторонний процесс на той же машине не сможет постучаться в эндпоинт подтверждений,
/// а сам токен не попадает в URL и в логи запросов.
///
/// Имя файла содержит PID: раньше файл был общим, и второй экземпляр шлюза (пробный прогон,
/// гонка при перезапуске, двойной автозапуск) перезаписывал его своим токеном, а на выходе
/// удалял — рабочий шлюз этого не замечал, и каждый запуск CLI падал с «mcp__tg__approve not
/// found». Свой файл у каждого процесса делает такое пересечение невозможным.
///
/// Конструктор классический, а не primary: файл нужно записать ровно один раз при создании
/// singleton-а, до того как хост смонтирует эндпоинт.
/// </summary>
public sealed class McpConfigFile : IDisposable
{
    public const string ServerName = "tg";
    public const string ToolName = "approve";
    public const string PermissionToolName = $"mcp__{ServerName}__{ToolName}";

    /// <summary>Путь эндпоинта. Статичный: секрет теперь в заголовке, а не в URL.</summary>
    public const string RoutePattern = "/mcp";

    private const string FilePrefix = "mcp-gateway-";
    private const string FileSuffix = ".json";

    private readonly ILogger<McpConfigFile> _logger;

    public McpConfigFile(AgentHost host, ILogger<McpConfigFile> logger)
    {
        _logger = logger;
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Path = System.IO.Path.Combine(host.DataDirectory, $"{FilePrefix}{Environment.ProcessId}{FileSuffix}");

        RemoveStaleFiles(host.DataDirectory);

        var config = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [ServerName] = new
                {
                    type = "http",
                    url = $"http://127.0.0.1:{host.LocalPort}{RoutePattern}",
                    headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
                    timeout = ToolCallTimeoutMs(host.ApprovalTimeout),
                },
            },
        };

        File.WriteAllText(Path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        logger.LogInformation("MCP-конфиг для CLI: {Path}", Path);
    }

    /// <summary>
    /// Поле <c>timeout</c> сервера в конфиге — предел одного вызова инструмента в мс. Без него CLI
    /// обрывает вызов, если сервер молчит 5 минут (idle-таймаут MCP, «sent no response or
    /// progress for 300s»), и <c>ApprovalTimeoutMinutes</c> сверх этого не работал: карточка в
    /// чате ещё висела, а агент уже получил ошибку, и нажатая кнопка пропадала. Значение не
    /// ниже 1000 поднимает и порог простоя до себя (CLI ≥ 2.1.203); progress-уведомления его
    /// не продлевают. Запас поверх таймаута хоста — на отправку карточки и ответ отказом.
    /// </summary>
    private static long ToolCallTimeoutMs(TimeSpan approvalTimeout) =>
        (long)(approvalTimeout + TimeSpan.FromMinutes(1)).TotalMilliseconds;

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

    /// <summary>
    /// Убирает файлы экземпляров, которые упали и не дошли до Dispose. Файл живого процесса
    /// (параллельный экземпляр на другом порту) не трогаем — иначе вернулась бы та же поломка.
    /// Старое общее имя mcp-gateway.json тоже подметается: его больше никто не пишет.
    /// </summary>
    private void RemoveStaleFiles(string directory)
    {
        // Вызывается до записи своего файла, поэтому среди кандидатов его нет. Вся уборка —
        // «по возможности»: любая её ошибка (обход папки, удаление) не должна мешать записи
        // конфига и старту шлюза.
        try
        {
            var candidates = Directory.EnumerateFiles(directory, $"{FilePrefix}*{FileSuffix}")
                .Append(System.IO.Path.Combine(directory, "mcp-gateway.json"))
                .ToList();

            foreach (var file in candidates)
            {
                if (!File.Exists(file)) continue;

                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (name.Length > FilePrefix.Length
                    && int.TryParse(name.AsSpan(FilePrefix.Length), out var pid)
                    && IsGateway(pid))
                {
                    continue;
                }

                File.Delete(file);
                _logger.LogInformation("Удалён MCP-конфиг завершившегося экземпляра: {Path}", file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось убрать старые MCP-конфиги в {Directory}", directory);
        }
    }

    /// <summary>
    /// Живой ли это наш экземпляр. PID после перезагрузки достаётся кому угодно, в том числе
    /// системным процессам, которые нельзя открыть (Win32Exception «Отказано в доступе»), —
    /// такой процесс шлюзом быть не может, файл считаем брошенным. Сверяем и имя: чужой процесс
    /// с тем же PID не должен удерживать файл мёртвого шлюза.
    /// </summary>
    private static bool IsGateway(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited
                && string.Equals(process.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
