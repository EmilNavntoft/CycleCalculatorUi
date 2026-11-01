using System.Globalization;

namespace CycleCalculatorUi.Shared;

/// <summary>Number formatting for displayed values. Always uses '.' as decimal separator.</summary>
public static class Format
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Pressure(double value) => value.ToString("0.00", Invariant);

    public static string Temperature(double value) => value.ToString("0.0", Invariant);

    public static string MassFlow(double value) => value.ToString("0.0000", Invariant);

    public static string Enthalpy(double value) => value.ToString("0.0", Invariant);

    /// <summary>Compact general formatting with up to <paramref name="digits"/> significant digits.</summary>
    public static string Number(double? value, int digits = 6) =>
        value is { } v ? v.ToString("G" + digits, Invariant) : "–";

    /// <summary>Parses user input, accepting both '.' and ',' as decimal separator.</summary>
    public static bool TryParse(string? text, out double value) =>
        double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, Invariant, out value) && double.IsFinite(value);
}
