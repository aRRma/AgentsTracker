using AgentsTracker.Gateway.Infrastructure.Claude;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Скиллы Claude Code кнопками: источник (встроенные, проект, личные, плагин) → скилл →
/// карточка с описанием, подсказкой по аргументам и флагами. Из карточки скилл запускается
/// сразу или после ввода аргументов следующим сообщением (<see cref="SkillLauncher"/>).
/// </summary>
public sealed class SkillsScreen(SessionStore store, SkillCatalog catalog, SkillLauncher launcher) : ISettingsScreen
{
    /// <summary>Префиксы callback-ов: не hex, чтобы не спутать с 12-значным ключом.</summary>
    private const string PagePrefix = "p";

    private const string GroupPrefix = "g";

    private const string CardPrefix = "o";

    private const string RunPrefix = "r";

    private const string AskPrefix = "w";

    private const string UpArgument = "up";

    private const string ListArgument = "list";

    /// <summary>Сколько частых скиллов выносить в отдельную группу наверх.</summary>
    private const int TopCount = 5;

    private const string TopGroup = "⭐ Частые";

    private readonly ScreenNavigation _nav = new();

    public string Key => "skills";

    public void Open(long userId)
    {
        _nav.Reset(userId);
        launcher.Cancel(userId);
    }

    public string? Apply(string argument, long userId, long chatId)
    {
        switch (argument)
        {
            case UpArgument:
                _nav.Reset(userId);
                return null;

            case ListArgument:
                _nav.Update(userId, p => p with { Card = null });
                launcher.Cancel(userId);
                return null;
        }

        if (argument.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            _nav.Set(userId, new ScreenPosition(Group: argument[GroupPrefix.Length..]));
            return null;
        }

        if (argument.StartsWith(PagePrefix, StringComparison.Ordinal)
            && int.TryParse(argument[PagePrefix.Length..], out var page))
        {
            _nav.Update(userId, p => p with { Page = page });
            return null;
        }

        if (argument.StartsWith(CardPrefix, StringComparison.Ordinal))
        {
            _nav.Update(userId, p => p with { Card = argument[CardPrefix.Length..] });
            return null;
        }

        if (argument.StartsWith(RunPrefix, StringComparison.Ordinal))
        {
            var skill = Find(argument[RunPrefix.Length..]);
            if (skill is null) return "Скилла уже нет в списке";

            launcher.Cancel(userId);
            return launcher.Launch(chatId, userId, skill.Command);
        }

        if (argument.StartsWith(AskPrefix, StringComparison.Ordinal))
        {
            var skill = Find(argument[AskPrefix.Length..]);
            if (skill is null) return "Скилла уже нет в списке";

            launcher.Expect(userId, skill.Command);
            return $"Напишите аргументы для {skill.Command} следующим сообщением";
        }

        return null;
    }

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(long userId, CancellationToken ct) =>
        Task.FromResult(Render(userId));

    /// <summary>Ищем по ключу, а не по номеру: между отрисовкой и нажатием список мог измениться.</summary>
    private SkillInfo? Find(string key) =>
        catalog.Grouped(store.ProjectPath).SelectMany(g => g.Skills).FirstOrDefault(s => Key12(s.Command) == key);

    private (string Html, InlineKeyboardMarkup Keyboard) Render(long userId)
    {
        var position = _nav.Of(userId);
        var usage = store.SkillUsage();

        // Каталог кэширует обход диска на несколько секунд: сюда попадают и Apply, и Render одного нажатия.
        var sources = catalog.Grouped(store.ProjectPath);

        if (sources.Count == 0)
        {
            var empty = """
                🧩 <b>Скиллы</b>

                Ничего не найдено: ни <code>.claude/skills</code> в проекте и профиле,
                ни включённых плагинов в <code>~/.claude/plugins</code>, ни строк
                в <code>Gateway:BuiltInSkills</code>.

                <i>Неизвестные шлюзу слэш-команды и так уходят агенту как есть.</i>
                """;
            return (empty, new InlineKeyboardMarkup([[BackButton]]));
        }

        if (position.Card is not null)
        {
            var skill = sources.SelectMany(g => g.Skills).FirstOrDefault(s => Key12(s.Command) == position.Card);
            if (skill is not null) return RenderCard(skill, usage);
            _nav.Update(userId, p => p with { Card = null });
        }

        // Один источник — экран выбора источника лишний; группа «Частые» это не отменяет.
        if (sources.Count == 1) return RenderSkills(userId, sources[0], single: true, position.Page, usage);

        var groups = WithTop(sources, usage);
        var opened = position.Group is null ? null : groups.FirstOrDefault(g => Key12(g.Name) == position.Group);

        if (opened is null)
        {
            _nav.Reset(userId);
            return RenderGroups(userId, groups, position.Page);
        }

        return RenderSkills(userId, opened, single: false, position.Page, usage);
    }

