using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Examples;
using CycleCalculatorUi.Core.Persistence;
using CycleCalculatorUi.Core.Solving;
using CycleCalculatorUi.Core.Studies;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

[TestFixture]
public class EnergyBalanceTests
{
    private readonly SolveService _service = new(ComponentCatalog.Default);

    public static IEnumerable<TestCaseData> Examples() =>
        ExampleSystems.All.Select(e => new TestCaseData(e.Title).SetName($"Balance closes: {e.Title}"));

    [TestCaseSource(nameof(Examples))]
    public async Task Examples_HeatInPlusPowerEqualsHeatOut(string title)
    {
        SystemDocument document = ExampleSystems.All.Single(e => e.Title == title).Create();
        SolveResult result = await _service.SolveAsync(document);
        Assert.That(result.Success, Is.True, result.Message);

        EnergySummary energy = EnergyBalance.Compute(document, ComponentCatalog.Default, result);
        Assert.That(energy.PowerIn, Is.GreaterThan(0));
        Assert.That(energy.HeatIn, Is.GreaterThan(0));
        Assert.That(energy.HeatOut, Is.GreaterThan(0));
        Assert.That(energy.Imbalance, Is.EqualTo(0).Within(1e-3 * energy.HeatOut));
        Assert.That(energy.CopHeating, Is.EqualTo(energy.HeatOut / energy.PowerIn).Within(1e-12));
    }

    [Test]
    public async Task TwoStage_ClassifiesComponents()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        SolveResult result = await _service.SolveAsync(document);
        EnergySummary energy = EnergyBalance.Compute(document, ComponentCatalog.Default, result);

        Assert.That(Role(document, energy, "Gas cooler"), Is.EqualTo(EnergyRole.HeatOutput));
        Assert.That(Role(document, energy, "Evaporator"), Is.EqualTo(EnergyRole.HeatInput));
        Assert.That(energy.Flows.Count(f => f.Role == EnergyRole.PowerInput), Is.EqualTo(3));
        Assert.That(energy.Flows.Any(f => f.Name == "EXV1"), Is.False);
    }

    [Test]
    public async Task Ejector_CountsWaterHeatExchangersButNotTheInternalOne()
    {
        SystemDocument document = ExampleSystems.EjectorHeatPump();
        SolveResult result = await _service.SolveAsync(document);
        Assert.That(result.Success, Is.True, result.Message);
        EnergySummary energy = EnergyBalance.Compute(document, ComponentCatalog.Default, result);

        Assert.That(Role(document, energy, "Gas cooler"), Is.EqualTo(EnergyRole.HeatOutput));
        Assert.That(Role(document, energy, "Evaporator"), Is.EqualTo(EnergyRole.HeatInput));
        Assert.That(energy.Flows.Any(f => f.Name == "IHX"), Is.False);
    }

    [Test]
    public async Task Override_ChangesOrRemovesContribution()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        SolveResult result = await _service.SolveAsync(document);
        EnergySummary before = EnergyBalance.Compute(document, ComponentCatalog.Default, result);

        document.Nodes.Single(n => n.Name == "Comp3").EnergyRole = EnergyRole.Ignore;
        document.Nodes.Single(n => n.Name == "Gas cooler").EnergyRole = EnergyRole.HeatInput;
        EnergySummary after = EnergyBalance.Compute(document, ComponentCatalog.Default, result);

        Assert.That(after.Flows.Count(f => f.Role == EnergyRole.PowerInput), Is.EqualTo(2));
        Assert.That(after.PowerIn, Is.LessThan(before.PowerIn));
        Assert.That(after.HeatOut, Is.EqualTo(0));
        Assert.That(after.HeatIn, Is.EqualTo(before.HeatIn + before.HeatOut).Within(1e-9));
    }

    [Test]
    public void EnergyRoleAndStudy_DoNotChangeFingerprint()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        string fingerprint = DocumentSerializer.Fingerprint(document);
        document.Nodes[0].EnergyRole = EnergyRole.Ignore;
        document.Study = new StudyDefinition { Variables = [new StudyVariable { NodeId = document.Nodes[0].Id, ParameterKey = "x" }] };
        Assert.That(DocumentSerializer.Fingerprint(document), Is.EqualTo(fingerprint));
    }

    private static EnergyRole? Role(SystemDocument document, EnergySummary energy, string name) =>
        energy.Flows.SingleOrDefault(f => f.NodeId == document.Nodes.Single(n => n.Name == name).Id)?.Role;
}

