namespace AgentsTracker.Gateway.Infrastructure.Autostart;

/// <summary>Что зарегистрировать в автозапуске операционной системы.</summary>
/// <param name="Name">Системное имя записи: имя задачи, службы или ярлыка.</param>
/// <param name="Description">Описание — его показывает оснастка ОС.</param>
/// <param name="ExecutablePath">Полный путь к исполняемому файлу.</param>
/// <param name="WorkingDirectory">Рабочая папка процесса.</param>
public sealed record AutostartRequest(
    string Name,
    string Description,
    string ExecutablePath,
    string WorkingDirectory);

/// <summary>
/// Автозапуск средствами ОС. Способ у каждой свой, но приём один: положить файл-описание
/// и позвать штатную утилиту — <c>schtasks</c>, <c>launchctl</c>, <c>systemctl --user</c>.
/// Поэтому интерфейс не знает ни про задачи Планировщика, ни про unit-файлы, а реализации
/// не тянут зависимостей и не мешают self-contained публикации.
/// </summary>
public interface IAutostartInstaller
{
    /// <summary>Как это называется в этой ОС: «задача Планировщика», «агент launchd».</summary>
    string Kind { get; }

    /// <summary>Регистрирует запись, перезаписывая одноимённую: обновление — тот же install.</summary>
    void Install(AutostartRequest request);

    /// <summary>Снимает регистрацию. false — записи и не было.</summary>
    bool Uninstall(string name);

    /// <summary>Есть ли такая запись.</summary>
    bool Exists(string name);

    /// <summary>Запускает зарегистрированное прямо сейчас.</summary>
    void Start(string name);

    /// <summary>Останавливает запущенное, если оно работает.</summary>
    void Stop(string name);
}
