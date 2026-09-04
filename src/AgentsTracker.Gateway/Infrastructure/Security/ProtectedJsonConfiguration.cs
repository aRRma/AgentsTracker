using Microsoft.Extensions.Configuration.Json;

namespace AgentsTracker.Gateway.Infrastructure.Security;

/// <summary>
/// JSON-конфиг, в котором значения вида <c>dpapi:…</c> расшифровываются при загрузке.
/// Открытые значения читаются как есть — так файл можно заполнить руками, а потом
/// зашифровать командой <c>protect-secrets</c>.
/// </summary>
public sealed class ProtectedJsonConfigurationSource : JsonConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new ProtectedJsonConfigurationProvider(this);
    }
}

public sealed class ProtectedJsonConfigurationProvider(ProtectedJsonConfigurationSource source)
    : JsonConfigurationProvider(source)
{
    public override void Load()
    {
        base.Load();

        foreach (var key in Data.Keys.ToArray())
        {
            if (!SecretsProtector.IsProtected(Data[key])) continue;

            try
            {
                Data[key] = SecretsProtector.Unprotect(Data[key]!);
            }
            catch (Exception ex)
            {
                // Значение зашифровано другим пользователем или на другой машине: оставить его
                // как есть — значит подсунуть в BotToken мусор; лучше упасть на валидации с понятной причиной.
                throw new InvalidOperationException(
                    $"Не удалось расшифровать «{key}» из {Source.Path}: {ex.Message}. " +
                    "Значение зашифровано DPAPI под другой учётной записью — перезапишите его открытым текстом и выполните protect-secrets.", ex);
            }
        }
    }
}

public static class ProtectedJsonConfigurationExtensions
{
    public static IConfigurationBuilder AddProtectedJsonFile(this IConfigurationBuilder builder, string path, bool optional)
    {
        var source = new ProtectedJsonConfigurationSource { Path = path, Optional = optional, ReloadOnChange = false };
        // Абсолютный путь (папка данных) требует своего провайдера файлов, не корня приложения.
        source.ResolveFileProvider();
        return builder.Add(source);
    }
}
