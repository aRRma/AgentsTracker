using System.Globalization;
using System.Text;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Шкалы расхода тарифных окон. Графиков в чате нет, поэтому шкала — строка сегментов
/// в <c>&lt;code&gt;</c> (моноширинный шрифт держит одну ширину), а «анимация» — несколько
/// правок сообщения, на каждой шкала заполнена на долю <c>progress</c> от расхода.
/// </summary>
internal static class LimitBars
{
    /// <summary>Сколько правок на заполнение: больше — плавнее, но каналы режут частые правки.</summary>
    public const int Frames = 3;

    /// <summary>Пауза между кадрами; на трёх кадрах укладываемся в секунду.</summary>
    public static readonly TimeSpan FrameDelay = TimeSpan.FromMilliseconds(350);

    private const int Cells = 10;

    /// <summary>Доля заполнения кадра 0..1: первый почти пустой, последний — итог.</summary>
    public static double Progress(int frame) => (double)(frame + 1) / Frames;

    /// <summary>
    /// Блок «Расход тарифа» построчно, уже в HTML. Ошибку опроса пишем курсивом, а вместо
    /// пустого блока — «окон нет»: пустота выглядела бы как сломанный экран.
    /// </summary>
    public static string Render(LimitsView view, double progress)
    {
        if (view.Error is { } error) return $"<i>{E(error)}</i>";
        if (view.Windows.Count == 0) return "<i>окон нет</i>";

        return string.Join("\n", view.Windows.Select(w => Line(w, progress)));
    }

    private static string Line(LimitGauge gauge, double progress)
    {
        var used = Math.Clamp(gauge.Used, 0.0, 1.0);
        var shown = used * Math.Clamp(progress, 0.0, 1.0);
        var filled = (int)Math.Round(shown * Cells, MidpointRounding.AwayFromZero);

        var bar = new StringBuilder(Cells)
            .Append('▰', filled)
            .Append('▱', Cells - filled)
            .ToString();

        // Цифры сразу итоговые, кадрами двигается только полоса: пара «расход · остаток» посреди
        // анимации не должна складываться во что-то кроме 100%.
        var percent = Percent(used).ToString(CultureInfo.InvariantCulture);
        var left = (100 - Percent(used)).ToString(CultureInfo.InvariantCulture);
        var reset = gauge.ResetLabel is { } label ? $", сброс {E(label)}" : "";

        return $"{Lamp(used)} <code>{bar}</code> {percent}% · осталось {left}% · {E(gauge.Title)}{reset}";
    }

    /// <summary>
    /// Расход в процентах, округление вверх — чтобы не обнадёживать. Остаток считается как
    /// <c>100 - Percent</c>, а не своим округлением: у 0.67 доля остатка в double равна
    /// 0.32999999999999996, и независимые округления давали «67% · осталось 32%». Поправка
    /// 1e-9 гасит ту же погрешность в другую сторону: без неё 0.67 даёт 67.00000000000001 и 68%.
    /// </summary>
    private static int Percent(double used) => Math.Clamp((int)Math.Ceiling(used * 100 - 1e-9), 0, 100);

    /// <summary>Цвет по итоговому остатку, а не по кадру: лампочка не должна мигать при заполнении.</summary>
    private static string Lamp(double used) => (1.0 - used) switch
    {
        <= 0.0 => "⛔",
        < 0.15 => "🔴",
        < 0.40 => "🟡",
        _ => "🟢",
    };
}
