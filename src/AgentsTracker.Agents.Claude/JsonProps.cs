using System.Text.Json;

namespace AgentsTracker.Agents.Claude;

/// <summary>Общие приёмы чтения чужого JSON: файлы CLI и его поток не должны ронять разбор исключением.</summary>
internal static class JsonProps
{
    /// <summary>
    /// Так CLI читает свои файлы настроек: с комментариями и хвостовыми запятыми.
    /// Строгий разбор отверг бы файл, который сам CLI принимает.
    /// </summary>
    public static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Строковое поле объекта или null: поле другого типа не должно давать исключение <c>GetString</c>.</summary>
    public static string? String(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
