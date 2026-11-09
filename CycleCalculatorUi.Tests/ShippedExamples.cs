using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Examples;

namespace CycleCalculatorUi.Tests;

/// <summary>The example systems shipped with the program (the Examples folder copied next to the tests).</summary>
public static class ShippedExamples
{
    private static readonly ExampleLibrary Library = new(ExampleLibrary.ShippedDirectory);

    public static IReadOnlyList<ExampleSystem> All => Library.Examples;

    public static IReadOnlyList<string> Problems => Library.Problems;

    public static SystemDocument TwoStageWithParallelCompressor() => Get("Two-stage CO₂ with parallel compressor");

    public static SystemDocument Co2WithReceiver() => Get("CO₂ with receiver");

    /// <summary>
    /// Same system as TestSystemBuilder.ValueBoxSystemVariableGascoolerTemp in the backend tests, with the components
    /// in the same order (the backend's presolve is sensitive to it).
    /// </summary>
    public static SystemDocument EjectorHeatPump() => Get("CO₂ heat pump with ejector");

    private static SystemDocument Get(string title) =>
        Library.Find(title)?.Create() ?? throw new InvalidOperationException($"No shipped example named '{title}'.");
}
