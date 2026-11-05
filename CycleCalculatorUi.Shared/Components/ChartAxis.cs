using System.Globalization;

namespace CycleCalculatorUi.Shared.Components;

/// <summary>Axis ranges, ticks and number formatting shared by the SVG charts.</summary>
public static class ChartAxis
{
    /// <summary>A rounded range with about <paramref name="tickCount"/> ticks. With <paramref name="pad"/> the range
    /// is widened to whole ticks so the data does not sit on the frame.</summary>
    public static (double Min, double Max, double Step, IReadOnlyList<double> Ticks) Scale(
        double min, double max, bool pad, int tickCount = 5)
    {
        if (max - min < 1e-12 * Math.Max(1, Math.Abs(max)))
        {
            double half = Math.Max(Math.Abs(min) * 0.05, 1e-6);
            min -= half;
            max += half;
        }

        double step = NiceStep((max - min) / tickCount);
        double lo = pad ? Math.Floor(min / step) * step : min;
        double hi = pad ? Math.Ceiling(max / step) * step : max;
        List<double> ticks = [];
        for (double t = Math.Ceiling(lo / step - 1e-9) * step; t <= hi + step * 1e-9; t += step)
            ticks.Add(Math.Abs(t) < step * 1e-9 ? 0 : t);
        return (lo, hi, step, ticks);
    }

    public static double NiceStep(double raw)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double fraction = raw / magnitude;
        return (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10) * magnitude;
    }

    /// <summary>A tick label with just enough decimals for the step.</summary>
    public static string Tick(double value, double step)
    {
        int decimals = Math.Max(0, (int)Math.Ceiling(-Math.Log10(step) - 1e-9));
        if (step * Math.Pow(10, decimals) % 1 > 1e-9)
            decimals++;
        return value.ToString("F" + Math.Min(decimals, 8), CultureInfo.InvariantCulture);
    }

    /// <summary>An SVG coordinate.</summary>
    public static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
