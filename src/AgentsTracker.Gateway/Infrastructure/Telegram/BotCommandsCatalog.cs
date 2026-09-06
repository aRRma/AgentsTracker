using Telegram.Bot;
using Telegram.Bot.Types;

namespace AgentsTracker.Gateway.Infrastructure.Telegram;

/// <summary>
/// Список команд для кнопки «Меню» в Telegram: без него бот выглядит как окно без подсказок.
/// Здесь только экраны: /new, /stop, /model, /effort, /mode работают текстом, но в списке
/// их нет — то же самое есть кнопками на экранах «Сессии» и «Агент», а длинный список
/// у кнопки «Меню» хуже короткого. Ошибку публикации глотаем — шлюз работает и без списка.
/// </summary>
public sealed class BotCommandsCatalog(ITelegramBotClient bot, ILogger<BotCommandsCatalog> logger)
{
    /// <summary>Порядок — по частоте: сначала то, что нужно в каждой сессии, потом настройки, в конце журналы.</summary>
    // Эмодзи — в описании: иконок у команд Bot API не даёт, а имя команды — только латиница.
    // Те же значки, что на кнопках экранов, — чтобы список и меню читались как одно.
    private static readonly BotCommand[] Commands =
    [
        new() { Command = "menu", Description = "⚙️ меню" },
        new() { Command = "status", Description = "📟 статус" },
        new() { Command = "sessions", Description = "🧵 сессия" },
        new() { Command = "agent", Description = "🤖 агент" },
        new() { Command = "skills", Description = "🧩 скиллы" },
        new() { Command = "project", Description = "📁 репозиторий" },
        new() { Command = "usage", Description = "📊 лимиты и расход" },
        new() { Command = "rules", Description = "♾ правила «всегда»" },
        new() { Command = "audit", Description = "📜 журнал" },
        new() { Command = "help", Description = "❓ справка" },
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
