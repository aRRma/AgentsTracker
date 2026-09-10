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
    public static decimal Progress(int frame) => (decimal)(frame + 1) / Frames;

    /// <summary>
    /// Блок «Расход тарифа» построчно, уже в HTML. Ошибку опроса пишем курсивом, а вместо
    /// пустого блока — «окон нет»: пустота выглядела бы как сломанный экран.
    /// </summary>
    public static string Render(LimitsView view, decimal progress)
    {
        if (view.Error is { } error) return $"<i>{E(error)}</i>";
        if (view.Windows.Count == 0) return "<i>окон нет</i>";

        return string.Join("\n", view.Windows.Select(w => Line(w, progress)));
    }

    private static string Line(LimitGauge gauge, decimal progress)
    {
        var used = Math.Clamp(gauge.Used, 0m, 1m);
        var shown = used * Math.Clamp(progress, 0m, 1m);
        var filled = (int)Math.Round(shown * Cells, MidpointRounding.AwayFromZero);

        var bar = new StringBuilder(Cells)
            .Append('▰', filled)
            .Append('▱', Cells - filled)
            .ToString();

        // Цифры сразу итоговые, кадрами двигается только полоса: пара «расход · остаток» посреди
        // анимации не должна складываться во что-то кроме 100%.
        var percent = LimitMath.Percent(used).ToString(CultureInfo.InvariantCulture);
        var left = LimitMath.Left(used).ToString(CultureInfo.InvariantCulture);
        var reset = gauge.ResetLabel is { } label ? $", сброс {E(label)}" : "";

        return $"{Lamp(used)} <code>{bar}</code> {percent}% · осталось {left}% · {E(gauge.Title)}{reset}";
    }

    /// <summary>Цвет по итоговому остатку, а не по кадру: лампочка не должна мигать при заполнении.</summary>
    private static string Lamp(decimal used) => (1m - used) switch
    {
        <= 0m => "⛔",
        < 0.15m => "🔴",
        < 0.40m => "🟡",
        _ => "🟢",
    };
}
