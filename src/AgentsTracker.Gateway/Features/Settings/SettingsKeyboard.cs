using System.Security.Cryptography;
using System.Text;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Общие кирпичики экранов меню: кнопки, маркер выбора, экранирование, страницы, ключи.</summary>
internal static class SettingsKeyboard
{
    /// <summary>
    /// Префикс callback_data меню. Карточки подтверждений используют «id:ключ» с восьмизначным
    /// hex-id, так что перепутать их нельзя — но разбирает их всё равно другой обработчик.
    /// </summary>
    public const string CallbackPrefix = "cfg:";

    /// <summary>Сколько строк показывать на странице списка, чтобы сообщение и клавиатура остались читаемыми.</summary>
    public const int PageSize = 12;

    public static InlineKeyboardButton Button(string label, string data) =>
        InlineKeyboardButton.WithCallbackData(label, CallbackPrefix + data);

    public static InlineKeyboardButton BackButton => Button("◀️ Назад", "root");

    public static string Marker(bool selected) => selected ? "▶" : "·";

    public static string E(string text) => TelegramFormatter.Escape(text);

    /// <summary>
    /// Короткий ключ значения для callback_data (лимит 64 байта: полный путь или команда
    /// плагина не влезают). Регистр не учитывается — как и ProjectCatalog при сравнении путей.
    /// Ключ всегда 12 hex-символов, поэтому однобуквенные префиксы экранов должны быть не hex.
    /// </summary>
    public static string Key12(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..12];

    /// <summary>
    /// Текущая страница списка, счётчик для текста и ряд кнопок перелистывания (нет — если
    /// страница одна). Номер зажимается в границы: список мог укоротиться после отрисовки.
    /// </summary>
    public static (T[] Items, int Page, string Counter, InlineKeyboardButton[]? PageRow) Page<T>(
        IReadOnlyList<T> all, int page, string screen, string pagePrefix, string noun)
    {
        var pages = Math.Max(1, (all.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);

        var items = all.Skip(page * PageSize).Take(PageSize).ToArray();
        if (pages == 1) return (items, page, "", null);

        var counter = $"{Environment.NewLine}{Environment.NewLine}Страница {page + 1} из {pages}, всего {noun}: {all.Count}.";

        InlineKeyboardButton[] row =
        [
            Button("◀", $"{screen}:{pagePrefix}{(page - 1 + pages) % pages}"),
            Button($"{page + 1}/{pages}", $"{screen}:{pagePrefix}{page}"),
            Button("▶", $"{screen}:{pagePrefix}{(page + 1) % pages}"),
        ];

        return (items, page, counter, row);
    }
}
