using System.Security.AccessControl;
using System.Security.Principal;

namespace AgentsTracker.Gateway.Infrastructure.Security;

/// <summary>
/// Закрывает папку данных от других учётных записей: снимает наследование и оставляет
/// полный доступ только владельцу и SYSTEM. Там лежат токен бота, секрет MCP-эндпоинта,
/// правила «всегда» и журнал аудита.
/// </summary>
public static class DataDirectoryAcl
{
    public static void Restrict(string directory, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();

            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Не удалось определить SID текущего пользователя.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            // Сначала свои правила, потом снятие наследования: иначе между шагами папка
            // на мгновение остаётся без единого разрешения.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            // Не смогли ужесточить — работаем с правами по умолчанию, но говорим об этом.
            logger?.LogWarning(ex, "Не удалось ограничить доступ к {Directory}", directory);
        }
    }
}
