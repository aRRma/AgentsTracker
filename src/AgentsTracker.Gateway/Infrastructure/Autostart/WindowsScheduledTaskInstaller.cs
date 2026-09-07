using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace AgentsTracker.Gateway.Infrastructure.Autostart;

/// <summary>
/// Автозапуск на Windows — задача Планировщика через <c>schtasks</c> с XML. От текущего
/// пользователя и без повышения прав: агент читает OAuth-логин из
/// <c>%USERPROFILE%\.claude</c>, а под SYSTEM его там нет — поэтому не служба.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScheduledTaskInstaller : ProcessAutostartInstaller, IAutostartInstaller
{
    private const string SchTasks = "schtasks.exe";

    public override string Kind => "задача Планировщика";

    public void Install(AutostartRequest request)
    {
        // Именно UTF-16: UTF-8 schtasks тоже примет, но кириллица в описании задачи
        // превратится в кракозябры.
        var file = Path.Combine(Path.GetTempPath(), $"agentstracker-task-{Environment.ProcessId}.xml");
        File.WriteAllText(file, BuildXml(request), Encoding.Unicode);

        try
        {
            // /F перезаписывает одноимённую: обновление версии — тот же install.
            EnsureOk(Run(SchTasks, "/Create", "/XML", file, "/TN", request.Name, "/F"),
                $"Не удалось создать задачу «{request.Name}»");
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { /* временный файл, не повод падать */ }
        }
    }

    public bool Uninstall(string name)
    {
        if (!Exists(name)) return false;

        Stop(name);
        EnsureOk(Run(SchTasks, "/Delete", "/TN", name, "/F"), $"Не удалось удалить задачу «{name}»");
        return true;
    }

    public bool Exists(string name) => Run(SchTasks, "/Query", "/TN", name).ExitCode == 0;

    public void Start(string name) =>
        EnsureOk(Run(SchTasks, "/Run", "/TN", name), $"Не удалось запустить задачу «{name}»");

    /// <summary>
    /// Останавливает запущенный экземпляр. Незапущенная задача не ошибка: <c>/End</c> для
    /// неё вернёт ненулевой код, а вызывающему важно лишь, что процесса больше нет.
    /// </summary>
    public void Stop(string name) => Run(SchTasks, "/End", "/TN", name);

    private static string BuildXml(AutostartRequest request)
    {
        var user = WindowsIdentity.GetCurrent().Name;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{X(request.Description)}</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{X(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{X(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{X(request.ExecutablePath)}</Command>
                  <WorkingDirectory>{X(request.WorkingDirectory)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Экранирование для XML: в путях и описании попадаются &amp; и кавычки.</summary>
    private static string X(string value) => SecurityElement.Escape(value) ?? "";
}
