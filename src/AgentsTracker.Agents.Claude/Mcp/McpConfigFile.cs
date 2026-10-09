using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AgentsTracker.Agents.Claude.Mcp;

/// <summary>
/// Пишет --mcp-config для CLI: один HTTP-сервер «tg» с инструментами подтверждений и
/// отправки файлов. Токен
/// генерируется при старте и уходит заголовком Authorization — посторонний процесс на той же
/// машине в эндпоинт не постучится, а токен не светится в URL и логах запросов.
///
/// Файл свой у каждого запуска: в нём, кроме токена, заголовок <see cref="RunHeader"/> с id
/// запуска. Задача и вопрос идут параллельно, а MCP-вызов иначе не говорит, чей он, — карточка
/// одного запуска ушла бы в чат другого. Сабагенты живут в том же процессе CLI и шлют тот же
/// заголовок.
///
/// В имени файла PID: общий файл второй экземпляр шлюза перезаписывал своим токеном и удалял
/// на выходе, после чего каждый запуск CLI у рабочего шлюза падал с «mcp__tg__approve not
/// found». Свой файл у процесса такое пересечение исключает.
///
/// Конструктор классический, а не primary: токен и адрес нужны до того, как хост смонтирует
/// эндпоинт, а старые файлы убираются один раз при создании синглтона.
/// </summary>
public sealed class McpConfigFile : IDisposable
{
    public const string ServerName = "tg";
    public const string ToolName = "approve";
    public const string PermissionToolName = $"mcp__{ServerName}__{ToolName}";

    /// <summary>Инструмент, которым агент шлёт файл в чат; зовёт его сам агент, а не CLI.</summary>
    public const string SendFileToolName = "send_file";
    public const string SendFileToolFullName = $"mcp__{ServerName}__{SendFileToolName}";

    /// <summary>Путь эндпоинта. Статичный: секрет теперь в заголовке, а не в URL.</summary>
    public const string RoutePattern = "/mcp";

    /// <summary>Заголовок, которым CLI называет свой запуск в каждом вызове инструмента.</summary>
    public const string RunHeader = "X-AgentsTracker-Run";

    private const string FilePrefix = "mcp-gateway-";
    private const string FileSuffix = ".json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ILogger<McpConfigFile> _logger;
    private readonly string _directory;
    private readonly string _url;
    private readonly long _timeoutMs;

    /// <summary>Начало имён файлов этого процесса: «mcp-gateway-&lt;pid&gt;-».</summary>
    private readonly string _ownPrefix = $"{FilePrefix}{Environment.ProcessId}-";

    public McpConfigFile(AgentHost host, ILogger<McpConfigFile> logger)
    {
        _logger = logger;
        _directory = host.DataDirectory;
        _url = $"http://127.0.0.1:{host.LocalPort}{RoutePattern}";
        _timeoutMs = ToolCallTimeoutMs(host.ApprovalTimeout);
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        RemoveStaleFiles(host.DataDirectory);
    }

    /// <summary>
    /// <c>timeout</c> сервера — предел одного вызова инструмента в мс. Без него CLI обрывает
    /// вызов после 5 минут молчания («sent no response or progress for 300s»), и карточка
    /// остаётся висеть в чате уже мёртвой. Значение не ниже 1000 поднимает до себя и порог
    /// простоя (CLI ≥ 2.1.203); progress-уведомления его не продлевают. Минута сверх
    /// таймаута хоста — запас на отправку карточки и ответ отказом.
    /// </summary>
    private static long ToolCallTimeoutMs(TimeSpan approvalTimeout) =>
        (long)(approvalTimeout + TimeSpan.FromMinutes(1)).TotalMilliseconds;

    /// <summary>Секрет, который CLI присылает в заголовке Authorization.</summary>
    public string Token { get; }

