using Telegram.Bot;
using Telegram.Bot.Types;

namespace AgentsTracker.Gateway.Infrastructure.Telegram;

/// <summary>
/// Список команд для кнопки «Меню» в Telegram: без него бот выглядит как окно без подсказок.
/// Ошибку публикации глотаем — сам шлюз работает и без опубликованного списка.
/// </summary>
public sealed class BotCommandsCatalog(ITelegramBotClient bot, ILogger<BotCommandsCatalog> logger)
{
    /// <summary>Порядок — по частоте: сначала то, что нужно в каждой сессии, потом настройки, в конце журналы.</summary>
    private static readonly BotCommand[] Commands =
    [
        new() { Command = "menu", Description = "меню" },
        new() { Command = "status", Description = "статус" },
        new() { Command = "sessions", Description = "сессии" },
        new() { Command = "new", Description = "новая сессия" },
        new() { Command = "stop", Description = "стоп" },
        new() { Command = "model", Description = "модель, effort, режим" },
        new() { Command = "effort", Description = "effort" },
        new() { Command = "mode", Description = "режим" },
        new() { Command = "skills", Description = "скиллы" },
        new() { Command = "project", Description = "репозиторий" },
        new() { Command = "usage", Description = "лимиты и расход" },
        new() { Command = "rules", Description = "правила «всегда»" },
        new() { Command = "audit", Description = "журнал" },
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
