using Telegram.Bot;
using Telegram.Bot.Types;

namespace AgentsTracker.Gateway.Infrastructure.Telegram;

/// <summary>
/// Список команд для кнопки «Меню» в Telegram: без него бот выглядит как окно без подсказок.
/// Ошибку публикации глотаем — сам шлюз работает и без опубликованного списка.
/// </summary>
public sealed class BotCommandsCatalog(ITelegramBotClient bot, ILogger<BotCommandsCatalog> logger)
{
    private static readonly BotCommand[] Commands =
    [
        new() { Command = "menu", Description = "настройки: репозиторий, модель, сессии, статистика" },
        new() { Command = "status", Description = "что происходит прямо сейчас" },
        new() { Command = "new", Description = "новая сессия, контекст сбрасывается" },
        new() { Command = "stop", Description = "прервать текущий запуск" },
        new() { Command = "sessions", Description = "переключиться между сессиями" },
        new() { Command = "usage", Description = "расход: запуски, токены, стоимость" },
        new() { Command = "project", Description = "сменить репозиторий" },
        new() { Command = "skills", Description = "скиллы агента кнопками" },
        new() { Command = "model", Description = "сменить модель" },
        new() { Command = "effort", Description = "сколько модели думать" },
        new() { Command = "mode", Description = "режим работы агента" },
        new() { Command = "rules", Description = "разрешения, выданные кнопкой «Всегда»" },
        new() { Command = "audit", Description = "журнал действий: кто, где, что" },
        new() { Command = "help", Description = "справка" },
    ];

    public async Task PublishAsync(CancellationToken ct)
    {
        try
        {
            await bot.SetMyCommands(Commands, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось опубликовать список команд");
        }
    }
}
