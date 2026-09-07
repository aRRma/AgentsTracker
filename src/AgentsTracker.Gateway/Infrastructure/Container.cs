namespace AgentsTracker.Gateway.Infrastructure;

/// <summary>
/// Запущены ли мы в контейнере. Влияет на две вещи: автозапуск там задаёт политика
/// перезапуска Docker, а монитор бесполезно слушать на loopback — наружу его не опубликовать.
/// </summary>
public static class Container
{
    /// <summary>
    /// Переменную ставят базовые образы Microsoft, файл — docker и podman. Проверяем оба:
    /// образ могли собрать не от базового, а переменную — потерять.
    /// </summary>
    public static bool Detected { get; } =
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") is "true" or "1"
        || File.Exists("/.dockerenv");
}
