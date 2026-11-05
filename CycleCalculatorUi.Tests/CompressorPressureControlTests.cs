using CycleCalculator.CycleModel.Fluids;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Solving;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

[TestFixture]
public class CompressorPressureControlTests
{
    private readonly SolveService _service = new(ComponentCatalog.Default);

    /// <summary>CO2 cycle whose UA evaporator sets the suction pressure from the duty.</summary>
    private static (SystemDocument Document, NodeModel Compressor) Cycle(Action<NodeModel> configure, bool connectSignal = true)
    {
        DocumentBuilder builder = new(ComponentCatalog.Default, "Suction control",
            new FluidSelection(CoolPropFluids.CarbonDioxide, CoolPropBackends.HEOS));
        NodeModel comp = builder.Add("SimpleCompressor", "Comp1", 0, 0, ("dischargePressure", 90));
        NodeModel gasCooler = builder.Add("TemperatureBoundary", "GC", 200, 0, ("outletTemperature", 30));
        NodeModel valve = builder.Add("ExpansionValve", "EXV", 400, 0);
        builder.Choice(valve, "mode", "free");
        NodeModel evaporator = builder.Add("FixedUaEvaporator", "Evap", 600, 0, ("ua", 10));
        builder.Connect(comp, "Outlet", gasCooler, "Inlet").Connect(gasCooler, "Outlet", valve, "Inlet")
            .Connect(valve, "Outlet", evaporator, "Inlet").Connect(evaporator, "Outlet", comp, "Inlet");
        if (connectSignal)
            builder.Connect(comp, "Inlet", comp, "Signal", ConnectionKind.Signal);
        configure(comp);
        return (builder.Build(), comp);
    }

    [Test]
    public async Task SimpleCompressor_HoldsTheMeasuredSuctionPressure()
    {
        (SystemDocument document, NodeModel comp) = Cycle(node =>
        {
            node.Choices["flowSpecification"] = "pressureControl";
            node.Numbers["pressureSetpoint"] = 38;
            node.Choices["controlSign"] = "negative";
        });

        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Port(comp.Id, "Inlet")!.Pressure, Is.EqualTo(38).Within(1e-5));
        Assert.That(result.Outputs[comp.Id].Single(o => o.Label == "Pressure error").Value, Is.EqualTo(0).Within(1e-5));
    }

    [Test]
    public void PressureControlWithoutMeasurement_IsAValidationError()
    {
        (SystemDocument document, NodeModel comp) = Cycle(node => node.Choices["flowSpecification"] = "pressureControl",
            connectSignal: false);

        IReadOnlyList<ValidationIssue> issues = new ModelBuilder(ComponentCatalog.Default).Validate(document);

        Assert.That(issues.Any(i => i.NodeId == comp.Id && i.PortKey == "Signal"), Is.True);
    }

    [Test]
    public void FixedMassFlow_DoesNotNeedTheSignalInput()
    {
        (SystemDocument document, _) = Cycle(node => node.Numbers["massFlow"] = 0.5, connectSignal: false);

        Assert.That(new ModelBuilder(ComponentCatalog.Default).Validate(document), Is.Empty);
    }
}
