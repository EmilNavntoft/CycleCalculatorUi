using CycleCalculator.CycleModel.Fluids;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Examples;

public sealed record ExampleSystem(string Title, string Description, Func<SystemDocument> Create);

/// <summary>Ready-made systems, matching systems used in the backend's end-to-end tests.</summary>
public static class ExampleSystems
{
    private static readonly FluidSelection Co2 = new(CoolPropFluids.CarbonDioxide, CoolPropBackends.HEOS);

    public static IReadOnlyList<ExampleSystem> All { get; } =
    [
        new("Two-stage CO₂ with parallel compressor",
            "Transcritical CO₂ booster-style cycle with a low-stage compressor and two high-stage compressors.",
            TwoStageWithParallelCompressor),
        new("CO₂ with receiver",
            "Transcritical CO₂ cycle with a flash receiver, flash gas bypass and low-temperature evaporator.",
            Co2WithReceiver),
        new("CO₂ heat pump with ejector",
            "Water-to-water CO₂ heat pump with an EN 12900 compressor, ejector, internal heat exchanger " +
            "controlled on discharge temperature and a superheat-controlled valve.",
            EjectorHeatPump)
    ];


    public static SystemDocument TwoStageWithParallelCompressor()
    {
        DocumentBuilder b = new(ComponentCatalog.Default, "Two-stage CO₂ with parallel compressor", Co2);
        NodeModel comp2 = b.Add("SimpleCompressor", "Comp2", 120, 80,
            ("isentropicEfficiency", 0.7), ("dischargePressure", 45), ("massFlow", 1));
        NodeModel comp1 = b.Add("SimpleCompressor", "Comp1", 320, 80,
            ("isentropicEfficiency", 0.7), ("dischargePressure", 90), ("massFlow", 1));
        NodeModel comp3 = b.Add("SimpleCompressor", "Comp3", 320, 240,
            ("isentropicEfficiency", 0.7), ("dischargePressure", 90), ("massFlow", 1));
        NodeModel merge = b.Add("Merge", "Merge1", 520, 80);
        NodeModel gasCooler = b.Add("TemperatureBoundary", "Gas cooler", 720, 80, ("outletTemperature", 30));
        NodeModel valve = b.Add("ExpansionValve", "EXV1", 720, 400, ("outletPressure", 20));
        NodeModel evaporator = b.Add("TemperatureBoundary", "Evaporator", 420, 400, ("outletTemperature", -8));
        NodeModel split = b.Add("Split", "Split2", 120, 400);

        b.Connect(comp1, "Outlet", merge, "Inlet")
            .Connect(comp3, "Outlet", merge, "Inlet2")
            .Connect(merge, "Outlet", gasCooler, "Inlet")
            .Connect(gasCooler, "Outlet", valve, "Inlet")
            .Connect(valve, "Outlet", evaporator, "Inlet")
            .Connect(evaporator, "Outlet", split, "Inlet")
            .Connect(split, "Outlet", comp2, "Inlet")
            .Connect(split, "Outlet2", comp3, "Inlet")
            .Connect(comp2, "Outlet", comp1, "Inlet");
        return b.Build();
    }

    public static SystemDocument Co2WithReceiver()
    {
        DocumentBuilder b = new(ComponentCatalog.Default, "CO₂ with receiver", Co2);
        NodeModel merge = b.Add("Merge", "Merge", 96, 64);
        NodeModel comp1 = b.Add("SimpleCompressor", "Comp1", 288, 64,
            ("isentropicEfficiency", 0.7), ("dischargePressure", 90), ("massFlow", null));
        NodeModel gasCooler = b.Add("TemperatureBoundary", "Gas cooler", 480, 64, ("outletTemperature", 30));
        NodeModel hpValve = b.Add("ExpansionValve", "HP valve", 672, 64, ("outletPressure", 45));
        NodeModel receiver = b.Add("Receiver", "Receiver", 864, 64);
        NodeModel bypassValve = b.Add("ExpansionValve", "Flash gas valve", 480, 224, ("outletPressure", 35));
        bypassValve.FlipHorizontal = true;
        NodeModel lpValve = b.Add("ExpansionValve", "LT valve", 1008, 368, ("outletPressure", 20));
        lpValve.Rotation = 90;
        NodeModel evaporator = b.Add("TemperatureBoundary", "LT evaporator", 768, 512, ("outletTemperature", -8));
        evaporator.FlipHorizontal = true;
        NodeModel comp2 = b.Add("SimpleCompressor", "Comp2", 480, 512,
            ("isentropicEfficiency", 0.7), ("dischargePressure", 35), ("massFlow", 1));
        comp2.FlipHorizontal = true;

        b.Connect(merge, "Outlet", comp1, "Inlet")
            .Connect(comp1, "Outlet", gasCooler, "Inlet")
            .Connect(gasCooler, "Outlet", hpValve, "Inlet")
            .Connect(hpValve, "Outlet", receiver, "Inlet")
            .Connect(receiver, "OutletGas", bypassValve, "Inlet")
            .Connect(bypassValve, "Outlet", merge, "Inlet2")
            .Connect(receiver, "OutletLiquid", lpValve, "Inlet")
            .Connect(lpValve, "Outlet", evaporator, "Inlet")
            .Connect(evaporator, "Outlet", comp2, "Inlet")
            .Connect(comp2, "Outlet", merge, "Inlet");
        return b.Build();
    }

