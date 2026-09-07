using System.Runtime.CompilerServices;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Один экран меню настроек: что показать и что сделать по нажатию.</summary>
public interface ISettingsScreen
{
    /// <summary>Ключ экрана в данных кнопки: «root», «status», «sess», «agent», «skills», «proj», «usage».</summary>
    string Key { get; }

    /// <summary>
    /// Отрисовка асинхронная: сводка тянет остаток тарифных окон, а это поход в сеть
    /// (с кэшем на три минуты, но всё же). Пользователь — для экранов с позицией в списке:
    /// у каждого своя страница.
    /// </summary>
    Task<(string Html, Keyboard Keyboard)> RenderAsync(UserId user, CancellationToken ct);

    /// <summary>
    /// Кадры отрисовки: координатор показывает первый и правит сообщение на каждом следующем.
    /// Паузу между кадрами держит сам экран. По умолчанию кадр один — обычный экран;
    /// «Статус» заполняет шкалы лимитов за несколько кадров.
    /// </summary>
    async IAsyncEnumerable<(string Html, Keyboard Keyboard)> RenderFramesAsync(
        UserId user, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return await RenderAsync(user, ct);
    }

    /// <summary>
    /// Применяет аргумент нажатия (или текстовой команды). Возвращает короткий текст для
    /// всплывающего уведомления; null — ничего не применено. Пользователь нужен аудиту, чат —
    /// экрану, который ставит задачу в очередь агента: ответ должен прийти в тот же чат.
    /// </summary>
    string? Apply(string argument, UserId user, ChatId chat);

    /// <summary>
    /// Экран открыт заново — из корня или командой. Экран со списком сбрасывает позицию:
    /// иначе вместо списка показалась бы карточка, открытая в прошлый раз.
    /// </summary>
    void Open(UserId user) { }
}
