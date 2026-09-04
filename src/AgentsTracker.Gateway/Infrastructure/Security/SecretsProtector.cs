using System.Security.Cryptography;
using System.Text;

namespace AgentsTracker.Gateway.Infrastructure.Security;

/// <summary>
/// Шифрование значений конфига через DPAPI в области текущего пользователя: расшифровать
/// сможет только процесс под той же учётной записью на той же машине. Этого достаточно —
/// шлюз и задача Планировщика запускаются от интерактивного пользователя.
/// Зашифрованное значение хранится как <c>dpapi:&lt;base64&gt;</c>.
/// </summary>
public static class SecretsProtector
{
    public const string Prefix = "dpapi:";

    public static bool IsProtected(string? value) =>
        value is not null && value.Length > Prefix.Length && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plain)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI доступен только на Windows.");

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string stored)
    {
        if (!IsProtected(stored)) return stored;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI доступен только на Windows.");

        var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
    }
}
