namespace AgentsTracker.Agents.Cursor;

/// <summary>Секция <c>Gateway:Cursor</c> — то, что нужно только этому агенту.</summary>
public sealed class CursorOptions
{
    public const string SectionName = "Gateway:Cursor";

    /// <summary>Путь к <c>agent</c>. null = автопоиск (см. <see cref="CursorCliLocator"/>).</summary>
    public string? Executable { get; set; }

    /// <summary>
    /// Ключ API, если нет <c>agent login</c>. null — CLI берёт уже сохранённый вход
    /// или <c>CURSOR_API_KEY</c> из окружения. В лог не пишем.
    /// </summary>
    public string? ApiKey { get; set; }
}
