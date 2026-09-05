using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Если пользователь нажал «С аргументами» — следующий его текст не промпт, а аргументы
/// скилла. Регистрируется раньше постановки в очередь, иначе текст ушёл бы агентом как есть.
/// </summary>
public sealed class SkillArgumentsTextHandler(ITelegramBotClient bot, SkillLauncher launcher) : ITelegramTextHandler
{
    private static readonly string[] CancelWords = ["отмена", "-", "cancel"];

    public async Task<bool> TryHandleAsync(long chatId, long userId, string text, CancellationToken ct)
    {
        if (!launcher.TryTake(userId, out var command)) return false;

        var trimmed = text.Trim();

        if (CancelWords.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            await bot.SendMessage(chatId, $"Запуск {command} отменён.", cancellationToken: ct);
            return true;
        }

        await bot.SendMessage(chatId, launcher.Launch(chatId, userId, command, trimmed), cancellationToken: ct);
        return true;
    }
}
