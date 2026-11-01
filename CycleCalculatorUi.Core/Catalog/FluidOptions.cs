using CycleCalculator.CycleModel.Fluids;

namespace CycleCalculatorUi.Core.Catalog;

/// <summary>Fluids and CoolProp backends offered in the editor. Any CoolProp fluid name can also be typed in.</summary>
public static class FluidOptions
{
    public static IReadOnlyList<ChoiceOption> Fluids { get; } =
    [
        new(CoolPropFluids.CarbonDioxide, "CO₂ (R744)"),
        new(CoolPropFluids.R134a, "R134a"),
        new("R290", "Propane (R290)"),
        new("R717", "Ammonia (R717)"),
        new("R1234yf", "R1234yf"),
        new("R32", "R32"),
        new("R410A", "R410A"),
        new(CoolPropFluids.Water, "Water"),
        new(CoolPropFluids.Air, "Air")
    ];

    public static IReadOnlyList<ChoiceOption> Backends { get; } =
    [
        new(CoolPropBackends.HEOS, "HEOS (exact, slower)"),
        new(CoolPropBackends.TTSEANDHEOS, "TTSE tables (fast)"),
        new(CoolPropBackends.BICUBICANDHEOS, "Bicubic tables (fast)"),
        new(CoolPropBackends.SVDSBTLANDHEOS, "SVD-SBTL tables")
    ];

    public static string FluidLabel(string fluid) =>
        Fluids.FirstOrDefault(option => option.Value == fluid)?.Label ?? fluid;
}
