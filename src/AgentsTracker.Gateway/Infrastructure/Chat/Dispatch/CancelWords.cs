namespace AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

/// <summary>
/// Слова, которыми человек отказывается от ожидаемого ввода (вопрос, аргументы скилла).
/// Один список: два обработчика с разными словами оставили бы «отмена» работающей лишь в одном из них.
/// </summary>
public static class CancelWords
{
    private static readonly string[] Words = ["отмена", "-", "cancel"];

    public static bool Matches(string text) => Words.Contains(text, StringComparer.OrdinalIgnoreCase);
}
