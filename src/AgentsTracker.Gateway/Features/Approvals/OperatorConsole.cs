using System.Text;
using System.Text.Json;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat;
using AgentsTracker.Gateway.Infrastructure.Monitoring;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Человек в чате глазами бэкенда: карточки подтверждения, правила «всегда», вопросы агента
/// и аудит каждого решения. Бэкенду достаётся только решение — где и кем оно принято,
/// знает лишь хост.
/// </summary>
public sealed class OperatorConsole(
    ApprovalBroker broker,
    IChatChannel channel,
    SessionStore store,
    IAuditLog audit,
    RunMonitor monitor,
    ILogger<OperatorConsole> logger) : IOperatorConsole
{
    // Бюджеты в символах уже экранированного HTML: сумма влезает в лимит сообщения,
    // даже если текст целиком состоит из «&».
    private const int HeaderBudget = 200;
    private const int QuestionBudget = 1000;
    private const int OptionLabelBudget = 120;
    private const int OptionDescriptionBudget = 300;

    public async Task<ApprovalDecision> ApproveAsync(ApprovalRequest request, CancellationToken ct)
    {
        var (toolName, input, suggested) = request;
        var signature = BuildSignature(toolName, input);

        // Только начало: в полной команде может быть токен из заголовка curl.
        var brief = Text.Preview(Highlight(toolName, input) ?? "");
        logger.LogInformation("Запрошено подтверждение: {Tool} {Key}", toolName, brief);

        // Монитору та же строка, что и логу: в полном вводе Edit/Write лежит содержимое файлов.
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
            logger.LogWarning("Подтверждение на {Tool} не получено за отведённое время", toolName);
            Audit(AuditKinds.Approval, signature, "timeout");
            return ApprovalDecision.Deny("Пользователь не подтвердил это действие за отведённое время. Не повторяйте его.");
        }
        catch (OperationCanceledException)
        {
            Audit(AuditKinds.Approval, signature, "cancel");
            return ApprovalDecision.Deny("Отменено пользователем.");
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

        // Обрезанный вход — файлом до карточки: команду без видимого хвоста разрешать нельзя.
        if (card.Attachment is { } attachment)
            await broker.SendAttachmentAsync(attachment.FileName, attachment.Text, ct);

        var (key, user) = await broker.AskChoiceAsync(card.Html, buttons, ct);
        Audit(AuditKinds.Approval, signature, key, user);

        switch (key)
        {
            case "allow":
                return ApprovalDecision.Allow();

            case "always":
                // Правило агента шире и живёт у него; своё пишем, только если он не предложил.
                if (suggested is null) store.AddAlwaysAllow(signature);
                Audit(AuditKinds.Rules, $"add {signature}", suggested is null ? "gateway" : "agent", user);
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
        // Сигнатуры у вопроса нет — правил «всегда» для него не бывает, а в журнал хватает
        // того же превью, что и в лог.
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
            return QuestionResult.Refused("Отменено пользователем.");
        }
    }

    private async Task<QuestionAnswer> AskOneAsync(AgentQuestion question, CancellationToken ct)
    {
        var card = new StringBuilder("❓ ");
        if (question.Header is { Length: > 0 } header)
            card.Append("<b>").Append(ChatHtml.EscapeCapped(header, HeaderBudget)).Append("</b>\n");
        card.Append(ChatHtml.EscapeCapped(question.Text, QuestionBudget));

        var buttons = new List<ChoiceOption>();
        // Подпись на кнопке урезана, а агенту нужен полный текст варианта.
        var fullLabels = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < question.Options.Count; i++)
        {
            var (label, description) = question.Options[i];

            card.Append("\n\n<b>").Append(ChatHtml.EscapeCapped(label, OptionLabelBudget)).Append("</b>");
            if (description is { Length: > 0 })
                card.Append(" — ").Append(ChatHtml.EscapeCapped(description, OptionDescriptionBudget));

            fullLabels[$"o{i}"] = label;
            // Под предел кнопки подпись режет брокер: предел объявляет канал.
            buttons.Add(new ChoiceOption($"o{i}", label));
        }

        buttons.Add(new ChoiceOption("free", "✍️ Свой ответ"));

        if (question.MultiSelect)
            card.Append("\n\n<i>Можно выбрать несколько — тогда «Свой ответ» и перечислите через запятую.</i>");

        var (key, user) = await broker.AskChoiceAsync(card.ToString(), buttons, ct);

        var answer = key == "free"
            ? await broker.AskTextAsync("Напишите ответ сообщением:", ct)
            : fullLabels.GetValueOrDefault(key, key);

        Audit(AuditKinds.Question,
            $"{Text.Preview(question.Header ?? question.Text, 60)}: {Text.Preview(answer, 60)}",
            key == "free" ? "free" : "option", user);

        return new QuestionAnswer(question.Text, answer);
    }

    // ---- файлы от агента ----

    /// <summary>
    /// Что агент может отправить в чат. Список в коде, а не в конфиге: расширять его —
    /// решение с последствиями (exe из проекта ушёл бы наружу одним вызовом).
    /// Картинки уходят фото, остальное — документом. Тот же список продублирован словами
    /// в описании инструмента (ClaudeSendFileTool): меняя здесь — поправьте там.
    /// Архивы (<c>.zip</c>, <c>.7z</c>) — сознательная уступка ради переноса наборов файлов:
    /// содержимое не разбирается, и проверки пути относятся только к самому архиву. Внутрь
    /// упаковано может быть что угодно, до чего дотянулся агент, — файл вне проекта, из папки
    /// данных или запрещённый правилами Claude. Собирать такой архив и просить отправить — нельзя.
    /// </summary>
    private static readonly Dictionary<string, bool> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = false,
        [".txt"] = false,
        [".json"] = false,
        [".cs"] = false,
        [".js"] = false,
        [".html"] = false,
        [".zip"] = false,
        [".7z"] = false,
        [".png"] = true,
        [".jpg"] = true,
        [".jpeg"] = true,
    };

    public async Task<FileSendResult> SendFileAsync(FileSendRequest request, CancellationToken ct)
    {
        var (path, caption, asDocument) = request;
        var project = ProjectCatalog.Normalize(store.ProjectPath);

        // Аргумент MCP может прийти null: в отказ идёт строка, а не NRE в превью.
        if (string.IsNullOrWhiteSpace(path))
            return Refuse(path ?? "", "Путь к файлу пуст.");

        string full;
        try
        {
            full = ProjectCatalog.Normalize(Path.GetFullPath(path, project));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refuse(path, "Путь к файлу некорректен.");
        }

        // Только папка текущего проекта: агент работает в ней, а шлюз читает файл сам, в обход
        // запретов Claude на чтение. Папка данных шлюза исключена отдельно — она вне проекта,
        // но пусть отказ не зависит от того, куда её перенесли.
        if (!ProjectCatalog.IsInside(full, project) || ProjectCatalog.IsInside(full, DataDirectory))
            return Refuse(full, $"Файл вне текущего проекта ({project}). Отправлять можно только файлы из него.");

        var extension = Path.GetExtension(full);
        if (!AllowedExtensions.TryGetValue(extension, out var isImage))
            return Refuse(full, $"Тип файла «{extension}» не разрешён. Допустимы: {string.Join(", ", AllowedExtensions.Keys)}.");

        var file = new FileInfo(full);
        if (!file.Exists)
            return Refuse(full, "Файл не найден.");

        // Путь проверен как строка, а открывать файл будет ОС по ссылкам: symlink или junction
        // внутри проекта, ведущие наружу, обошли бы проверку выше и отдали бы чужой файл.
        if (CrossesLink(file, project))
            return Refuse(full, "Путь проходит через символическую ссылку или junction — отправлять можно только сами файлы проекта.");

        var asPhoto = isImage && !asDocument;
        var limits = channel.Limits;
        var limit = asPhoto ? limits.PhotoBytes : limits.DocumentBytes;
        if (file.Length > limit)
            return Refuse(full, $"Файл слишком большой: {file.Length.Bytes}, предел канала — {limit.Bytes}.");

        var trimmedCaption = caption is { Length: > 0 } ? Text.Clip(caption.Trim(), limits.CaptionLength) : null;

        logger.LogInformation("Агент отправляет файл: {Path} ({Size})", full, file.Length.Bytes);

        try
        {
            // ReadWrite: файл может дописывать сборка или сам агент. Asynchronous — иначе на
            // Windows ReadAsync читает синхронно и держит поток пула на всё время загрузки.
            await using var content = new FileStream(full, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            await broker.SendFileAsync(file.Name, content, trimmedCaption, asPhoto, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Refuse(full, "Отправка отменена пользователем.");
        }
        catch (Exception ex)
        {
            // Любой сбой — отказом с записью в аудит: попытка отправки должна остаться в журнале,
            // а таймаут клиента без отмены пользователем — не «отменено пользователем».
            logger.LogWarning(ex, "Не удалось отправить файл {Path}", full);
            return Refuse(full, $"Не удалось отправить файл: {ex.Message}");
        }

        Audit(AuditKinds.FileSend, $"{Path.GetFileName(full)} ({file.Length.Bytes})", asPhoto ? "photo" : "document");
        return FileSendResult.Ok();
    }

    /// <summary>Отказ — тоже в журнал: попытка выслать чужой файл важнее удачной отправки.</summary>
    private FileSendResult Refuse(string path, string reason)
    {
        logger.LogWarning("Отказ в отправке файла {Path}: {Reason}", path, reason);
        Audit(AuditKinds.FileSend, $"{Text.Preview(path, 120)}: {reason}", "refused");
        return FileSendResult.Refused(reason);
    }

    private static readonly string DataDirectory = ProjectCatalog.Normalize(AppPaths.DataDirectory);

    /// <summary>
    /// Сам файл или любая папка между ним и корнем проекта — точка повторного разбора.
    /// Корень не проверяется: проект по ссылке — выбор пользователя, а не агента.
    /// </summary>
    private static bool CrossesLink(FileInfo file, string project)
    {
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;

        for (var dir = file.Directory; dir is not null && !ProjectCatalog.Same(dir.FullName, project); dir = dir.Parent)
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;

        return false;
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
    /// Ключ для кнопки «Всегда», когда агент своих правил не предложил. Запоминаем точное
    /// значение ключевого аргумента, а не префикс: разрешённое «git status» не должно
    /// открывать «git status &amp;&amp; rm -rf .», а один Write — запись в любой файл.
    /// </summary>
    private static string BuildSignature(string toolName, JsonElement? input)
    {
        var key = Highlight(toolName, input)?.Trim();
        if (key is { Length: > 0 }) return $"{toolName}({key})";

        // Ключевого поля нет (WebSearch, MCP-инструменты) — берём вход целиком: голое имя
        // инструмента открыло бы «Всегда» для любых аргументов.
        var raw = input is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } element
            ? JsonSerializer.Serialize(element, SignatureJson)
            : "";

        // Скобки нужны даже при пустом входе: по ним сигнатура отличается от голого имени
        // инструмента, которое писали старые версии и которое вычищается при загрузке.
        return raw is { Length: > 0 } && raw != "{}" ? $"{toolName}{raw}" : $"{toolName}()";
    }

    /// <summary>
    /// Сигнатура видна в карточке и в /rules, а с экранированием по умолчанию кириллица
    /// превращается в \u-последовательности.
    /// </summary>
    private static readonly JsonSerializerOptions SignatureJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void Audit(string kind, string summary, string outcome, UserId? user = null) =>
        audit.Write(AuditEvent.Now(kind, summary, user, broker.ActiveChat, store.ProjectPath, store.SessionId, outcome));
}