    /// <summary>
    /// Первой группой — до пяти самых запускаемых скиллов из каталога; счётчики ведёт
    /// <see cref="SessionStore.RecordSkillUse"/>. Остальные группы остаются по алфавиту:
    /// частый скилл виден и там, чтобы в списке источника не было «дыр».
    /// </summary>
    private static IReadOnlyList<SkillGroup> WithTop(IReadOnlyList<SkillGroup> groups, IReadOnlyDictionary<string, int> usage)
    {
        if (usage.Count == 0) return groups;

        var top = groups
            .SelectMany(g => g.Skills)
            .Select(s => (Skill: s, Count: usage.GetValueOrDefault(s.Command)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Skill.Command, StringComparer.OrdinalIgnoreCase)
            .Take(TopCount)
            .Select(x => x.Skill)
            .ToList();

        return top.Count == 0 ? groups : [new SkillGroup(TopGroup, top), .. groups];
    }

    private (string Html, InlineKeyboardMarkup Keyboard) RenderGroups(long userId, IReadOnlyList<SkillGroup> groups, int pageIndex)
    {
        var (page, clamped, counter, pageRow) = Page(groups, pageIndex, Key, PagePrefix, "источников");
        _nav.Update(userId, p => p with { Page = clamped });

        var lines = page.Select(group => $"· <b>{E(group.Name)}</b> — {group.Skills.Count}");

        var html = $"""
            🧩 <b>Скиллы</b>

            {string.Join("\n", lines)}

            <i>Выберите источник, потом скилл: откроется карточка с описанием,
            аргументами и кнопкой запуска.</i>{counter}
            """;

        var buttons = page
            .Select(group => Button($"📦 {group.Name} ({group.Skills.Count})", $"{Key}:{GroupPrefix}{Key12(group.Name)}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    private (string Html, InlineKeyboardMarkup Keyboard) RenderSkills(
        long userId, SkillGroup group, bool single, int pageIndex, IReadOnlyDictionary<string, int> usage)
    {
        var (page, clamped, counter, pageRow) = Page(group.Skills, pageIndex, Key, PagePrefix, "скиллов");
        _nav.Update(userId, p => p with { Page = clamped });

        var lines = page.Select(skill =>
            $"· <code>{E(skill.Command)}</code>"
            + (usage.GetValueOrDefault(skill.Command) is > 0 and var count ? $" — {count} {Times(count)}" : "")
            + (skill.Description.Length > 0 ? $"\n   {E(skill.Description)}" : ""));

        var html = $"""
            🧩 <b>{E(group.Name)}</b>

            {string.Join("\n", lines)}

            <i>Нажатие открывает карточку скилла.</i>{counter}
            """;

        // В кнопке — имя без префикса плагина: он и так в заголовке, а место в кнопке дорого.
        // В группе частых источники разные, там префикс остаётся — иначе два одноимённых не различить.
        var mixed = group.Name == TopGroup;
        var buttons = page
            .Select(skill => Button(mixed ? skill.Command : ShortName(skill.Command), $"{Key}:{CardPrefix}{Key12(skill.Command)}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add(single ? [BackButton] : [Button("📦 К источникам", $"{Key}:{UpArgument}"), BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    /// <summary>
    /// Карточка: всё, что известно о скилле до запуска. Формальной схемы аргументов у скиллов
    /// нет, поэтому показываем то, что удалось достать — подсказку из frontmatter и флаги,
    /// упомянутые в тексте.
    /// </summary>
    private (string Html, InlineKeyboardMarkup Keyboard) RenderCard(SkillInfo skill, IReadOnlyDictionary<string, int> usage)
    {
        var count = usage.GetValueOrDefault(skill.Command);
        var key = Key12(skill.Command);

        var parts = new List<string> { $"🧩 <b>{E(skill.Command)}</b>  <i>({E(skill.Group)})</i>" };

        if (skill.Details.Length > 0) parts.Add(E(skill.Details));

        var facts = new List<string>();
        if (skill.ArgumentHint is not null) facts.Add($"Аргументы: <code>{E(skill.Command)} {E(skill.ArgumentHint)}</code>");
        if (skill.Flags.Count > 0) facts.Add($"Флаги, упомянутые в описании: {string.Join(", ", skill.Flags.Select(f => $"<code>{E(f)}</code>"))}");
        if (facts.Count == 0) facts.Add("<i>Подсказки по аргументам нет — обычно запускается без них.</i>");
        if (count > 0) facts.Add($"Запускали: {count} {Times(count)}");
        parts.Add(string.Join("\n", facts));

        parts.Add("<i>«Запустить» — без аргументов. «С аргументами» — следующее сообщение станет аргументами команды; «отмена» отменяет.</i>");

        var html = string.Join("\n\n", parts);

        var keyboard = new InlineKeyboardMarkup(
        [
            [Button("🚀 Запустить", $"{Key}:{RunPrefix}{key}"), Button("✏️ С аргументами", $"{Key}:{AskPrefix}{key}")],
            [Button("◀️ К списку", $"{Key}:{ListArgument}"), BackButton],
        ]);

        return (html, keyboard);
    }

    /// <summary>«раз» / «раза» по правилам русского: 1, 21 — раз; 2–4, 22–24 — раза; 5–20, 25–30 — раз.</summary>
    private static string Times(int count)
    {
        var tens = count % 100;
        var ones = count % 10;
        return ones is >= 2 and <= 4 && tens is < 12 or > 14 ? "раза" : "раз";
    }

    private static string ShortName(string command)
    {
        var colon = command.IndexOf(':');
        return colon < 0 ? command : "/" + command[(colon + 1)..];
    }
}
