namespace AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

public static class ChatChannelExtensions
{
    /// <summary>
    /// Простой текст без разметки. Одно место вместо копий `new OutgoingMessage(text, Rich: false)`:
    /// забытый <c>Rich: false</c> отдал бы пользовательский текст в разбор разметки.
    /// </summary>
    public static Task<MessageRef> SayAsync(this IChatChannel channel, ChatId chat, string text, CancellationToken ct,
        MessageRef? replyTo = null) =>
        channel.SendAsync(chat, new OutgoingMessage(text, Rich: false, ReplyTo: replyTo), ct);
}
