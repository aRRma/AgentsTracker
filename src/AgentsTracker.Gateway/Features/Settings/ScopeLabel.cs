using AgentsTracker.Gateway.Features.Settings.Screens;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Как назвать человеку то, с чем работает чат: беседа, «Работа» или проект. Одна развилка на все
/// экраны: разойдись они — где-то вместо «работа» всплыло бы имя папки темы («telegram_123_42»).
/// </summary>
public static class ScopeLabel
{
    /// <summary>Короткое имя в строку: «беседа», «работа» или имя проекта.</summary>
    public static string Name(SessionStore.ChatScope scope, string project) =>
        scope.IsTalk ? "беседа" : scope.IsWork ? "работа" : Path.GetFileName(project);

    /// <summary>
    /// Блок «где» для сводок: тип, имя и путь. <paramref name="projectSuffix"/> дописывается только к
    /// проекту (ветка) и зовётся только для него — у беседы и «Работы» ветки нет.
    /// </summary>
    public static string Where(SessionStore.ChatScope scope, string project, Func<string>? projectSuffix = null) =>
        scope.IsNewTopic ? RootScreen.NewTopic
        : scope.IsTalk ? "💬 <b>Беседа</b>"
        : scope.IsWork ? $"💼 <b>Работа</b>\n<code>{E(project)}</code>"
        : $"📁 <b>{E(Path.GetFileName(project))}</b>\n<code>{E(project)}</code>{projectSuffix?.Invoke()}";
}
