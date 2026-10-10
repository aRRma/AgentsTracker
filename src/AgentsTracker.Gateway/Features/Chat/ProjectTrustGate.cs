using System.Text;
using AgentsTracker.Gateway.Features.Approvals;
using AgentsTracker.Gateway.Infrastructure.Audit;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>
/// Доверие к проекту до запуска. Агент без человека за терминалом не спрашивает, доверять ли
/// папке, и выполняет её настройки сам: хук из чужого репозитория запустил бы любую команду
/// без единой карточки подтверждения. Поэтому перед первым запуском в проекте с настройками
/// агента — и после каждой их правки — шлюз показывает, что там лежит, и ждёт решения.
/// </summary>
public sealed class ProjectTrustGate(
    IAgentBackend agent,
    SessionStore store,
    ApprovalBroker broker,
    IChatChannel channel,
    IAuditLog audit,
    ILogger<ProjectTrustGate> logger)
{
    /// <summary>Сколько пунктов показать: длинный список не влез бы в карточку и не читался бы.</summary>
    private const int ItemsShown = 12;

    /// <summary>
    /// true — запускать можно: настроек нет или им уже доверились. false — нет, и пользователь
    /// уже знает почему.
    /// </summary>
    public async Task<bool> EnsureAsync(ChatId chat, MessageRef? replyTo, string project, CancellationToken ct)
    {
        if (agent.InspectProject(project) is not { } settings) return true;
        if (store.IsProjectTrusted(project, settings.Fingerprint)) return true;

        var name = Path.GetFileName(project.TrimEnd('\\', '/'));
        var summary = $"{name}: пунктов {settings.Items.Count}, отпечаток {settings.Fingerprint[..8]}";
        logger.LogInformation("Спрашиваю доверие к проекту {Project}: {Items}", project, Text.Redact(string.Join("; ", settings.Items)));

        // Карточке нужен запуск: по нему брокер находит чат и снимает её по /stop.
        var scope = new RunScope(Guid.NewGuid().ToString("N"), chat, replyTo, project, null);
        using var registration = broker.BeginRun(scope);

        try
        {
            var (key, user) = await broker.AskChoiceAsync(
                scope, Card(name, settings), [new("trust", "🛡 Доверять"), new("deny", "❌ Отклонить")], ct);

            audit.Write(AuditEvent.Now(AuditKinds.Trust, summary, user, chat, project, outcome: key));
            if (key == "trust")
            {
                store.TrustProject(project, settings.Fingerprint);
                return true;
            }

            await SayAsync(chat, replyTo,
                $"Задача не запущена: проекту {name} нет доверия. Уберите из него настройки агента или пришлите задачу снова и нажмите «Доверять».",
                ct);
            return false;
        }
        catch (TimeoutException)
        {
            audit.Write(AuditEvent.Now(AuditKinds.Trust, summary, null, chat, project, outcome: "timeout"));
            await SayAsync(chat, replyTo, $"Задача не запущена: на вопрос о доверии к проекту {name} не ответили. Пришлите её снова.", ct);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // /stop снял карточку — ответ на него пользователь уже получил.
            audit.Write(AuditEvent.Now(AuditKinds.Trust, summary, null, chat, project, outcome: "cancel"));
            return false;
        }
    }

    private string Card(string name, ProjectSettings settings)
    {
        var card = new StringBuilder()
            .Append("🛡 <b>Доверять проекту ").Append(ChatHtml.Escape(name)).Append("?</b>\n\n")
            .Append("В проекте лежат настройки агента. Он выполнит их сам, без карточек подтверждения:\n");

        // Предел по длине, а не только по числу: экранирование раздувает «&» и «<» впятеро, и
        // карточка сверх лимита канала не ушла бы вовсе — задача упала бы с невнятной ошибкой.
        var budget = channel.Limits.MessageLength - 600;
        var shown = 0;
        foreach (var item in settings.Items.Take(ItemsShown))
        {
            var line = $"• <code>{ChatHtml.Escape(item)}</code>\n";
            if (card.Length + line.Length > budget) break;
            card.Append(line);
            shown++;
        }

        if (settings.Items.Count > shown)
            card.Append("• и ещё ").Append(settings.Items.Count - shown).Append('\n');

        return card
            .Append("\nНе узнаёте эти команды или проект не ваш — отклоните. ")
            .Append("Изменятся эти файлы — шлюз спросит снова.")
            .ToString();
    }

    private async Task SayAsync(ChatId chat, MessageRef? replyTo, string text, CancellationToken ct)
    {
        try
        {
            await channel.SendAsync(chat, new OutgoingMessage(text, Rich: false, ReplyTo: replyTo), ct);
        }
        catch (ChannelRequestException ex)
        {
            logger.LogWarning(ex, "Не удалось сказать в чат {Chat}, почему задача не запущена", chat.Key);
        }
    }
}