    /// <summary>
    /// Пишет конфиг одного запуска. Файлом, а не JSON-строкой в аргументе: аргументы CLI
    /// попадают в лог, а с ними попал бы и токен. Файл удаляется, когда запуск освобождает
    /// <see cref="RunConfig"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Id не из букв и цифр: он идёт в имя файла.</exception>
    public RunConfig ForRun(string runId)
    {
        if (runId.Length == 0 || !runId.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException($"Id запуска «{runId}» не годится для имени файла.", nameof(runId));

        var path = System.IO.Path.Combine(_directory, $"{_ownPrefix}{runId}{FileSuffix}");

        var config = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [ServerName] = new
                {
                    type = "http",
                    url = _url,
                    headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {Token}",
                        [RunHeader] = runId,
                    },
                    timeout = _timeoutMs,
                },
            },
        };

        File.WriteAllText(path, JsonSerializer.Serialize(config, Indented));
        _logger.LogDebug("MCP-конфиг запуска: {Path}", path);
        return new RunConfig(path, _logger);
    }

    /// <summary>Id запуска из заголовка вызова; null — заголовка нет.</summary>
    public static string? RunIdOf(HttpContext? context) =>
        context?.Request.Headers[RunHeader].ToString() is { Length: > 0 } id ? id : null;

    /// <summary>Проверяет Authorization запроса к MCP. Сравнение постоянное по времени.</summary>
    public bool Authorizes(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(header[prefix.Length..]), Encoding.UTF8.GetBytes(Token));
    }

    /// <summary>
    /// Файлы с секретом не должны переживать процесс: запуск, оборванный остановкой шлюза,
    /// мог не успеть убрать свой.
    /// </summary>
    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, $"{_ownPrefix}*{FileSuffix}"))
                Delete(file, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось убрать MCP-конфиги в {Directory}", _directory);
        }
    }

    /// <summary>
    /// Убирает файлы экземпляров, которые упали не дойдя до Dispose. Файл живого процесса
    /// не трогаем — иначе вернулась бы та же поломка. PID в имени — до первого дефиса:
    /// «mcp-gateway-&lt;pid&gt;-&lt;запуск&gt;.json», у релизов до файлов на запуск —
    /// «mcp-gateway-&lt;pid&gt;.json». Старое общее имя mcp-gateway.json подметается тоже:
    /// его больше никто не пишет.
    /// </summary>
    private void RemoveStaleFiles(string directory)
    {
        // Зовётся до записи своих файлов, так что среди кандидатов их нет. Уборка —
        // «по возможности»: её ошибка не должна мешать старту шлюза.
        try
        {
            var candidates = Directory.EnumerateFiles(directory, $"{FilePrefix}*{FileSuffix}")
                .Append(System.IO.Path.Combine(directory, "mcp-gateway.json"))
                .ToList();

            foreach (var file in candidates)
            {
                if (!File.Exists(file)) continue;

                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                var owner = name.Length > FilePrefix.Length ? name.AsSpan(FilePrefix.Length) : ReadOnlySpan<char>.Empty;
                if (owner.IndexOf('-') is var dash and >= 0) owner = owner[..dash];

                if (int.TryParse(owner, out var pid) && IsGateway(pid)) continue;

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
    /// Живой ли это наш экземпляр. PID после перезагрузки достаётся кому угодно, включая
    /// системные процессы, которые не открыть («Отказано в доступе») — такой шлюзом быть
    /// не может, файл считаем брошенным. Имя сверяем затем же: чужой процесс с тем же PID
    /// не должен удерживать файл мёртвого шлюза.
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

    private static void Delete(string path, ILogger logger)
    {
        try { File.Delete(path); }
        catch (Exception ex) { logger.LogDebug(ex, "Не удалось удалить {Path}", path); }
    }

    /// <summary>Конфиг одного запуска; Dispose удаляет файл с токеном.</summary>
    public sealed class RunConfig(string path, ILogger logger) : IDisposable
    {
        /// <summary>Путь к json-файлу для --mcp-config.</summary>
        public string Path { get; } = path;

        public void Dispose() => Delete(Path, logger);
    }
}
