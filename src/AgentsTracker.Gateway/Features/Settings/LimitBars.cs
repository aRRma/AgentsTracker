using System.Globalization;
using System.Text;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Шкалы остатка тарифных окон. Графиков в чате нет, поэтому шкала — строка сегментов
/// в <c>&lt;code&gt;</c> (моноширинный шрифт держит одну ширину), а «анимация» — несколько
/// правок сообщения, на каждой шкала заполнена на долю <c>progress</c> от остатка.
/// </summary>
internal static class LimitBars
{
    /// <summary>Сколько правок на заполнение: больше красивее, но каналы режут частые правки.</summary>
    public const int Frames = 3;

    /// <summary>Пауза между кадрами; на трёх кадрах укладываемся в секунду.</summary>
    public static readonly TimeSpan FrameDelay = TimeSpan.FromMilliseconds(350);

    private const int Cells = 10;

    /// <summary>Доля заполнения кадра 0..1: первый почти пустой, последний — итог.</summary>
    public static double Progress(int frame) => (double)(frame + 1) / Frames;

    /// <summary>
    /// Блок «Остаток тарифа» построчно, уже в HTML. Ошибку опроса пишем курсивом, а вместо
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
        var shown = Math.Clamp(gauge.Remaining * Math.Clamp(progress, 0.0, 1.0), 0.0, 1.0);
        var filled = (int)Math.Round(shown * Cells, MidpointRounding.AwayFromZero);

        var bar = new StringBuilder(Cells)
            .Append('▰', filled)
            .Append('▱', Cells - filled)
            .ToString();

        // Округляем вниз, как и в сводке, — чтобы не обнадёживать.
        var percent = ((int)Math.Floor(shown * 100)).ToString(CultureInfo.InvariantCulture);
        var reset = gauge.ResetLabel is { } label ? $", сброс {E(label)}" : "";

        return $"{Lamp(gauge.Remaining)} <code>{bar}</code> {percent}% осталось · {E(gauge.Title)}{reset}";
    }

    /// <summary>Цвет по итоговому остатку, а не по кадру: лампочка не должна мигать при заполнении.</summary>
    private static string Lamp(double remaining) => remaining switch
    {
        <= 0.0 => "⛔",
        < 0.15 => "🔴",
        < 0.40 => "🟡",
        _ => "🟢",
    };
}