[TestFixture]
public class StudyTests
{
    private readonly SolveService _service = new(ComponentCatalog.Default);

    private StudyRunner Runner => new(ComponentCatalog.Default, _service);

    private static StudyVariable Variable(SystemDocument document, string node, string key, double start, double end, int steps) =>
        new() { NodeId = document.Nodes.Single(n => n.Name == node).Id, ParameterKey = key, Start = start, End = end, Steps = steps };

    [Test]
    public void Grid_IsEveryCombination_FirstVariableSlowest()
    {
        StudyDefinition study = new()
        {
            Variables =
            [
                new StudyVariable { Start = 1, End = 2, Steps = 2 },
                new StudyVariable { Start = 10, End = 30, Steps = 3 }
            ]
        };
        IReadOnlyList<double[]> grid = study.Grid();
        Assert.That(study.PointCount(), Is.EqualTo(6));
        Assert.That(grid.Select(p => (p[0], p[1])),
            Is.EqualTo(new[] { (1.0, 10.0), (1.0, 20.0), (1.0, 30.0), (2.0, 10.0), (2.0, 20.0), (2.0, 30.0) }));
    }

    [Test]
    public void LogSpacing_HasEqualRatios()
    {
        StudyVariable variable = new() { Start = 1, End = 100, Steps = 3, Spacing = StudySpacing.Logarithmic };
        Assert.That(variable.Values(), Is.EqualTo(new[] { 1.0, 10.0, 100.0 }).Within(1e-9));
    }

    [Test]
    public async Task TwoByThree_SolvesEveryPointWithItsValues()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        NodeModel gasCooler = document.Nodes.Single(n => n.Name == "Gas cooler");
        StudyDefinition study = new()
        {
            Variables =
            [
                Variable(document, "Gas cooler", "outletTemperature", 28, 32, 2),
                Variable(document, "EXV1", "outletPressure", 18, 22, 3)
            ],
            Outputs =
            [
                new StudyOutput { Kind = StudyOutputKind.Port, NodeId = gasCooler.Id, PortKey = "Outlet", Quantity = PortQuantity.Temperature }
            ]
        };

        List<StudyProgress> reports = [];
        StudyResult result = await Runner.RunAsync(document, study, new SyncProgress(reports.Add));

