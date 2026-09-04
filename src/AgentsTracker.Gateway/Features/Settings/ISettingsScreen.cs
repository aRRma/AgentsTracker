using Telegram.Bot.Types.ReplyMarkups;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Один экран меню настроек: что показать и что сделать по нажатию.</summary>
public interface ISettingsScreen
{
    /// <summary>Ключ экрана в callback_data: «root», «proj», «model», «effort», «mode», «sess», «usage».</summary>
    string Key { get; }

    (string Html, InlineKeyboardMarkup Keyboard) Render();

    /// <summary>
    /// Применяет аргумент нажатия (или текстовой команды). Возвращает короткий текст для
    /// всплывающего уведомления; null — ничего не применено. userId нужен аудиту.
    /// </summary>
    string? Apply(string argument, long userId);
}
