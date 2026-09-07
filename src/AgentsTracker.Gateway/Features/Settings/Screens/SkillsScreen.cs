using AgentsTracker.Gateway.Infrastructure.Audit;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Скиллы агента кнопками: источник (встроенные, проект, личные, плагин) → скилл →
/// карточка с описанием, подсказкой и флагами. Из карточки скилл идёт сразу или после
/// ввода аргументов следующим сообщением (<see cref="SkillLauncher"/>). Экран «Плагины»
/// включает их и выключает: список скиллов меняется сразу, агент — со следующего запуска.
/// </summary>
public sealed class SkillsScreen(
    SessionStore store, IAgentSkillCatalog catalog, SkillLauncher launcher, IAuditLog audit) : ISettingsScreen
{
    /// <summary>Префиксы callback-ов: не hex, чтобы не спутать с 12-значным ключом.</summary>
    private const string PagePrefix = "p";

    private const string GroupPrefix = "g";

    private const string CardPrefix = "o";

    private const string RunPrefix = "r";

    private const string AskPrefix = "w";

    private const string TogglePrefix = "t";

    private const string UpArgument = "up";

    private const string ListArgument = "list";

    /// <summary>Сброс кэша каталога: плагин выключили в IDE, а шлюз ещё показывает его скиллы.</summary>
    private const string ReloadArgument = "reload";

    /// <summary>Открыть плагины. Лежит в «группе» позиции: не hex, с ключами групп не спутается.</summary>
    private const string PluginsArgument = "plugins";

    /// <summary>Сколько частых скиллов выносить в отдельную группу наверх.</summary>
    private const int TopCount = 5;

    private const string TopGroup = "⭐ Частые";

    private readonly ScreenNavigation _nav = new();

    public string Key => "skills";

    public void Open(UserId user)
    {
        _nav.Reset(user);
        launcher.Cancel(user);
    }

    public string? Apply(string argument, UserId user, ChatId chat)
    {
        switch (argument)
        {
            case UpArgument:
                _nav.Reset(user);
                return null;

            case ListArgument:
                _nav.Update(user, p => p with { Card = null });
                launcher.Cancel(user);
                return null;

            case ReloadArgument:
                catalog.Refresh();
                return "Список перечитан с диска";

            case PluginsArgument:
                _nav.Set(user, new ScreenPosition(Group: PluginsArgument));
                return null;
        }

        if (argument.StartsWith(TogglePrefix, StringComparison.Ordinal))
            return Toggle(argument[TogglePrefix.Length..], user);

        if (argument.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            _nav.Set(user, new ScreenPosition(Group: argument[GroupPrefix.Length..]));
            return null;
        }

        if (argument.StartsWith(PagePrefix, StringComparison.Ordinal)
            && int.TryParse(argument[PagePrefix.Length..], out var page))
        {
            _nav.Update(user, p => p with { Page = page });
            return null;
        }

        if (argument.StartsWith(CardPrefix, StringComparison.Ordinal))
        {
            _nav.Update(user, p => p with { Card = argument[CardPrefix.Length..] });
            return null;
        }

        if (argument.StartsWith(RunPrefix, StringComparison.Ordinal))
        {
            var skill = Find(argument[RunPrefix.Length..]);
            if (skill is null) return "Скилла уже нет в списке";

            launcher.Cancel(user);
            return launcher.Launch(chat, user, skill.Command);
        }

        if (argument.StartsWith(AskPrefix, StringComparison.Ordinal))
        {
            var skill = Find(argument[AskPrefix.Length..]);
            if (skill is null) return "Скилла уже нет в списке";

            launcher.Expect(user, skill.Command);
            return $"Напишите аргументы для {skill.Command} следующим сообщением";
        }

        return null;
    }

    public Task<(string Html, Keyboard Keyboard)> RenderAsync(UserId user, CancellationToken ct) =>
        Task.FromResult(Render(user));

    /// <summary>Ищем по ключу, а не по номеру: между отрисовкой и нажатием список мог измениться.</summary>
    private SkillInfo? Find(string key) =>
        catalog.Grouped(store.ProjectPath).SelectMany(g => g.Skills).FirstOrDefault(s => Key12(s.Command) == key);

    /// <summary>
    /// Переворачивает состояние плагина. Кнопка несёт только ключ, без желаемого состояния:
    /// если плагин тем временем переключили в IDE, нажатие всё равно даст противоположное
    /// действующему, и экран сразу его покажет.
    /// </summary>
    private string? Toggle(string key, UserId user)
    {
        var project = store.ProjectPath;
        var plugin = catalog.Plugins(project).FirstOrDefault(p => Key12(p.Key) == key);
        if (plugin is null) return "Плагина уже нет в списке";
        if (plugin.LockedBy is not null) return $"Задано в {plugin.LockedBy} — там и меняйте";

        var enabled = !plugin.Enabled;
        if (catalog.SetPluginEnabled(plugin.Key, enabled, project) is { } error) return error;

        audit.Changed(store, user, $"plugin {plugin.Name}", State(plugin.Enabled), State(enabled));
        return $"{plugin.Name}: {State(enabled)} — со следующего запуска агента";
    }

    private static string State(bool enabled) => enabled ? "включён" : "выключен";

    private (string Html, Keyboard Keyboard) Render(UserId user)
    {
        var position = _nav.Of(user);
        var usage = store.SkillUsage();

        // Плагины раньше проверки на пустоту: если выключены все, включить их можно только отсюда.
        if (position.Group == PluginsArgument) return RenderPlugins(user, position.Page);

        // Каталог кэширует обход диска: одно нажатие — это Apply и Render подряд.
        var sources = catalog.Grouped(store.ProjectPath);

        if (sources.Count == 0)
        {
            // Где агент ищет скиллы, знает только он сам — подсказка приходит из каталога.
            var empty = $"""
                🧩 <b>Скиллы</b>

                {catalog.EmptyHint}

                <i>Неизвестные шлюзу слэш-команды и так уходят агенту как есть.</i>
                """;
            return (empty, new Keyboard([ToolsRow(), [BackButton]]));
        }

        if (position.Card is not null)
        {
            var skill = sources.SelectMany(g => g.Skills).FirstOrDefault(s => Key12(s.Command) == position.Card);
            if (skill is not null) return RenderCard(skill, usage);
            _nav.Update(user, p => p with { Card = null });
        }

        // Источник один — выбирать не из чего, экран выбора лишний.
        if (sources.Count == 1) return RenderSkills(user, sources[0], single: true, position.Page, usage);

        var groups = WithTop(sources, usage);
        var opened = position.Group is null ? null : groups.FirstOrDefault(g => Key12(g.Name) == position.Group);

        if (opened is null)
        {
            _nav.Reset(user);
            return RenderGroups(user, groups, position.Page);
        }

        return RenderSkills(user, opened, single: false, position.Page, usage);
    }

    /// <summary>
    /// Первой группой — самые запускаемые скиллы; счётчики ведёт
    /// <see cref="SessionStore.RecordSkillUse"/>. Из своих групп они не исчезают,
    /// иначе в списке источника появились бы дыры.
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

    private (string Html, Keyboard Keyboard) RenderGroups(UserId user, IReadOnlyList<SkillGroup> groups, int pageIndex)
    {
        var (page, clamped, counter, pageRow) = Page(groups, pageIndex, Key, PagePrefix, "источников");
        _nav.Update(user, p => p with { Page = clamped });

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
        buttons.Add(ToolsRow());
        buttons.Add([BackButton]);

        return (html, new Keyboard(buttons));
    }

    /// <summary>
    /// Служебный ряд кнопок. «Обновить» сбрасывает кэш — скилл добавили или плагин выключили
    /// в IDE, а шлюз показывает старое. «Плагины» — только если они у агента есть.
    /// </summary>
    private KeyboardButton[] ToolsRow()
    {
        var plugins = catalog.Plugins(store.ProjectPath);
        var reload = Button("🔄 Обновить", $"{Key}:{ReloadArgument}");
        return plugins.Count == 0
            ? [reload]
            : [reload, Button($"🔌 Плагины ({plugins.Count(p => p.Enabled)}/{plugins.Count})", $"{Key}:{PluginsArgument}")];
    }

    /// <summary>
    /// Плагины с переключателями: в кнопке текущее состояние, нажатие его переворачивает.
    /// Заданный в настройках проекта помечен замком — из чата шлюз правит только личные
    /// настройки, а слой проекта их перекрыл бы.
    /// </summary>
    private (string Html, Keyboard Keyboard) RenderPlugins(UserId user, int pageIndex)
    {
        var plugins = catalog.Plugins(store.ProjectPath);
        var (page, clamped, counter, pageRow) = Page(plugins, pageIndex, Key, PagePrefix, "плагинов");
        _nav.Update(user, p => p with { Page = clamped });

        var lines = page.Select(plugin =>
            $"{Icon(plugin)} <b>{E(plugin.Name)}</b>"
            + (plugin.LockedBy is null ? "" : $" — <i>{State(plugin.Enabled)}, задано в {E(plugin.LockedBy)}</i>"));

        var html = $"""
            🔌 <b>Плагины</b>

            {(lines.Any() ? string.Join("\n", lines) : "<i>Установленных плагинов нет.</i>")}

            <i>Нажатие включает или выключает плагин в личных настройках агента —
            как его собственная команда /plugin. Список скиллов обновится сразу,
            агент подхватит настройку со следующего запуска.</i>{counter}
            """;

        var buttons = page
            .Select(plugin => Button($"{Icon(plugin)} {plugin.Name}", $"{Key}:{TogglePrefix}{Key12(plugin.Key)}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add([Button("🧩 К скиллам", $"{Key}:{UpArgument}"), BackButton]);

        return (html, new Keyboard(buttons));
    }

    private static string Icon(PluginInfo plugin) =>
        plugin.LockedBy is not null ? "🔒" : plugin.Enabled ? "✅" : "⛔";

    private (string Html, Keyboard Keyboard) RenderSkills(
        UserId user, SkillGroup group, bool single, int pageIndex, IReadOnlyDictionary<string, int> usage)
    {
        var (page, clamped, counter, pageRow) = Page(group.Skills, pageIndex, Key, PagePrefix, "скиллов");
        _nav.Update(user, p => p with { Page = clamped });

        var lines = page.Select(skill =>
            $"· <code>{E(skill.Command)}</code>"
            + (usage.GetValueOrDefault(skill.Command) is > 0 and var count ? $" — {count} {Times(count)}" : "")
            + (skill.Description.Length > 0 ? $"\n   {E(skill.Description)}" : ""));

        var html = $"""
            🧩 <b>{E(group.Name)}</b>

            {string.Join("\n", lines)}

            <i>Нажатие открывает карточку скилла.</i>{counter}
            """;

        // Имя без префикса плагина: он и так в заголовке, а место в кнопке дорого. В группе
        // частых источники разные, там префикс нужен — иначе одноимённые не различить.
        var mixed = group.Name == TopGroup;
        var buttons = page
            .Select(skill => Button(mixed ? skill.Command : ShortName(skill.Command), $"{Key}:{CardPrefix}{Key12(skill.Command)}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        if (single) buttons.Add(ToolsRow());
        buttons.Add(single ? [BackButton] : [Button("📦 К источникам", $"{Key}:{UpArgument}"), BackButton]);

        return (html, new Keyboard(buttons));
    }

    /// <summary>
    /// Карточка: всё, что известно о скилле до запуска. Схемы аргументов у скиллов нет,
    /// поэтому показываем что нашлось — подсказку из frontmatter и флаги из текста.
    /// </summary>
    private (string Html, Keyboard Keyboard) RenderCard(SkillInfo skill, IReadOnlyDictionary<string, int> usage)
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

        var keyboard = new Keyboard(
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
