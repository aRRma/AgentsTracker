using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AgentsTracker.Agents.Claude.Mcp;

/// <summary>
/// Инструмент, которым агент отправляет файл человеку в чат. Здесь только перевод:
/// аргументы → <see cref="IOperatorConsole.SendFileAsync"/> → JSON {"sent":true} или
/// {"sent":false,"reason":"…"}. Какие папки, типы и размеры допустимы, решает хост.
/// Отказ — всегда ответом, не исключением: ошибка инструмента у CLI выглядит как сбой
/// сервера, и агент не понял бы, что именно не так.
/// </summary>
[McpServerToolType]
public sealed class ClaudeSendFileTool(IOperatorConsole console, ILogger<ClaudeSendFileTool> logger)
{
    // Список расширений повторяет OperatorConsole.AllowedExtensions в шлюзе: атрибут требует
    // константу, а ссылки на хост отсюда нет. Меняя один — поправьте второй, иначе агент
    // будет предлагать типы, которые шлюз отвергнет.
    [McpServerTool(Name = McpConfigFile.SendFileToolName)]
    [Description("Sends a file from the current project folder to the user in the chat. "
        + "Use it to deliver reports, documents, source files and screenshots. "
        + "Allowed: .md .txt .json .cs .js .html as documents, .png .jpg .jpeg as photos. "
        + "Files outside the project folder are refused.")]
    public async Task<string> SendFile(
        [Description("Path to the file: absolute or relative to the project folder")] string path,
        [Description("Optional caption shown under the file, plain text")] string? caption = null,
        [Description("Send an image as a document to keep it uncompressed")] bool as_document = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await console.SendFileAsync(new FileSendRequest(path, caption, as_document), cancellationToken);
            return result.Sent
                ? JsonSerializer.Serialize(new { sent = true })
                : JsonSerializer.Serialize(new { sent = false, reason = result.Reason });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Сбой при отправке файла {Path}", path);
            return JsonSerializer.Serialize(new { sent = false, reason = $"Шлюз не смог отправить файл: {ex.Message}" });
        }
    }
}