        Assert.That(result.Completed, Is.True);
        Assert.That(result.Points, Has.Count.EqualTo(6));
        Assert.That(result.Points.All(p => p.Success), Is.True, string.Join("; ", result.Points.Select(p => p.Message)));
        Assert.That(reports[^1], Is.EqualTo(new StudyProgress(6, 6, 0)));
        foreach (StudyPoint point in result.Points)
            Assert.That(point.Outputs[0], Is.EqualTo(point.Values[0]).Within(1e-6), "gas cooler outlet temperature");
        // A warmer gas cooler outlet gives less heating per unit of power.
        Assert.That(result.Points[3].Energy!.CopHeating, Is.LessThan(result.Points[0].Energy!.CopHeating));
        // The document itself is not changed.
        Assert.That(gasCooler.Numbers["outletTemperature"], Is.EqualTo(30));
    }

    [Test]
    public async Task WarmStart_GivesSameResultsAsColdStart()
    {
        SystemDocument document = ExampleSystems.Co2WithReceiver();
        StudyDefinition study = new() { Variables = [Variable(document, "Gas cooler", "outletTemperature", 25, 35, 4)] };

        StudyResult warm = await Runner.RunAsync(document, study);
        study.WarmStart = false;
        StudyResult cold = await Runner.RunAsync(document, study);

        Assert.That(warm.Points.All(p => p.Success) && cold.Points.All(p => p.Success), Is.True);
        for (int i = 0; i < warm.Points.Count; i++)
        {
            Assert.That(warm.Points[i].Energy!.HeatOut, Is.EqualTo(cold.Points[i].Energy!.HeatOut).Within(1e-4));
            Assert.That(warm.Points[i].Energy!.PowerIn, Is.EqualTo(cold.Points[i].Energy!.PowerIn).Within(1e-4));
        }
    }

    [TestCaseSource(typeof(EnergyBalanceTests), "Examples")]
    public async Task WarmStartedDocument_SolvesToTheSameState(string title)
    {
        SystemDocument document = ExampleSystems.All.Single(e => e.Title == title).Create();
        SolveResult cold = await _service.SolveAsync(document);
        SolveResult warm = await _service.SolveAsync(StudyRunner.WithWarmStart(document, cold));

        Assert.That(warm.Success, Is.True, warm.Message);
        foreach ((string node, IReadOnlyDictionary<string, PortResult> ports) in cold.Ports)
        foreach ((string port, PortResult state) in ports)
            Assert.That(warm.Port(node, port)!.Enthalpy, Is.EqualTo(state.Enthalpy).Within(1e-4));
    }

    [Test]
    public async Task OutOfRangeValue_FailsOnlyItsOwnPoint()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        // Isentropic efficiency must be at most 1.
        StudyDefinition study = new() { Variables = [Variable(document, "Comp1", "isentropicEfficiency", 0.8, 1.2, 3)] };

        StudyResult result = await Runner.RunAsync(document, study);

        Assert.That(result.Points.Select(p => p.Success), Is.EqualTo(new[] { true, true, false }));
        Assert.That(result.Points[2].Message, Does.Contain("Isentropic efficiency"));
    }

    [Test]
    public async Task Cancelling_StopsEarlyAndKeepsFinishedPoints()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        StudyDefinition study = new() { Variables = [Variable(document, "Gas cooler", "outletTemperature", 25, 35, 10)] };
        using CancellationTokenSource cancellation = new();

        StudyResult result = await Runner.RunAsync(document, study,
            new SyncProgress(p => { if (p.Done == 2) cancellation.Cancel(); }), cancellation.Token);

        Assert.That(result.Completed, Is.False);
        Assert.That(result.Points, Has.Count.EqualTo(2));
    }

    [Test]
    public void InvalidSetup_IsReported()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        StudyDefinition study = new()
        {
            Variables =
            [
                new StudyVariable { NodeId = "missing", ParameterKey = "x", Start = 1, End = 2 },
                Variable(document, "Comp1", "dischargePressure", -1, 10, 3)
            ]
        };
        study.Variables[1].Spacing = StudySpacing.Logarithmic;

        IReadOnlyList<string> problems = study.Validate(document, ComponentCatalog.Default);
        Assert.That(problems, Has.Count.EqualTo(2));
        Assert.ThrowsAsync<InvalidOperationException>(() => Runner.RunAsync(document, study));
    }

    [Test]
    public void Study_RoundTripsThroughFile()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        document.Study = new StudyDefinition
        {
            Variables = [Variable(document, "Comp1", "dischargePressure", 80, 100, 5)],
            Outputs = [new StudyOutput { Kind = StudyOutputKind.Component, NodeId = document.Nodes[0].Id, Label = "Power" }],
            WarmStart = false
        };
        document.Nodes[0].EnergyRole = EnergyRole.Ignore;

        SystemDocument loaded = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(document));

        Assert.That(loaded.Study!.Variables.Single().End, Is.EqualTo(100));
        Assert.That(loaded.Study.Outputs.Single().Label, Is.EqualTo("Power"));
        Assert.That(loaded.Study.WarmStart, Is.False);
        Assert.That(loaded.Nodes[0].EnergyRole, Is.EqualTo(EnergyRole.Ignore));
        Assert.That(DocumentSerializer.Deserialize("""{"version":1,"nodes":[],"connections":[]}""").Study, Is.Null);
    }

    private sealed class SyncProgress(Action<StudyProgress> report) : IProgress<StudyProgress>
    {
        public void Report(StudyProgress value) => report(value);
    }
}
