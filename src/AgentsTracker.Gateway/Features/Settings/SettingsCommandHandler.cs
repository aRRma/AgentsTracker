using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Команды, открывающие меню, и их текстовые формы: <c>/mode plan</c>, <c>/model sonnet</c>,
/// <c>/effort high</c> применяются через тот же экран, что и кнопка, — логика одна.
/// </summary>
public sealed class SettingsCommandHandler(
    ITelegramBotClient bot,
    SettingsMenuCoordinator menu,
    SessionStore store,
    ChatWorker worker,
    IOptions<GatewayOptions> options) : ITelegramCommandHandler
{
    public IReadOnlyCollection<string> Commands { get; } =
        ["/menu", "/settings", "/project", "/sessions", "/usage", "/skills", "/model", "/effort", "/mode"];

    public async Task HandleAsync(TelegramCommandContext context, CancellationToken ct)
    {
        var (chatId, userId, command, argument) = context;

        switch (command)
        {
            case "/menu" or "/settings":
                await menu.OpenAsync(chatId, userId, ct);
                break;

            case "/project":
                await menu.OpenAsync(chatId, userId, ct, "proj");
                break;

            case "/sessions":
                await menu.OpenAsync(chatId, userId, ct, "sess");
                break;

            case "/usage":
                await menu.OpenAsync(chatId, userId, ct, "usage");
                break;

            case "/skills":
                await menu.OpenAsync(chatId, userId, ct, "skills");
                break;

            case "/effort" when argument.Length == 0:
                await menu.OpenAsync(chatId, userId, ct, "effort");
                break;

            case "/effort":
                await bot.SendMessage(chatId, ChangeEffort(argument, userId, chatId), cancellationToken: ct);
                break;

            case "/model" when argument.Length == 0:
                await bot.SendMessage(chatId,
                    $"Текущая модель: {store.EffectiveModel ?? "по умолчанию"}\nЗадать: /model sonnet | opus | haiku | reset",
                    cancellationToken: ct);
                break;

            case "/model":
                await bot.SendMessage(chatId, menu.Screen("model").Apply(argument, userId, chatId) ?? "", cancellationToken: ct);
                break;

            case "/mode" when argument.Length == 0:
                await bot.SendMessage(chatId, DescribeMode(), cancellationToken: ct);
                break;

            case "/mode":
                await bot.SendMessage(chatId, ChangeMode(argument, userId, chatId), cancellationToken: ct);
                break;
        }
    }

    /// <summary>Меняет уровень усилий модели. Применяется со следующего запуска.</summary>
    private string ChangeEffort(string argument, long userId, long chatId)
    {
        if (!argument.Equals("reset", StringComparison.OrdinalIgnoreCase) && EffortLevels.Resolve(argument) is null)
            return $"Не знаю уровень «{argument}». Доступно: {string.Join(", ", EffortLevels.All)}, reset.";

        menu.Screen("effort").Apply(argument, userId, chatId);

        // Именно выбранный из чата уровень, а не действующий: после reset он null,
        // и ответ должен говорить про конфиг, а не повторять его значение как выбранное.
        return store.Effort is { } level
            ? $"🎚 {EffortLevels.Describe(level)}. Применится со следующего запуска."
            : $"🎚 Effort: {options.Value.Effort ?? "по умолчанию"} — как в конфиге.";
    }

    private string DescribeMode()
    {
        var current = store.EffectivePermissionMode;
        var list = string.Join(
            '\n',
            PermissionModes.Selectable.Select(m =>
                $"{(m == current ? "▶" : "·")} {PermissionModes.Describe(m)}"));

        return $"""
            Уровень доступа: {current}

            {list}

            Сменить: /mode plan|default|acceptEdits|auto
            Вернуть значение из конфига ({options.Value.PermissionMode}): /mode reset
            """;
    }

    /// <summary>
    /// Меняет уровень доступа агента к машине. Новый режим ложится в state.json
    /// и переживает перезапуск; текущий запуск доигрывает со старым.
    /// </summary>
    private string ChangeMode(string argument, long userId, long chatId)
    {
        if (argument.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            menu.Screen("mode").Apply(argument, userId, chatId);
            return $"Уровень доступа: {options.Value.PermissionMode} — как в конфиге. Применится со следующего запуска.";
        }

        var mode = PermissionModes.Resolve(argument);

        if (mode is null || !PermissionModes.Selectable.Contains(mode, StringComparer.Ordinal))
        {
            // dontAsk и bypassPermissions сюда не попадают намеренно: полное снятие
            // подтверждений остаётся правкой конфига на самой машине.
            return $"""
                Не знаю уровень «{argument}». Доступно: {string.Join(", ", PermissionModes.Selectable)}, reset.
                Снять подтверждения полностью можно только в appsettings.Local.json на самой машине.
                """;
        }

        if (mode == store.EffectivePermissionMode) return $"Уже {PermissionModes.Describe(mode)}.";

        menu.Screen("mode").Apply(mode, userId, chatId);

        // Занятость спрашиваем у воркера, а не угадываем по тексту тоста экрана.
        var note = worker.IsBusy ? "\nТекущий запуск доигрывает со старым уровнем." : "";
        return $"🔐 {PermissionModes.Describe(mode)}.\nПрименится со следующего запуска.{note}";
    }
}
