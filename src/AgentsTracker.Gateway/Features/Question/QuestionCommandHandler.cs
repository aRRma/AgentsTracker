using AgentsTracker.Gateway.Features.Settings;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Question;

/// <summary>/ask &lt;текст&gt; — вопрос вне сессии; без текста — ждать его следующим сообщением.</summary>
public sealed class QuestionCommandHandler(
    IChatChannel channel, QuestionLauncher launcher, SkillLauncher skills, IOptions<GatewayOptions> options)
    : IChatCommandHandler
{
    public IReadOnlyCollection<string> Commands { get; } = ["/ask"];

    public async Task HandleAsync(ChatCommandContext context, CancellationToken ct)
    {
        if (context.Argument.Length == 0)
        {
            await channel.SendAsync(context.Chat, new OutgoingMessage(Prompt(context.Chat), Rich: false), ct);
            return;
        }

        var accepted = launcher.Ask(context.Chat, context.User, context.Argument, context.Message);
        await channel.SendAsync(context.Chat, new OutgoingMessage(accepted, Rich: false, ReplyTo: context.Message), ct);
    }

    private string Prompt(ChatId chat)
    {
        if (launcher.Refusal is { } refusal) return refusal;

        // Как и у кнопки «Задать вопрос»: иначе следующий текст мог достаться ожиданию аргументов
        // скилла (тот в цепочке раньше) — вопрос пропал бы, а скилл ушёл бы с ним аргументом.
        skills.Cancel(chat);
        launcher.Expect(chat);
        return Invitation(options.Value.Question.Model);
    }

    /// <summary>Приглашение написать вопрос — одно на команду и кнопки меню.</summary>
    public static string Invitation(string? model) =>
        $"💬 Напишите вопрос следующим сообщением. Ответит {model ?? "модель по умолчанию"} — без сессии и без проекта, " +
        "текущая сессия не затронется. «отмена» — передумать.";
}
