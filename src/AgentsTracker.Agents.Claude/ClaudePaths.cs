namespace AgentsTracker.Agents.Claude;

/// <summary>Папка <c>~/.claude</c> в одном месте: чтобы каталоги, настройки и токен не разошлись в расчёте пути.</summary>
internal static class ClaudePaths
{
    public static readonly string Home =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string Credentials => Path.Combine(Home, ".credentials.json");
}
