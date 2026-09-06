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
        new() { Command = "menu", Description = "настройки кнопками: статус, сессии, агент, скиллы" },
        new() { Command = "status", Description = "что происходит и сколько осталось тарифа" },
        new() { Command = "sessions", Description = "сессии проекта: активная, переключение, новая, стоп" },
        new() { Command = "new", Description = "новая сессия, контекст сбрасывается" },
        new() { Command = "stop", Description = "прервать текущий запуск" },
        new() { Command = "model", Description = "агент: модель, effort и режим одним экраном" },
        new() { Command = "effort", Description = "сколько модели думать" },
        new() { Command = "mode", Description = "режим работы агента" },
        new() { Command = "skills", Description = "скиллы агента кнопками" },
        new() { Command = "project", Description = "сменить репозиторий" },
        new() { Command = "usage", Description = "остаток тарифа и расход: запуски, токены" },
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
