using System.Globalization;
using System.Text;

namespace AgentsTracker.Gateway.Infrastructure.Monitoring;

/// <summary>
/// Лог в <c>logs\gateway-ГГГГ-ММ-ДД.log</c> папки данных. Без него лог жил только в консоли,
/// которой у задачи Планировщика нет, и в 500 строках памяти: после перезапуска разбирать было
/// нечего. Пишем синхронно с flush на строку: фоновая очередь не успела бы записать Critical
/// перед выходом процесса.
/// </summary>
public sealed class LogFile(string directory)
{
    /// <summary>Сколько суток хранить: за две недели жалоба доходит до разбора.</summary>
    public const int KeptDays = 14;

    private const string Prefix = "gateway-";
    private const string DayFormat = "yyyy-MM-dd";
    private const string Indent = "    ";

    private readonly Lock _gate = new();
    private DateOnly _day;

    public void Write(LogEntry entry)
    {
        var at = entry.At.ToLocalTime();
        var line = Format(at, entry);
        var day = DateOnly.FromDateTime(at.DateTime);

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (day != _day)
                {
                    _day = day;
                    Sweep(day);
                }

                using var stream = new FileStream(
                    Path.Combine(directory, Prefix + day.ToString(DayFormat, CultureInfo.InvariantCulture) + ".log"),
                    FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(line);
            }
            catch (Exception)
            {
                // Не в ILogger: сбой записи вернулся бы сюда же. Консоли у задачи Планировщика
                // нет, сказать о нём некому — а ронять из-за лога обработку сообщения нельзя.
            }
        }
    }

    /// <summary>
    /// Продолжение записи — с отступом: строка, которая начинается с даты, всегда начало новой
    /// записи, и поиск по времени не путается в стеке исключения.
    /// </summary>
    internal static string Format(DateTimeOffset at, LogEntry entry)
    {
        var text = new StringBuilder()
            .Append(at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(Level(entry.Level))
            .Append(' ').Append(entry.Category).Append(": ")
            .AppendLine(Continued(entry.Message));

        if (entry.Exception is { Length: > 0 } exception) text.Append(Indent).AppendLine(Continued(exception));
        return text.ToString();
    }

    private static string Continued(string text) =>
        text.TrimEnd().ReplaceLineEndings(Environment.NewLine + Indent);

    private static string Level(string name) => RingBufferLog.Level(name) switch
    {
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => name.ToUpperInvariant(),
    };

    /// <summary>
    /// Срок — по дате в имени, а не по времени изменения: файл из бэкапа получил бы свежую
    /// дату и пережил бы срок. Файл, который не удалился, удалится на следующей смене дня.
    /// </summary>
    private void Sweep(DateOnly today)
    {
        var oldest = today.AddDays(-KeptDays);
        foreach (var file in Directory.EnumerateFiles(directory, Prefix + "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file)[Prefix.Length..];
            if (!DateOnly.TryParseExact(name, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || day >= oldest) continue;

            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
