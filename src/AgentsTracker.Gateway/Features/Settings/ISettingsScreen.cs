using System.Runtime.CompilerServices;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Один экран меню настроек: что показать и что сделать по нажатию.</summary>
public interface ISettingsScreen
{
    /// <summary>Ключ экрана в callback_data: «root», «status», «sess», «agent», «skills», «proj», «usage».</summary>
    string Key { get; }

    /// <summary>
    /// Отрисовка асинхронная: сводка тянет остаток тарифных окон, а это поход в сеть
    /// (с кэшем на три минуты, но всё же). userId — для экранов с позицией в списке:
    /// у каждого пользователя своя страница.
    /// </summary>
    Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(long userId, CancellationToken ct);

    /// <summary>
    /// Кадры отрисовки: координатор показывает первый и правит сообщение на каждом следующем.
    /// Паузу между кадрами держит сам экран. По умолчанию кадр один — обычный экран;
    /// «Статус» заполняет шкалы лимитов за несколько кадров.
    /// </summary>
    async IAsyncEnumerable<(string Html, InlineKeyboardMarkup Keyboard)> RenderFramesAsync(
        long userId, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return await RenderAsync(userId, ct);
    }

    /// <summary>
    /// Применяет аргумент нажатия (или текстовой команды). Возвращает короткий текст для
    /// всплывающего уведомления; null — ничего не применено. userId нужен аудиту, chatId — экрану,
    /// который ставит задачу в очередь агента: ответ должен прийти в тот же чат.
    /// </summary>
    string? Apply(string argument, long userId, long chatId);

    /// <summary>
    /// Экран открыт заново — из корня или командой. Экран со списком сбрасывает позицию:
    /// иначе вместо списка показалась бы карточка, открытая в прошлый раз.
    /// </summary>
    void Open(long userId) { }
}
