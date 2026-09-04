using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Общие кирпичики экранов меню: кнопки, маркер выбора, экранирование.</summary>
internal static class SettingsKeyboard
{
    /// <summary>
    /// Префикс callback_data меню. Карточки подтверждений используют «id:ключ» с восьмизначным
    /// hex-id, так что перепутать их нельзя — но разбирает их всё равно другой обработчик.
    /// </summary>
    public const string CallbackPrefix = "cfg:";

    public static InlineKeyboardButton Button(string label, string data) =>
        InlineKeyboardButton.WithCallbackData(label, CallbackPrefix + data);

    public static InlineKeyboardButton BackButton => Button("◀️ Назад", "root");

    public static string Marker(bool selected) => selected ? "▶" : "·";

    public static string E(string text) => TelegramFormatter.Escape(text);
}
