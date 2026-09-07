using System.Runtime.CompilerServices;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Один экран меню настроек: что показать и что сделать по нажатию.</summary>
public interface ISettingsScreen
{
    /// <summary>Ключ экрана в данных кнопки: «root», «status», «sess», «agent», «skills», «proj», «usage».</summary>
    string Key { get; }

    /// <summary>
    /// Отрисовка асинхронная: сводка тянет остаток тарифных окон, а это поход в сеть.
    /// Пользователь нужен экранам со списком — позиция в нём своя у каждого.
    /// </summary>
    Task<(string Html, Keyboard Keyboard)> RenderAsync(UserId user, CancellationToken ct);

    /// <summary>
    /// Кадры отрисовки: координатор показывает первый и правит сообщение на каждом
    /// следующем, паузу держит сам экран. По умолчанию кадр один; несколько нужны
    /// «Статусу» — он так заполняет шкалы лимитов.
    /// </summary>
    async IAsyncEnumerable<(string Html, Keyboard Keyboard)> RenderFramesAsync(
        UserId user, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return await RenderAsync(user, ct);
    }

    /// <summary>
    /// Применяет аргумент нажатия или текстовой команды. Возвращает текст всплывающего
    /// уведомления, null — ничего не применилось. Пользователь нужен аудиту, чат — экрану,
    /// который ставит задачу в очередь: ответ должен прийти в тот же чат.
    /// </summary>
    string? Apply(string argument, UserId user, ChatId chat);

    /// <summary>
    /// Экран открыли заново — из корня или командой. Список сбрасывает позицию: иначе
    /// вместо него показалась бы карточка, открытая в прошлый раз.
    /// </summary>
    void Open(UserId user) { }
}
