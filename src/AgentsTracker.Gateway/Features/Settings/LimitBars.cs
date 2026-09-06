using System.Globalization;
using System.Text;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Шкалы остатка тарифных окон для чата. В Telegram нет графики, поэтому шкала — строка из
/// сегментов в <c>&lt;code&gt;</c> (моноширинный шрифт держит их одной ширины), а «анимация» —
/// несколько правок сообщения, на каждой шкала заполнена на долю <c>progress</c> от остатка.
/// </summary>
internal static class LimitBars
{
    /// <summary>Сколько правок делает заполнение: больше — красивее, но Telegram режет частые правки 429.</summary>
    public const int Frames = 3;

    /// <summary>Пауза между кадрами; на трёх кадрах укладываемся в секунду.</summary>
    public static readonly TimeSpan FrameDelay = TimeSpan.FromMilliseconds(350);

    private const int Cells = 10;

    /// <summary>Доля заполнения кадра 0..1: последний кадр — итог, первый — почти пустая шкала.</summary>
    public static double Progress(int frame) => (double)(frame + 1) / Frames;

    /// <summary>
    /// Блок «Остаток тарифа» построчно, уже в HTML. Ошибка опроса — одной строкой курсивом;
    /// нет окон — «окон нет»: пустой блок выглядел бы как сломанный экран.
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

        // Процент округляем вниз, как и в сводке: «1%» честнее обнадёживающих «2%».
        var percent = ((int)Math.Floor(shown * 100)).ToString(CultureInfo.InvariantCulture);
        var reset = gauge.ResetLabel is { } label ? $", сброс {E(label)}" : "";

        return $"{Lamp(gauge.Remaining)} <code>{bar}</code> {percent}% осталось · {E(gauge.Title)}{reset}";
    }

    /// <summary>Цвет — по итоговому остатку, а не по кадру: лампочка не должна мигать во время заполнения.</summary>
    private static string Lamp(double remaining) => remaining switch
    {
        <= 0.0 => "⛔",
        < 0.15 => "🔴",
        < 0.40 => "🟡",
        _ => "🟢",
    };
}
