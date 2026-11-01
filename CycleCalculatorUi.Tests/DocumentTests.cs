using CycleCalculator.CycleModel.Fluids;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Editing;
using CycleCalculatorUi.Core.Examples;
using CycleCalculatorUi.Core.Persistence;
using CycleCalculatorUi.Core.Solving;
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
        document.Connections[0].Points = [new CanvasPoint(10, 20)];
        Assert.That(DocumentSerializer.Fingerprint(document), Is.EqualTo(original));

        document.Nodes[0].Numbers["dischargePressure"] = 95;
        Assert.That(DocumentSerializer.Fingerprint(document), Is.Not.EqualTo(original));
    }

    [Test]
    public void ShapedRoutes_MoveWithBothEnds_AndSaveAndUndo()
    {
        EditorState state = new(ComponentCatalog.Default, new Core.Solving.SolveService(ComponentCatalog.Default));
        state.Load(ExampleSystems.Co2WithReceiver());
        ConnectionModel connection = state.Document.Connections[0];
        state.SetConnectionPoints(connection.Id, [new CanvasPoint(100, 50), new CanvasPoint(100, 200)]);

        connection = state.Document.Connections[0];
        Assert.That(connection.Points, Is.EqualTo(new[] { new CanvasPoint(100, 50), new CanvasPoint(100, 200) }));
        Assert.That(DocumentSerializer.Deserialize(DocumentSerializer.Serialize(state.Document)).Connections[0].Points,
            Is.EqualTo(connection.Points));

        NodeModel from = state.Document.FindNode(connection.From.NodeId)!;
        NodeModel to = state.Document.FindNode(connection.To.NodeId)!;

        // Only one end moves: corners stay put.
        state.MoveNodes(new Dictionary<string, (double, double)> { [from.Id] = (from.X + 32, from.Y) });
        Assert.That(state.Document.Connections[0].Points![0], Is.EqualTo(new CanvasPoint(100, 50)));

        // Both ends move together: corners move with them.
        state.MoveNodes(new Dictionary<string, (double, double)>
        {
            [from.Id] = (from.X + 16, from.Y + 32),
            [to.Id] = (to.X + 16, to.Y + 32)
        });
        Assert.That(state.Document.Connections[0].Points![0], Is.EqualTo(new CanvasPoint(116, 82)));

        state.SetConnectionPoints(connection.Id, null);
        Assert.That(state.Document.Connections[0].Points, Is.Null);
        state.Undo();
        Assert.That(state.Document.Connections[0].Points, Is.Not.Null);
    }

    [Test]
    public void FlowDirection_IsPositiveDownstream_AndFlagsReversedFlow()
    {
        SystemDocument document = ExampleSystems.EjectorHeatPump();
        ComponentCatalog catalog = ComponentCatalog.Default;
        NodeModel cooler = document.Nodes.Single(n => n.Name == "Gas cooler");
        NodeModel sinkIn = document.Nodes.Single(n => n.Name == "Water in (sink)");
        NodeModel sinkOut = document.Nodes.Single(n => n.Name == "Water out (sink)");
        PortResult into = new(0.5, 1, 1, 1), outOf = new(-0.5, 1, 1, 1);

        // The backend counts flow into a component as positive.
        Assert.That(FlowDirection.Of(document, catalog, new PortRef(cooler.Id, "Inlet"), into), Is.EqualTo(new FlowValue(0.5, false)));
        Assert.That(FlowDirection.Of(document, catalog, new PortRef(cooler.Id, "Outlet"), outOf), Is.EqualTo(new FlowValue(0.5, false)));
        Assert.That(FlowDirection.Of(document, catalog, new PortRef(cooler.Id, "Outlet"), into), Is.EqualTo(new FlowValue(-0.5, true)));

        // Boundary ports take their direction from what they are connected to.
        Assert.That(FlowDirection.Of(document, catalog, new PortRef(sinkIn.Id, "Port"), outOf), Is.EqualTo(new FlowValue(0.5, false)));
        Assert.That(FlowDirection.Of(document, catalog, new PortRef(sinkOut.Id, "Port"), into), Is.EqualTo(new FlowValue(0.5, false)));
        Assert.That(FlowDirection.Of(document, catalog, new PortRef(sinkOut.Id, "Port"), outOf).Reversed, Is.True);
    }
}
