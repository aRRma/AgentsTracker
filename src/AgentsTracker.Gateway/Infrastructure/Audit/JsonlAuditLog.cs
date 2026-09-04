using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentsTracker.Gateway.Infrastructure.Audit;

/// <summary>
/// Аудит в <c>%LOCALAPPDATA%\AgentsTracker\audit\audit-ГГГГ-ММ.jsonl</c>: одна запись в строке,
/// файл на месяц. Запись синхронная с flush на каждую строку — журнал должен пережить
/// падение процесса; объём в десятки килобайт в месяц ротации по размеру не требует.
/// </summary>
public sealed class JsonlAuditLog(ILogger<JsonlAuditLog> logger) : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Кириллица в Summary должна читаться в файле как есть, а не как \u-последовательности.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _directory = Path.Combine(AppPaths.DataDirectory, "audit");
    private readonly Lock _gate = new();

    public void Write(AuditEvent entry)
    {
        var line = JsonSerializer.Serialize(entry, Json);

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                using var stream = new FileStream(
                    FileFor(entry.At), FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(line);
                writer.Flush();
            }
            catch (Exception ex)
            {
                // Аудит не должен ронять обработку сообщения; о сбое узнаем из обычного лога.
                logger.LogError(ex, "Не удалось записать аудит: {Line}", line);
            }
        }
    }

    public IReadOnlyList<AuditEvent> Tail(int count)
    {
        if (count <= 0) return [];

        lock (_gate)
        {
            var result = new List<AuditEvent>(count);

            // Идём от свежего файла к старому, пока не наберём count записей.
            foreach (var file in Files().OrderDescending(StringComparer.Ordinal))
            {
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "Не удалось прочитать {File}", file);
                    continue;
                }

                for (var i = lines.Length - 1; i >= 0 && result.Count < count; i--)
                {
                    if (Parse(lines[i]) is { } entry) result.Add(entry);
                }

                if (result.Count >= count) break;
            }

            result.Reverse();
            return result;
        }
    }

    private IEnumerable<string> Files() =>
        Directory.Exists(_directory) ? Directory.EnumerateFiles(_directory, "audit-*.jsonl") : [];

    private string FileFor(DateTimeOffset moment) =>
        Path.Combine(_directory, $"audit-{moment:yyyy-MM}.jsonl");

    private static AuditEvent? Parse(string line)
    {
        if (line.Length == 0) return null;
        try { return JsonSerializer.Deserialize<AuditEvent>(line, Json); }
        catch (JsonException) { return null; }
    }
}
