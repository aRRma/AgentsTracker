using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Если пользователь нажал «С аргументами» — следующий его текст не промпт, а аргументы
/// скилла. Регистрируется раньше постановки в очередь, иначе текст ушёл бы агентом как есть.
/// </summary>
public sealed class SkillArgumentsTextHandler(IChatChannel channel, SkillLauncher launcher) : IChatTextHandler
{
    public async Task<bool> TryHandleAsync(IncomingMessage message, CancellationToken ct)
    {
        var (chat, user) = (message.Chat, message.User);

        // Сообщение с вложением аргументами скилла не считаем: скилл запустился бы (без
        // подписи — вовсе без аргументов), а картинка пропала бы молча. Ожидание аргументов
        // остаётся — их пришлют следующим сообщением.
        if (message.Attachments.Count > 0) return false;

        if (!launcher.TryTake(chat, out var command)) return false;

        var trimmed = message.Text.Trim();

        if (CancelWords.Matches(trimmed))
        {
            await channel.SayAsync(chat, $"Запуск {command} отменён.", ct);
            return true;
        }

        await channel.SayAsync(chat, launcher.Launch(chat, user, command, trimmed), ct);
        return true;
    }
}
