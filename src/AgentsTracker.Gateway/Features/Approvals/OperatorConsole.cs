using System.Text;
using System.Text.Json;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Monitoring;
using AgentsTracker.Gateway.Infrastructure.Telegram;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Человек в Telegram глазами бэкенда: карточка запроса разрешения с кнопками, правила
/// «всегда», вопросы агента с вариантами — и аудит каждого решения. Бэкенд получает
/// только решение; как оно добыто, в каком чате и кем, знает лишь хост.
/// </summary>
public sealed class OperatorConsole(
    ApprovalBroker broker,
    SessionStore store,
    IAuditLog audit,
    RunMonitor monitor,
    ILogger<OperatorConsole> logger) : IOperatorConsole
{
    // Бюджеты в символах уже экранированного HTML: сумма с запасом влезает в лимит
    // сообщения Telegram (4096), даже если текст целиком состоит из «&».
    private const int HeaderBudget = 200;
    private const int QuestionBudget = 1000;
    private const int OptionLabelBudget = 120;
    private const int OptionDescriptionBudget = 300;

    /// <summary>Предел длины подписи на инлайн-кнопке Telegram.</summary>
    private const int ButtonLabelLimit = 64;

    public async Task<ApprovalDecision> ApproveAsync(ApprovalRequest request, CancellationToken ct)
    {
        var (toolName, input, suggested) = request;
        var signature = BuildSignature(toolName, input);

        // Только начало: полная команда может нести токен в заголовке curl, ему в логе не место.
        var brief = Text.Preview(Highlight(toolName, input) ?? "");
        logger.LogInformation("Запрос разрешения: {Tool} {Key}", toolName, brief);

        // Монитору — та же короткая строка, что и логу: полный ввод Edit/Write — содержимое файлов.
        using var pending = monitor.Approval(toolName, brief);

        if (store.IsAlwaysAllowed(signature))
        {
            logger.LogInformation("Автоматически разрешено по правилу «всегда»: {Signature}", signature);
            Audit(AuditKinds.Approval, signature, "rule");
            return ApprovalDecision.Allow();
        }

        try
        {
            return await AskApprovalAsync(toolName, input, signature, suggested, ct);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Разрешение на {Tool} не получено за отведённое время", toolName);
            Audit(AuditKinds.Approval, signature, "timeout");
            return ApprovalDecision.Deny("Пользователь не ответил на запрос разрешения за отведённое время. Не повторяйте это действие.");
        }
        catch (OperationCanceledException)
        {
            Audit(AuditKinds.Approval, signature, "cancel");
            return ApprovalDecision.Deny("Запрос отменён пользователем.");
        }
    }

    private async Task<ApprovalDecision> AskApprovalAsync(
        string toolName, JsonElement? input, string signature, IReadOnlyList<PersistentRule>? suggested, CancellationToken ct)
    {
        var buttons = new List<ChoiceOption>
        {
            new("allow", "✅ Разрешить"),
            new("always", "♾ Всегда"),
            new("deny", "❌ Отклонить"),
            new("reason", "✋ Отклонить с причиной"),
        };

        var card = ApprovalCardRenderer.Render(toolName, input, signature, suggested, store.ProjectPath);

        // Обрезанный вход — файлом до карточки: разрешать команду, хвост которой не виден, нельзя.
        if (card.FullText is { } fullText)
            await broker.SendAttachmentAsync($"{SafeFileName(toolName)}-input.txt", fullText, ct);

        var (key, userId) = await broker.AskChoiceAsync(card.Html, buttons, ct);
        Audit(AuditKinds.Approval, signature, key, userId);

        switch (key)
        {
            case "allow":
                return ApprovalDecision.Allow();

            case "always":
                // Правило агента шире и живёт у него; своё запоминаем, только когда он ничего не предложил.
                if (suggested is null) store.AddAlwaysAllow(signature);
                Audit(AuditKinds.Rules, $"add {signature}", suggested is null ? "gateway" : "agent", userId);
                return ApprovalDecision.Allow(persistRules: suggested is not null);

            case "reason":
                var reason = await broker.AskTextAsync(
                    "Напишите сообщением, почему отклоняете и что делать вместо этого:", ct);
                return ApprovalDecision.Deny(reason);

            default:
                return ApprovalDecision.Deny("Пользователь отклонил это действие.");
        }
    }

    public async Task<QuestionResult> AskAsync(IReadOnlyList<AgentQuestion> questions, CancellationToken ct)
    {
        // У вопроса сигнатуры нет — правил «всегда» для него не бывает, а полный текст вопроса
        // в журнале лишний: хватает того же превью, что в логе.
        var brief = Text.Preview(questions.FirstOrDefault()?.Text ?? "");
        logger.LogInformation("Вопрос агента: {Key}", brief);

        using var pending = monitor.Approval("AskUserQuestion", brief);

        try
        {
            var answers = new List<QuestionAnswer>();
            foreach (var question in questions) answers.Add(await AskOneAsync(question, ct));
            return QuestionResult.Answered(answers);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Ответ на вопрос агента не получен за отведённое время");
            Audit(AuditKinds.Approval, $"AskUserQuestion({brief})", "timeout");
            return QuestionResult.Refused("Пользователь не ответил на вопрос за отведённое время. Не повторяйте его.");
        }
        catch (OperationCanceledException)
        {
            Audit(AuditKinds.Approval, $"AskUserQuestion({brief})", "cancel");
            return QuestionResult.Refused("Запрос отменён пользователем.");
        }
    }

    private async Task<QuestionAnswer> AskOneAsync(AgentQuestion question, CancellationToken ct)
    {
        var card = new StringBuilder("❓ ");
        if (question.Header is { Length: > 0 } header)
            card.Append("<b>").Append(TelegramFormatter.EscapeCapped(header, HeaderBudget)).Append("</b>\n");
        card.Append(TelegramFormatter.EscapeCapped(question.Text, QuestionBudget));

        var buttons = new List<ChoiceOption>();
        // На кнопке подпись урезана, а агенту нужен полный текст варианта.
        var fullLabels = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < question.Options.Count; i++)
        {
            var (label, description) = question.Options[i];

            card.Append("\n\n<b>").Append(TelegramFormatter.EscapeCapped(label, OptionLabelBudget)).Append("</b>");
            if (description is { Length: > 0 })
                card.Append(" — ").Append(TelegramFormatter.EscapeCapped(description, OptionDescriptionBudget));

            fullLabels[$"o{i}"] = label;
            buttons.Add(new ChoiceOption($"o{i}", Text.Clip(label, ButtonLabelLimit)));
        }

        buttons.Add(new ChoiceOption("free", "✍️ Свой ответ"));

        if (question.MultiSelect)
            card.Append("\n\n<i>Можно выбрать несколько — тогда «Свой ответ» и перечислите через запятую.</i>");

        var (key, userId) = await broker.AskChoiceAsync(card.ToString(), buttons, ct);

        var answer = key == "free"
            ? await broker.AskTextAsync("Напишите ответ сообщением:", ct)
            : fullLabels.GetValueOrDefault(key, key);

        Audit(AuditKinds.Question,
            $"{Text.Preview(question.Header ?? question.Text, 60)}: {Text.Preview(answer, 60)}",
            key == "free" ? "free" : "option", userId);

        return new QuestionAnswer(question.Text, answer);
    }

    /// <summary>Имя инструмента приходит от агента: в имени файла ему нечего делать с разделителями путей.</summary>
    private static string SafeFileName(string toolName)
    {
        var safe = new string(toolName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return Text.Clip(safe.Length == 0 ? "tool" : safe, 40);
    }

    /// <summary>Самое важное поле инструмента — команда или путь к файлу.</summary>
    private static string? Highlight(string toolName, JsonElement? input)
    {
        if (input is not { ValueKind: JsonValueKind.Object } obj) return null;

        string[] interesting = ["command", "file_path", "path", "url", "pattern"];

        foreach (var name in interesting)
        {
            if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    /// <summary>
    /// Ключ для кнопки «Всегда», когда агент не предложил своих правил. Запоминаем точное значение
    /// ключевого аргумента, а не префикс: разрешённое «git status» не должно открывать дорогу
    /// «git status &amp;&amp; rm -rf .», а один разрешённый Write — записи в любой файл.
    /// </summary>
    private static string BuildSignature(string toolName, JsonElement? input)
    {
        var key = Highlight(toolName, input)?.Trim();
        if (key is { Length: > 0 }) return $"{toolName}({key})";

        // Ключевого поля нет (WebSearch с query, MCP-инструменты) — берём весь вход целиком.
        // Голое имя инструмента открыло бы «Всегда» для любых его аргументов.
        var raw = input is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } element
            ? JsonSerializer.Serialize(element)
            : "";

        // Скобки обязательны даже при пустом входе: по ним отличаются сигнатуры этого формата
        // от голого имени инструмента, которое писала старая версия (оно разрешало любые
        // аргументы и вычищается при загрузке state.json).
        return raw is { Length: > 0 } && raw != "{}" ? $"{toolName}{raw}" : $"{toolName}()";
    }

    private void Audit(string kind, string summary, string outcome, long? userId = null) =>
        audit.Write(AuditEvent.Now(kind, summary, userId, broker.ActiveChatId, store.ProjectPath, store.SessionId, outcome));
}
