using Telegram.Bot.Types.ReplyMarkups;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Один экран меню настроек: что показать и что сделать по нажатию.</summary>
public interface ISettingsScreen
{
    /// <summary>Ключ экрана в callback_data: «root», «proj», «model», «effort», «mode», «sess», «usage», «skills».</summary>
    string Key { get; }

    /// <summary>
    /// Отрисовка асинхронная: сводка тянет остаток тарифных окон, а это поход в сеть
    /// (с кэшем на три минуты, но всё же).
    /// </summary>
    Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(CancellationToken ct);

    /// <summary>
    /// Применяет аргумент нажатия (или текстовой команды). Возвращает короткий текст для
    /// всплывающего уведомления; null — ничего не применено. userId нужен аудиту, chatId — экрану,
    /// который ставит задачу в очередь агента: ответ должен прийти в тот же чат.
    /// </summary>
    string? Apply(string argument, long userId, long chatId);
}
