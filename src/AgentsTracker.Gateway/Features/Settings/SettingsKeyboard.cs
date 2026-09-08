using System.Security.Cryptography;
using System.Text;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Общие кирпичики экранов меню: кнопки, маркер выбора, экранирование, страницы, ключи.</summary>
internal static class SettingsKeyboard
{
    /// <summary>
    /// Префикс данных кнопок меню. У карточек подтверждений формат «id:ключ» с восьмизначным
    /// hex-id, так что перепутать нельзя.
    /// </summary>
    public const string CallbackPrefix = "cfg:";

    /// <summary>Строк на странице списка: больше — и сообщение с клавиатурой уже не прочитать.</summary>
    public const int PageSize = 12;

    public static KeyboardButton Button(string label, string data) => new(label, CallbackPrefix + data);

    public static KeyboardButton BackButton => Button("◀️ Назад", "root");

    public static string Marker(bool selected) => selected ? "▶" : "·";

    public static string E(string text) => ChatHtml.Escape(text);

    /// <summary>
    /// Короткий ключ значения для данных кнопки: канал ограничивает их длину
    /// (<see cref="ChannelLimits.ButtonDataBytes"/>, у Telegram 64 байта), и полный путь
    /// или команда плагина туда не влезут. Регистр не учитываем — как и ProjectCatalog
    /// в путях. Ключ всегда 12 hex-символов, поэтому однобуквенные префиксы экранов
    /// не должны быть hex.
    /// </summary>
    public static string Key12(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..12];

    /// <summary>
    /// Текущая страница списка, счётчик для текста и ряд кнопок перелистывания (null, если
    /// страница одна). Номер зажимаем в границы: список мог укоротиться после отрисовки.
    /// </summary>
    public static (T[] Items, int Page, string Counter, KeyboardButton[]? PageRow) Page<T>(
        IReadOnlyList<T> all, int page, string screen, string pagePrefix, string noun)
    {
        var pages = Math.Max(1, (all.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);

        var items = all.Skip(page * PageSize).Take(PageSize).ToArray();
        if (pages == 1) return (items, page, "", null);

        var counter = $"{Environment.NewLine}{Environment.NewLine}Страница {page + 1} из {pages}, всего {noun}: {all.Count}.";

        KeyboardButton[] row =
        [
            Button("◀", $"{screen}:{pagePrefix}{(page - 1 + pages) % pages}"),
            Button($"{page + 1}/{pages}", $"{screen}:{pagePrefix}{page}"),
            Button("▶", $"{screen}:{pagePrefix}{(page + 1) % pages}"),
        ];

        return (items, page, counter, row);
    }
}
