using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentsTracker.Channels;

/// <summary>
/// Что хост даёт каналу, не раскрывая своего конфига: общий прокси машины. Канал может
/// перекрыть его своим. Регистрируется хостом до <see cref="IChatChannelModule.AddServices"/>.
/// </summary>
public sealed record ChannelHost(string? Proxy);

/// <summary>Где в конфиге лежат настройки выбранного канала — единый путь для всех модулей.</summary>
public static class ChannelConfiguration
{
    /// <summary>Ключ модуля: <c>Gateway:Channel:Type</c>.</summary>
    public const string TypeKey = "Gateway:Channel:Type";

    /// <summary>Секция настроек канала: <c>Gateway:Channel:Settings</c>. Что в ней — знает только модуль.</summary>
    public const string SettingsSection = "Gateway:Channel:Settings";
}

/// <summary>
/// Модуль канала: регистрирует <see cref="IChatChannel"/> и всё своё, читает настройки из
/// <see cref="ChannelConfiguration.SettingsSection"/>. Хост выбирает один модуль по
/// <see cref="ChannelConfiguration.TypeKey"/> и больше ничего о нём не знает.
/// </summary>
public interface IChatChannelModule
{
    /// <summary>Совпадает с <see cref="IChatChannel.Id"/>.</summary>
    string Id { get; }

    /// <summary>Ключи настроек, которые нельзя хранить открытым текстом (токен, прокси с паролем).</summary>
    IReadOnlyList<string> SecretKeys { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);
}