    /// <summary>
    /// Same system as TestSystemBuilder.ValueBoxSystemVariableGascoolerTemp in the backend tests, with the
    /// components in the same order (the backend's presolve is sensitive to it) and fast TTSE property tables.
    /// </summary>
    public static SystemDocument EjectorHeatPump()
    {
        FluidSelection co2 = new(CoolPropFluids.CarbonDioxide, CoolPropBackends.TTSEANDHEOS);
        FluidSelection water = new(CoolPropFluids.Water, CoolPropBackends.TTSEANDHEOS);
        DocumentBuilder b = new(ComponentCatalog.Default, "CO₂ heat pump with ejector", co2);
        NodeModel comp = b.Add("En12900Compressor", "Comp1", 80, 80);
        NodeModel ejector = b.Add("FixedEfficiencyEjector", "Ejector", 704, 80, ("isentropicEfficiency", 0.3));
        NodeModel receiver = b.Add("Receiver", "Receiver", 912, 80);
        NodeModel gasCooler = b.Add("FixedUaHeatExchanger", "Gas cooler", 288, 80, ("ua", 30));
        NodeModel evaporator = b.Add("FixedUaHeatExchanger", "Evaporator", 704, 304, ("ua", 20));
        evaporator.FlipHorizontal = true;
        NodeModel valve = b.Add("ExpansionValve", "EXV", 912, 304, ("superheatSetpoint", 5));
        b.Choice(valve, "mode", "signal").Choice(valve, BuiltInComponents.ControlSignalKey, Measurements.Superheat.Key);
        valve.FlipHorizontal = true;
        NodeModel ihx = b.Add("FixedEfficiencyHeatExchanger", "IHX", 496, 80,
            ("temperatureSetpoint", 120), ("minEfficiency", 0.1), ("maxEfficiency", 0.9));
        b.Choice(ihx, "mode", "signal").Choice(ihx, BuiltInComponents.ControlSignalKey, Measurements.Temperature.Key);

        NodeModel sinkIn = b.Add("BoundaryCondition", "Water in (sink)", 336, 208, ("pressure", 2), ("temperature", 40));
        NodeModel sinkOut = b.Add("BoundaryCondition", "Water out (sink)", 240, 208, ("temperature", 80));
        NodeModel sourceIn = b.Add("BoundaryCondition", "Water in (source)", 656, 432, ("pressure", 2), ("temperature", 12));
        NodeModel sourceOut = b.Add("BoundaryCondition", "Water out (source)", 752, 432, ("temperature", 7));
        foreach (NodeModel boundary in new[] { sinkIn, sinkOut, sourceIn, sourceOut })
        {
            boundary.Rotation = 270; // port pointing up
            boundary.Choices["thermal"] = "temperature";
            boundary.Fluids = [water];
        }

        gasCooler.Fluids = [co2, water];
        evaporator.Fluids = [co2, water];

        b.Connect(comp, "Outlet", gasCooler, "Inlet")
            .Connect(gasCooler, "Outlet", ihx, "Inlet")
            .Connect(ihx, "Outlet", ejector, "InletMotive")
            .Connect(ejector, "OutletDischarge", receiver, "Inlet")
            .Connect(receiver, "OutletGas", ihx, "Inlet2")
            .Connect(ihx, "Outlet2", comp, "Inlet")
            .Connect(receiver, "OutletLiquid", valve, "Inlet")
            .Connect(valve, "Outlet", evaporator, "Inlet")
            .Connect(evaporator, "Outlet", ejector, "InletSuction")
            .Connect(sinkIn, "Port", gasCooler, "Inlet2")
            .Connect(gasCooler, "Outlet2", sinkOut, "Port")
            .Connect(sourceIn, "Port", evaporator, "Inlet2")
            .Connect(evaporator, "Outlet2", sourceOut, "Port")
            .Connect(comp, "Outlet", ihx, "Signal", ConnectionKind.Signal)
            .Connect(evaporator, "Outlet", valve, "Signal", ConnectionKind.Signal);
        return b.Build();
    }
}
