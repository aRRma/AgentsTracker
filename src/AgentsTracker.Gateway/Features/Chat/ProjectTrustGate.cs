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

    /// <summary>Чем кончилась проверка доверия.</summary>
    public enum Outcome
    {
        /// <summary>Запускать можно: настроек нет или им доверились.</summary>
        Trusted,

        /// <summary>«Отклонить» или молчание: задача не запущена, пользователю сказано почему.</summary>
        Declined,

        /// <summary>Карточку снял /stop — ответ на него пользователь уже получил.</summary>
        Cancelled,
    }

    public async Task<Outcome> EnsureAsync(ChatId chat, MessageRef? replyTo, string project, CancellationToken ct)
    {
        var name = Path.GetFileName(project.TrimEnd('\\', '/'));

        while (agent.InspectProject(project) is { } settings && !store.IsProjectTrusted(project, settings.Fingerprint))
        {
            var summary = $"{name}: пунктов {settings.Items.Count}, отпечаток {settings.Fingerprint[..8]}";
            logger.LogInformation("Спрашиваю доверие к проекту {Project}: {Items}", project, string.Join("; ", settings.Items));

            // Карточке нужен запуск: по нему брокер находит чат и снимает её по /stop.
            var scope = new RunScope(Guid.NewGuid().ToString("N"), chat, replyTo, project, null);
            using var registration = broker.BeginRun(scope);

            try
            {
                var (key, user) = await broker.AskChoiceAsync(
                    scope, Card(name, settings), [new("trust", "🛡 Доверять"), new("deny", "❌ Отклонить")], ct);

                audit.Write(AuditEvent.Now(AuditKinds.Trust, summary, user, chat, project, outcome: key));
                if (key != "trust")
                {
                    await SayAsync(chat, replyTo,
                        $"Задача не запущена: проекту {name} нет доверия. Уберите из него настройки агента или пришлите задачу снова и нажмите «Доверять».",
                        ct);
                    return Outcome.Declined;
                }

                // Доверие — тому, что человек видел: файлы могли смениться, пока карточка ждала
                // (git pull, правка), и тогда — новая карточка с новым списком.
                if (agent.InspectProject(project)?.Fingerprint == settings.Fingerprint)
                {
                    store.TrustProject(project, settings.Fingerprint);
                    return Outcome.Trusted;
                }

                logger.LogWarning("Настройки агента в проекте {Project} изменились, пока карточка ждала — спрашиваю снова", project);
            }
            catch (TimeoutException)
            {
                audit.Write(AuditEvent.Now(AuditKinds.Trust, summary, null, chat, project, outcome: "timeout"));
                await SayAsync(chat, replyTo, $"Задача не запущена: на вопрос о доверии к проекту {name} не ответили.", ct);
                return Outcome.Declined;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                audit.Write(AuditEvent.Now(AuditKinds.Trust, summary, null, chat, project, outcome: "cancel"));
                return Outcome.Cancelled;
            }
        }

        return Outcome.Trusted;
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
