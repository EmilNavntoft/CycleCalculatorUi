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
    public void Solver_StopsWhenCancelledMidSolve()
    {
        using CancellationTokenSource cts = new();
        int evaluations = 0;
        double[] Residuals(double[] x)
        {
            if (++evaluations == 5)
                cts.Cancel();
            return [x[0] * x[0] - 2, x[1] - x[0]];
        }

        Assert.Throws<OperationCanceledException>(() => NonlinearLeastSquaresSolver.SolveBounded(
            Residuals, [10, 0], 2, [double.NegativeInfinity, double.NegativeInfinity],
            [double.PositiveInfinity, double.PositiveInfinity], cancellationToken: cts.Token));
        Assert.That(evaluations, Is.LessThanOrEqualTo(6));
    }

    [Test]
    public async Task CancelledSolve_ReturnsCancelledResult()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        SolveResult result = await _service.SolveAsync(ExampleSystems.TwoStageWithParallelCompressor(), cts.Token);

        Assert.That(result.Success, Is.False);
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

    [Test]
    public async Task NonConvergingSystem_ReturnsLastIterateMarkedAsFailed()
    {
        // Two temperatures on one connection conflict in the nonlinear equations.
        DocumentBuilder builder = new(ComponentCatalog.Default, "Conflicting temperatures",
            new FluidSelection(CoolPropFluids.CarbonDioxide, CoolPropBackends.HEOS));
        NodeModel first = builder.Add("BoundaryCondition", "First", 0, 0,
            ("massFlow", 1), ("pressure", 45), ("temperature", 30));
        NodeModel second = builder.Add("BoundaryCondition", "Second", 100, 0, ("temperature", 50));
        SystemDocument document = builder
            .Choice(first, "thermal", "temperature")
            .Choice(second, "thermal", "temperature")
            .Connect(first, "Port", second, "Port")
            .Build();

        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.False);
        Assert.That(result.HasResults, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("did not converge"));
        Assert.That(result.Port(first.Id, "Port")!.Pressure, Is.EqualTo(45).Within(1e-9));
    }

    [Test]
    public async Task ThreeWayValve_MassFlowSetpoint_SplitsFlowToMeetIt()
    {
        // Same system as ThreeWayValveTests.GivenMassFlowSetpoint_ValveSplitsFlowToMeetItOrSaturates in the backend.
        DocumentBuilder builder = new(ComponentCatalog.Default, "Mass flow valve",
            new FluidSelection(CoolPropFluids.CarbonDioxide, CoolPropBackends.HEOS));
        NodeModel comp = builder.Add("SimpleCompressor", "Compressor", 0, 0,
            ("isentropicEfficiency", 1), ("dischargePressure", 20), ("massFlow", 1));
        NodeModel valve = builder.Add("ThreeWayValve", "Valve", 100, 0, ("massFlowSetpoint", 0.3));
        NodeModel warm = builder.Add("TemperatureBoundary", "Warm branch", 200, 0, ("outletTemperature", 40));
        NodeModel cool = builder.Add("TemperatureBoundary", "Cool branch", 200, 100, ("outletTemperature", 10));
        NodeModel merge = builder.Add("Merge", "Merge", 300, 0);
        SystemDocument document = builder
            .Choice(valve, "mode", "massFlow")
            .Connect(comp, "Outlet", valve, "Inlet")
            .Connect(valve, "Outlet", warm, "Inlet")
            .Connect(valve, "Outlet2", cool, "Inlet")
            .Connect(warm, "Outlet", merge, "Inlet")
            .Connect(cool, "Outlet", merge, "Inlet2")
            .Connect(merge, "Outlet", comp, "Inlet")
            .Connect(warm, "Inlet", valve, "Signal", ConnectionKind.Signal)
            .Build();

        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.True, result.Message);
        IReadOnlyList<OutputValue> outputs = result.Outputs[valve.Id];
        Assert.That(outputs.Single(o => o.Label == "Opening to outlet 1").Value, Is.EqualTo(0.3).Within(1e-6));
        Assert.That(outputs.Single(o => o.Label == "Mass flow error").Value, Is.EqualTo(0).Within(1e-6));
        Assert.That(outputs.Single(o => o.Label == "Temperature error").Value, Is.Null);
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
