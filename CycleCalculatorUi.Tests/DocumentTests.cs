using CycleCalculator.CycleModel.Fluids;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Examples;
using CycleCalculatorUi.Core.Persistence;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

[TestFixture]
public class DocumentTests
{
    private static readonly ComponentCatalog Catalog = ComponentCatalog.Default;
    private static readonly FluidSelection Co2 = new(CoolPropFluids.CarbonDioxide, CoolPropBackends.HEOS);
    private static readonly FluidSelection R134a = new(CoolPropFluids.R134a, CoolPropBackends.HEOS);

    [Test]
    public void Examples_AreValid()
    {
        foreach (ExampleSystem example in ExampleSystems.All)
        {
            IReadOnlyList<ValidationIssue> issues = new ModelBuilder(Catalog).Validate(example.Create());
            Assert.That(issues, Is.Empty, example.Title);
        }
    }

    [Test]
    public void EmptyDocument_IsAnError()
    {
        IReadOnlyList<ValidationIssue> issues = new ModelBuilder(Catalog).Validate(new SystemDocument());
        Assert.That(issues.Single().Severity, Is.EqualTo(IssueSeverity.Error));
    }

    [Test]
    public void UnconnectedPort_IsReportedOnThatPort()
    {
        DocumentBuilder b = new(Catalog, "test", Co2);
        NodeModel compressor = b.Add("SimpleCompressor", "Comp", 0, 0);

        IReadOnlyList<ValidationIssue> issues = new ModelBuilder(Catalog).Validate(b.Build());

        Assert.That(issues.Select(i => (i.NodeId, i.PortKey)),
            Is.EquivalentTo(new[] { (compressor.Id, "Inlet"), (compressor.Id, "Outlet") }));
    }

    [Test]
    public void OutOfRangeParameter_IsReportedOnThatParameter()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        NodeModel compressor = document.Nodes.First(n => n.TypeId == "SimpleCompressor");
        compressor.Numbers["isentropicEfficiency"] = 1.5;

        ValidationIssue issue = new ModelBuilder(Catalog).Validate(document).Single();

        Assert.That((issue.NodeId, issue.ParameterKey), Is.EqualTo((compressor.Id, "isentropicEfficiency")));
    }

    [Test]
    public void ControlledValve_RequiresSignal_AndIgnoresInactiveOutletPressure()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        NodeModel valve = document.Nodes.Single(n => n.TypeId == "ExpansionValve");
        valve.Choices["mode"] = "superheat";
        valve.Numbers["outletPressure"] = -5; // inactive in superheat mode, so not validated

        ValidationIssue issue = new ModelBuilder(Catalog).Validate(document).Single();
        Assert.That((issue.NodeId, issue.PortKey), Is.EqualTo((valve.Id, "Signal")));

        NodeModel evaporator = document.Nodes.Single(n => n.Name == "Evaporator");
        PortRef measured = new(evaporator.Id, "Outlet");
        ConnectionProposal? proposal = ConnectionRules.TryConnect(document, Catalog,
            new PortRef(valve.Id, "Signal"), measured, out _);
        Assert.That(proposal, Is.EqualTo(new ConnectionProposal(ConnectionKind.Signal, measured, new PortRef(valve.Id, "Signal"))));
        document.Connections.Add(new ConnectionModel { Kind = proposal!.Kind, From = proposal.From, To = proposal.To });

        Assert.That(new ModelBuilder(Catalog).Validate(document), Is.Empty);
        Assert.That(new ModelBuilder(Catalog).Build(document).Success, Is.True);
    }

    [Test]
    public void ConnectionRules_RejectInvalidAndNormaliseDirection()
    {
        DocumentBuilder b = new(Catalog, "test", Co2);
        NodeModel a = b.Add("SimpleCompressor", "A", 0, 0);
        NodeModel c = b.Add("SimpleCompressor", "C", 0, 0);
        SystemDocument document = b.Build();

        Assert.That(ConnectionRules.TryConnect(document, Catalog, new(a.Id, "Inlet"), new(c.Id, "Inlet"), out string? reason), Is.Null);
        Assert.That(reason, Does.Contain("inlets"));

        ConnectionProposal? proposal = ConnectionRules.TryConnect(document, Catalog, new(c.Id, "Inlet"), new(a.Id, "Outlet"), out _);
        Assert.That(proposal, Is.EqualTo(new ConnectionProposal(ConnectionKind.Fluid, new(a.Id, "Outlet"), new(c.Id, "Inlet"))));

        document.Connections.Add(new ConnectionModel { Kind = proposal!.Kind, From = proposal.From, To = proposal.To });
        Assert.That(ConnectionRules.TryConnect(document, Catalog, new(c.Id, "Outlet"), new(c.Id, "Inlet"), out reason), Is.Null);
        Assert.That(reason, Does.Contain("one connection"));
    }

    [Test]
    public void FluidCircuits_FollowConnections_AndConflictsAreErrors()
    {
        DocumentBuilder b = new(Catalog, "test", Co2);
        NodeModel a = b.Add("SimpleCompressor", "A", 0, 0);
        NodeModel c = b.Add("TemperatureBoundary", "C", 0, 0);
        NodeModel d = b.Add("TemperatureBoundary", "D", 0, 0);
        b.Connect(a, "Outlet", c, "Inlet");
        SystemDocument document = b.Build();

        IReadOnlyList<FluidCircuit> circuits = FluidCircuitResolver.Resolve(document, Catalog);
        Assert.That(circuits, Has.Count.EqualTo(2));

        FluidCircuitResolver.SetCircuitFluid(document, Catalog, new StreamRef(a.Id, 0), R134a);
        Assert.That(c.Fluids[0], Is.EqualTo(R134a));
        Assert.That(d.Fluids[0], Is.EqualTo(Co2));

        document.Connections.Add(new ConnectionModel { From = new(c.Id, "Outlet"), To = new(d.Id, "Inlet") });
        Assert.That(FluidCircuitResolver.Resolve(document, Catalog).Single().HasConflict, Is.True);
        Assert.That(new ModelBuilder(Catalog).Validate(document).Any(i => i.Message.Contains("different fluids")), Is.True);
    }

    [Test]
    public void Serializer_RoundTrips()
    {
        SystemDocument document = ExampleSystems.Co2WithReceiver();
        document.Nodes[0].Guesses.Add(new GuessModel(CycleCalculator.CycleModel.Model.BaseComponents.GuessQuantity.Pressure, 30, "Inlet"));

        string json = DocumentSerializer.Serialize(document);
        SystemDocument loaded = DocumentSerializer.Deserialize(json);

        Assert.That(DocumentSerializer.Serialize(loaded), Is.EqualTo(json));
        Assert.That(json, Does.Contain("\"Pressure\""));
    }

    [Test]
    public void Serializer_RejectsGarbageAndNewerVersions()
    {
        Assert.Throws<InvalidDataException>(() => DocumentSerializer.Deserialize("not json"));
        Assert.Throws<InvalidDataException>(() => DocumentSerializer.Deserialize("{\"version\": 999}"));
    }

    [Test]
    public void Fingerprint_IgnoresLayoutButNotParameters()
    {
        SystemDocument document = ExampleSystems.Co2WithReceiver();
        string original = DocumentSerializer.Fingerprint(document);

        document.Nodes[0].X += 100;
        document.Nodes[0].Rotation = 90;
        Assert.That(DocumentSerializer.Fingerprint(document), Is.EqualTo(original));

        document.Nodes[0].Numbers["dischargePressure"] = 95;
        Assert.That(DocumentSerializer.Fingerprint(document), Is.Not.EqualTo(original));
    }
}
