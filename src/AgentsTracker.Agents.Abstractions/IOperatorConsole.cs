using System.Text.Json;

namespace AgentsTracker.Agents;

/// <summary>
/// Правило, которое агент готов запомнить у себя по кнопке «Всегда» (у Claude —
/// <c>permission_suggestions</c> в <c>.claude/settings.local.json</c>). Хост показывает
/// <see cref="Display"/> и возвращает <see cref="Raw"/> как есть: форма правила — дело бэкенда.
/// </summary>
/// <param name="Display">Как показать человеку: «Bash(git push:*)».</param>
/// <param name="Raw">Элемент в формате агента, отдаётся обратно без изменений.</param>
public sealed record PersistentRule(string Display, JsonElement Raw);

/// <summary>Запрос разрешения. Вход как прислал агент: что показать, решает карточка.</summary>
/// <param name="SuggestedRules">
/// Правила, которые агент готов записать у себя по «Всегда». Если они есть, хост не
/// запоминает свою сигнатуру, а возвращает их в решении: правило агента префиксное
/// и шире точного совпадения.
/// </param>
public sealed record ApprovalRequest(
    string ToolName,
    JsonElement? Input,
    IReadOnlyList<PersistentRule>? SuggestedRules);

/// <summary>Решение оператора. При отказе <see cref="Reason"/> — текст для агента.</summary>
/// <param name="PersistRules">
/// Разрешено «всегда» и агент должен сам записать <see cref="ApprovalRequest.SuggestedRules"/>.
/// false при «Всегда» без предложенных правил: тогда сигнатуру запомнил хост.
/// </param>
public sealed record ApprovalDecision(bool Allowed, string? Reason, bool PersistRules)
{
    public static ApprovalDecision Allow(bool persistRules = false) => new(true, null, persistRules);

    public static ApprovalDecision Deny(string reason) => new(false, reason, false);
}

/// <summary>Вариант ответа на вопрос агента.</summary>
public sealed record QuestionOption(string Label, string? Description);

/// <summary>Вопрос агента человеку (у Claude — инструмент <c>AskUserQuestion</c>).</summary>
public sealed record AgentQuestion(
    string Text,
    string? Header,
    bool MultiSelect,
    IReadOnlyList<QuestionOption> Options);

/// <summary>Ответ на один вопрос: полный текст выбранного варианта либо свой текст.</summary>
public sealed record QuestionAnswer(string Question, string Answer);

/// <summary>Ответы на все вопросы либо причина, по которой их не получили (таймаут, /stop).</summary>
public sealed record QuestionResult(IReadOnlyList<QuestionAnswer>? Answers, string? Refusal)
{
    public static QuestionResult Answered(IReadOnlyList<QuestionAnswer> answers) => new(answers, null);

    public static QuestionResult Refused(string reason) => new(null, reason);
}

/// <summary>
/// Человек по ту сторону шлюза: у него агент просит разрешения и ему задаёт вопросы.
/// Реализует хост (карточки, правила «всегда», аудит), а зовут бэкенды — каждый из своего
/// канала подтверждений; у Claude это MCP-инструмент.
///
/// Таймаут и отмена не бросают, а приходят отказом с текстом для агента: бэкенду незачем
/// знать, как хост различает «не ответил» и «/stop».
/// </summary>
public interface IOperatorConsole
{
    Task<ApprovalDecision> ApproveAsync(ApprovalRequest request, CancellationToken ct);

    Task<QuestionResult> AskAsync(IReadOnlyList<AgentQuestion> questions, CancellationToken ct);
}
