using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Question;

/// <summary>/ask &lt;текст&gt; — вопрос вне сессии; без текста — ждать его следующим сообщением.</summary>
public sealed class QuestionCommandHandler(
    IChatChannel channel, QuestionLauncher launcher, IOptions<GatewayOptions> options) : IChatCommandHandler
{
    public IReadOnlyCollection<string> Commands { get; } = ["/ask"];

    public async Task HandleAsync(ChatCommandContext context, CancellationToken ct)
    {
        var reply = context.Argument.Length > 0
            ? launcher.Ask(context.Chat, context.User, context.Argument)
            : Prompt(context.User);

        await channel.SendAsync(context.Chat, new OutgoingMessage(reply, Rich: false), ct);
    }

    private string Prompt(UserId user)
    {
        if (launcher.Refusal is { } refusal) return refusal;

        launcher.Expect(user);
        return Invitation(options.Value.Question.Model);
    }

    /// <summary>Приглашение написать вопрос — одно на команду и кнопку меню.</summary>
    public static string Invitation(string? model) =>
        $"💬 Напишите вопрос следующим сообщением. Ответит {model ?? "модель по умолчанию"} — без сессии и без проекта, " +
        "текущий разговор не затронется. «отмена» — передумать.";
}
