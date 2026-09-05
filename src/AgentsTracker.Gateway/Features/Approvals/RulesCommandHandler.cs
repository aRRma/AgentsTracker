using System.Text;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>/rules — показывает и снимает разрешения, выданные кнопкой «Всегда».</summary>
public sealed class RulesCommandHandler(ITelegramBotClient bot, SessionStore store, IAuditLog audit)
    : ITelegramCommandHandler
{
    /// <summary>Бюджет списка правил в символах и предел длины одного правила.</summary>
    private const int RuleListBudget = 3500;
    private const int RuleDisplayLimit = 200;

    // Правила, которые по «Всегда» записал сам агент, живут в его настройках проекта
    // (у Claude Code — .claude/settings.local.json) — шлюз их не видит и снять не может.
    private const string CliNote = "\n\nПравила, записанные самим агентом (когда карточка показывала «запишет в "
        + "настройки проекта у агента»), правятся в его файле настроек в папке проекта.";

    public IReadOnlyCollection<string> Commands { get; } = ["/rules"];

    public async Task HandleAsync(TelegramCommandContext context, CancellationToken ct) =>
        await bot.SendMessage(context.ChatId, Manage(context), cancellationToken: ct);

    private string Manage(TelegramCommandContext context)
    {
        var argument = context.Argument;
        var rules = store.AlwaysAllowRules();
        var project = Path.GetFileName(store.ProjectPath);

        if (argument.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            var removed = store.ClearAlwaysAllow();
            if (removed > 0) Audit(context, $"clear ({removed})");
            return removed == 0
                ? $"Правил «всегда» в {project} и не было."
                : $"♾ Снято правил в {project}: {removed}. Теперь всё снова спрашивается кнопками.";
        }

        if (argument.StartsWith("del", StringComparison.OrdinalIgnoreCase))
        {
            var number = argument[3..].Trim();

            if (!int.TryParse(number, out var index) || index < 1 || index > rules.Count)
                return $"Нужен номер правила из списка /rules (1..{rules.Count}).";

            var signature = rules[index - 1];
            store.RemoveAlwaysAllow(signature);
            Audit(context, $"del {signature}");
            return $"♾ Снято: {Shorten(signature)}";
        }

        if (rules.Count == 0)
            return $"Правил «всегда» для {project} нет — каждое действие спрашивается кнопками." + CliNote;

        // Правил может накопиться сколько угодно, а сообщение Telegram ограничено:
        // набираем список по бюджету, остаток показываем числом.
        var list = new StringBuilder();
        var shown = 0;

        foreach (var rule in rules)
        {
            var line = $"{shown + 1}. {Shorten(rule)}";
            if (list.Length + line.Length + 1 > RuleListBudget) break;

            if (shown > 0) list.Append('\n');
            list.Append(line);
            shown++;
        }

        var tail = shown < rules.Count ? $"\n…и ещё {rules.Count - shown}." : "";

        // Правила у каждого проекта свои: заголовок говорит, чьи это.
        return $"""
            Разрешено без вопросов в {project} ({rules.Count}):
            {list}{tail}

            Снять: /rules del <номер> | /rules clear
            """ + CliNote;
    }

    private void Audit(TelegramCommandContext context, string summary) =>
        audit.Write(AuditEvent.Now(AuditKinds.Rules, summary, context.UserId, context.ChatId, store.ProjectPath, outcome: "gateway"));

    private static string Shorten(string text) => Text.Clip(text, RuleDisplayLimit);
}
