using CycleCalculator.CycleModel.Fluids;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Solving;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

[TestFixture]
public class PumpTests
{
    private readonly SolveService _service = new(ComponentCatalog.Default);

    /// <summary>Water source (2 bar, 20 °C, 2 kg/s) → pump → free sink.</summary>
    private static (SystemDocument Document, NodeModel Pump) WaterLine(Action<NodeModel>? configure = null)
    {
        DocumentBuilder builder = new(ComponentCatalog.Default, "Pump test",
            new FluidSelection(CoolPropFluids.Water, CoolPropBackends.HEOS));
        NodeModel source = builder.Add("BoundaryCondition", "Source", 0, 0, ("massFlow", -2), ("pressure", 2), ("temperature", 20));
        builder.Choice(source, "thermal", "temperature");
        NodeModel pump = builder.Add("Pump", "Pump1", 200, 0);
        NodeModel sink = builder.Add("BoundaryCondition", "Sink", 400, 0);
        builder.Connect(source, "Port", pump, "Inlet").Connect(pump, "Outlet", sink, "Port");
        configure?.Invoke(pump);
        return (builder.Build(), pump);
    }

    private static double Output(SolveResult result, NodeModel node, string label) =>
        result.Outputs[node.Id].Single(o => o.Label == label).Value!.Value;

    [Test]
    public async Task DefaultPump_RunsOnMaximumCurveAndCountsAsPowerInput()
    {
        (SystemDocument document, NodeModel pump) = WaterLine();
        SolveResult result = await _service.SolveAsync(document);
        Assert.That(result.Success, Is.True, result.Message);

        double volumeFlow = Output(result, pump, "Volume flow");
        Assert.That(Output(result, pump, "Envelope position"), Is.EqualTo(1));
        Assert.That(Output(result, pump, "Head"), Is.EqualTo(30 - 0.02 * volumeFlow * volumeFlow).Within(1e-3));

        EnergySummary energy = EnergyBalance.Compute(document, ComponentCatalog.Default, result);
        Assert.That(energy.PowerIn, Is.EqualTo(Output(result, pump, "Power")).Within(1e-9));
        Assert.That(energy.PowerIn, Is.GreaterThan(0));
    }

    [Test]
    public async Task UnreachableSetpoint_StaysOnMaximumCurveAndReportsError()
    {
        (SystemDocument document, NodeModel pump) = WaterLine(node =>
        {
            node.Choices["control"] = "pressureRise";
            node.Numbers["pressureRiseSetpoint"] = 10;
        });
        SolveResult result = await _service.SolveAsync(document);
        Assert.That(result.Success, Is.True, result.Message);

        Assert.That(Output(result, pump, "Envelope position"), Is.EqualTo(1).Within(1e-6));
        Assert.That(Output(result, pump, "Setpoint error"), Is.LessThan(-5));
    }

    [Test]
    public void CrossedCurves_PointAtTheMaximumCurve()
    {
        (SystemDocument document, NodeModel pump) = WaterLine(node => node.Arrays["maxCurve"] = [5, 0, 0]);

        BuildResult build = new ModelBuilder(ComponentCatalog.Default).Build(document);

        Assert.That(build.Success, Is.False);
        Assert.That(build.Issues.Single(i => i.NodeId == pump.Id).ParameterKey, Is.EqualTo("maxCurve"));
    }

    [Test]
    public async Task PumpOnMaximumCurveIntoPipe_FlowFollowsFromBothCurves()
    {
        DocumentBuilder builder = new(ComponentCatalog.Default, "Pump and pipe",
            new FluidSelection(CoolPropFluids.Water, CoolPropBackends.HEOS));
        NodeModel source = builder.Add("BoundaryCondition", "Source", 0, 0, ("pressure", 2), ("temperature", 20));
        builder.Choice(source, "thermal", "temperature");
        NodeModel pump = builder.Add("Pump", "Pump1", 200, 0);
        NodeModel pipe = builder.Add("Pipe", "Pipe1", 400, 0, ("nominalPressureDrop", 2), ("nominalMassFlow", 2));
        NodeModel sink = builder.Add("BoundaryCondition", "Sink", 600, 0, ("pressure", 2));
        builder.Connect(source, "Port", pump, "Inlet").Connect(pump, "Outlet", pipe, "Inlet")
            .Connect(pipe, "Outlet", sink, "Port");

        SolveResult result = await _service.SolveAsync(builder.Build());
        Assert.That(result.Success, Is.True, result.Message);

        double massFlow = result.Port(pipe.Id, "Inlet")!.MassFlow;
        Assert.That(massFlow, Is.GreaterThan(2));
        Assert.That(Output(result, pipe, "Pressure drop"), Is.EqualTo(2 * Math.Pow(massFlow / 2, 2)).Within(1e-6));
        Assert.That(Output(result, pipe, "Pressure drop"), Is.EqualTo(Output(result, pump, "Pressure rise")).Within(1e-6));
    }
}
