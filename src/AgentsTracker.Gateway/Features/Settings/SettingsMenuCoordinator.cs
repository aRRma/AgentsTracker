using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Меню настроек: одно сообщение, которое перерисовывается кнопками. Экраны — отдельные
/// <see cref="ISettingsScreen"/>, координатор только открывает, применяет и перерисовывает.
/// </summary>
public sealed class SettingsMenuCoordinator(
    ITelegramBotClient bot,
    IEnumerable<ISettingsScreen> screens,
    ILogger<SettingsMenuCoordinator> logger)
{
    public const string CallbackPrefix = SettingsKeyboard.CallbackPrefix;

    private readonly Dictionary<string, ISettingsScreen> _screens =
        screens.ToDictionary(s => s.Key, StringComparer.Ordinal);

    /// <summary>Экран по ключу; неизвестный ключ ведёт на корневой.</summary>
    public ISettingsScreen Screen(string key) => _screens.GetValueOrDefault(key) ?? _screens["root"];

    /// <summary>Показывает меню новым сообщением. screen — экран, с которого начать.</summary>
    public async Task OpenAsync(long chatId, long userId, CancellationToken ct, string screen = "root")
    {
        var target = Screen(screen);
        target.Open(userId);
        var (html, keyboard) = await target.RenderAsync(userId, ct);
        await bot.SendMessage(chatId, html, ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
    }

    public async Task HandleCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        var data = query.Data![CallbackPrefix.Length..];
        var separator = data.IndexOf(':');
        var screen = separator < 0 ? data : data[..separator];
        var argument = separator < 0 ? "" : data[(separator + 1)..];

        if (screen == "close")
        {
            await AnswerAsync(query.Id, null, ct);
            if (query.Message is { } closing) await DeleteQuietlyAsync(closing, ct);
            return;
        }

        var userId = query.From.Id;
        var target = Screen(screen);

        // Применяем выбор до отрисовки: экран должен показать уже новое состояние.
        // Нажатие без аргумента — переход на экран из корня: он открывается с начала.
        string? toast = null;
        if (argument.Length > 0) toast = target.Apply(argument, userId, query.Message?.Chat.Id ?? userId);
        else target.Open(userId);

        await AnswerAsync(query.Id, toast, ct);

        if (query.Message is not { } message) return;

        var (html, keyboard) = await target.RenderAsync(userId, ct);
        await EditQuietlyAsync(message, html, keyboard, ct);
    }

    private async Task EditQuietlyAsync(
        Message message, string html, InlineKeyboardMarkup keyboard, CancellationToken ct)
    {
        try
        {
            await bot.EditMessageText(
                message.Chat.Id, message.MessageId, html, ParseMode.Html,
                replyMarkup: keyboard, cancellationToken: ct);
        }
        catch (ApiRequestException ex)
        {
            // «message is not modified» — нормальный исход: пользователь нажал ту же кнопку.
            logger.LogDebug(ex, "Не удалось перерисовать меню");
        }
    }

    private async Task DeleteQuietlyAsync(Message message, CancellationToken ct)
    {
        try
        {
            await bot.DeleteMessage(message.Chat.Id, message.MessageId, ct);
        }
        catch (ApiRequestException ex)
        {
            logger.LogDebug(ex, "Не удалось закрыть меню");
        }
    }

    private async Task AnswerAsync(string callbackQueryId, string? text, CancellationToken ct)
    {
        try
        {
            await bot.AnswerCallbackQuery(callbackQueryId, text, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось ответить на callback меню");
        }
    }
}
