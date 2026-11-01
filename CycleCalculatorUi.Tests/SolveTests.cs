using CycleCalculator.CycleModel.Fluids;
using CycleCalculator.CycleModel.Model;
using CycleCalculator.CycleModel.Model.BaseComponents;
using CycleCalculator.CycleModel.Solver;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Examples;
using CycleCalculatorUi.Core.Solving;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

[TestFixture]
public class SolveTests
{
    private readonly SolveService _service = new(ComponentCatalog.Default);

    [Test]
    public async Task TwoStageExample_MatchesBackendReferenceValues()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(Port(document, result, "Merge1", "Outlet").MassFlow, Is.EqualTo(-2).Within(1e-9));
        Assert.That(Port(document, result, "Split2", "Outlet").MassFlow, Is.EqualTo(-1).Within(1e-9));
        Assert.That(Port(document, result, "Comp1", "Inlet").Pressure, Is.EqualTo(45).Within(1e-9));
        Assert.That(Port(document, result, "Evaporator", "Outlet").Pressure, Is.EqualTo(20).Within(1e-9));
    }

    [Test]
    public async Task TwoStageExample_MatchesSameSystemBuiltDirectlyInBackend()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        SolveResult result = await _service.SolveAsync(document);
        Assert.That(result.Success, Is.True, result.Message);

        // Same system as TestSystemBuilder.TwoStageWithParallelComp in the backend tests.
        const string co2 = CoolPropFluids.CarbonDioxide, heos = CoolPropBackends.HEOS;
        SimpleCompressor comp1 = new("Comp1", co2, heos, 0.7, 90, 1);
        SimpleCompressor comp2 = new("Comp2", co2, heos, 0.7, 45, 1);
        SimpleCompressor comp3 = new("Comp3", co2, heos, 0.7, 90, 1);
        Merge merge = new("Merge1", co2, heos);
        Split split = new("Split2", co2, heos);
        TemperatureBoundary tb1 = new("Gas cooler", co2, heos, 30);
        TemperatureBoundary tb2 = new("Evaporator", co2, heos, -8);
        ExpansionValve exp1 = new("EXV1", co2, heos, 20);
        comp1.Outlet.ConnectTo(merge.Inlet2);
        merge.Inlet.ConnectTo(comp3.Outlet);
        merge.Outlet.ConnectTo(tb1.Inlet);
        tb1.Outlet.ConnectTo(exp1.Inlet);
        exp1.Outlet.ConnectTo(tb2.Inlet);
        tb2.Outlet.ConnectTo(split.Inlet);
        split.Outlet.ConnectTo(comp2.Inlet);
        split.Outlet2.ConnectTo(comp3.Inlet);
        comp2.Outlet.ConnectTo(comp1.Inlet);
        Orchestrator.Solve([comp1, comp2, comp3, split, merge, tb1, tb2, exp1]);

        void Compare(string node, string port, CycleCalculator.CycleModel.Model.Connectors.Port expected)
        {
            PortResult actual = Port(document, result, node, port);
            Assert.That(actual.MassFlow, Is.EqualTo(expected.MassFlow).Within(1e-6), $"{node}.{port} mass flow");
            Assert.That(actual.Pressure, Is.EqualTo(expected.Pressure).Within(1e-6), $"{node}.{port} pressure");
            Assert.That(actual.Enthalpy, Is.EqualTo(expected.Enthalpy).Within(1e-4), $"{node}.{port} enthalpy");
            Assert.That(actual.Temperature, Is.EqualTo(expected.Temperature).Within(1e-4), $"{node}.{port} temperature");
        }

        Compare("Comp1", "Outlet", comp1.Outlet);
        Compare("Comp2", "Outlet", comp2.Outlet);
        Compare("Comp3", "Inlet", comp3.Inlet);
        Compare("Gas cooler", "Outlet", tb1.Outlet);
        Compare("EXV1", "Outlet", exp1.Outlet);
        Compare("Merge1", "Outlet", merge.Outlet);
    }

    [Test]
    public async Task ReceiverExample_Solves()
    {
        SystemDocument document = ExampleSystems.Co2WithReceiver();
        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.True, result.Message);
        PortResult inlet = Port(document, result, "Receiver", "Inlet");
        PortResult gas = Port(document, result, "Receiver", "OutletGas");
        PortResult liquid = Port(document, result, "Receiver", "OutletLiquid");
        Assert.That(inlet.MassFlow + gas.MassFlow + liquid.MassFlow, Is.EqualTo(0).Within(1e-9));
        Assert.That(result.Outputs[NodeId(document, "HP valve")].Single(o => o.Label == "Outlet pressure").Value,
            Is.EqualTo(45).Within(1e-9));
    }

    [Test]
    public async Task InvalidDocument_ReturnsIssuesInsteadOfThrowing()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        document.Connections.RemoveAt(0);

        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Issues, Is.Not.Empty);
    }

    [Test]
    public async Task UnderSpecifiedSystem_ReportsSolverMessage()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        foreach (NodeModel compressor in document.Nodes.Where(n => n.TypeId == "SimpleCompressor"))
            compressor.Numbers["massFlow"] = null;

        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Is.Not.Empty);
    }

    private static string NodeId(SystemDocument document, string name) => document.Nodes.Single(n => n.Name == name).Id;

    private static PortResult Port(SystemDocument document, SolveResult result, string node, string port) =>
        result.Port(NodeId(document, node), port) ?? throw new AssertionException($"No result for {node}.{port}");
}

[TestFixture]
public class EjectorExampleTests
{
    [Test]
    public async Task EjectorHeatPump_SolvesWithSignalConnectionsAndTwoFluids()
    {
        SystemDocument document = ExampleSystems.EjectorHeatPump();
        Assert.That(new CycleCalculatorUi.Core.Building.ModelBuilder(ComponentCatalog.Default).Validate(document), Is.Empty);

        SolveResult result = await new SolveService(ComponentCatalog.Default).SolveAsync(document);

        Assert.That(result.Message, Does.StartWith("Solved"));
        string Id(string name) => document.Nodes.Single(n => n.Name == name).Id;
        Assert.That(result.Port(Id("Water out (sink)"), "Port")!.Temperature, Is.EqualTo(80).Within(0.01));
        Assert.That(result.Port(Id("Water in (source)"), "Port")!.Temperature, Is.EqualTo(12).Within(0.01));
        Assert.That(result.Port(Id("IHX"), "Inlet")!.MassFlow, Is.GreaterThan(0));
        Assert.That(result.Outputs[Id("Comp1")].Single(o => o.Label == "Power").Value, Is.GreaterThan(0));
    }
}
