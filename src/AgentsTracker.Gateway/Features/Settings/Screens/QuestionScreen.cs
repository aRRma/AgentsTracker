using AgentsTracker.Gateway.Features.Question;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Кнопка «Вопрос»: экран только приглашает написать вопрос, а ждёт его QuestionLauncher.
/// Ожидание снимает «Назад» — корневой экран отменяет его при открытии, иначе следующая
/// задача ушла бы вопросом мимо сессии.
/// </summary>
public sealed class QuestionScreen(QuestionLauncher question, SkillLauncher skills, IOptions<GatewayOptions> options)
    : ISettingsScreen
{
    public string Key => "ask";

    public void Open(UserId user)
    {
        if (question.Refusal is not null) return;

        // Ждём одно: иначе текст забрали бы аргументами скилла, нажатого раньше.
        skills.Cancel(user);
        question.Expect(user);
    }

    public string? Apply(string argument, UserId user, ChatId chat) => null;

    public Task<(string Html, Keyboard Keyboard)> RenderAsync(UserId user, CancellationToken ct)
    {
        var text = question.Refusal ?? QuestionCommandHandler.Invitation(options.Value.Question.Model);
        return Task.FromResult((E(text), new Keyboard([[BackButton]])));
    }
}
