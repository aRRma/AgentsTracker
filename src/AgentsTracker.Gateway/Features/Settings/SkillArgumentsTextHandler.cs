using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Если пользователь нажал «С аргументами» — следующий его текст не промпт, а аргументы
/// скилла. Регистрируется раньше постановки в очередь, иначе текст ушёл бы агентом как есть.
/// </summary>
public sealed class SkillArgumentsTextHandler(IChatChannel channel, SkillLauncher launcher) : IChatTextHandler
{
    private static readonly string[] CancelWords = ["отмена", "-", "cancel"];

    public async Task<bool> TryHandleAsync(IncomingMessage message, CancellationToken ct)
    {
        var (chat, user) = (message.Chat, message.User);

        if (!launcher.TryTake(user, out var command)) return false;

        var trimmed = message.Text.Trim();

        if (CancelWords.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            await channel.SendAsync(chat, new OutgoingMessage($"Запуск {command} отменён.", Rich: false), ct);
            return true;
        }

        await channel.SendAsync(chat, new OutgoingMessage(launcher.Launch(chat, user, command, trimmed), Rich: false), ct);
        return true;
    }
}
