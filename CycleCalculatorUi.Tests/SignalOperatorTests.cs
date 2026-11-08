using CycleCalculator.CycleModel.Fluids;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Editing;
using CycleCalculatorUi.Core.Persistence;
using CycleCalculatorUi.Core.Solving;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

/// <summary>Signal operators on the canvas: repeated inputs, output connections and controllers following them.</summary>
[TestFixture]
public class SignalOperatorTests
{
    private const string Operator = "SignalOperator";
    private const string Inputs = "inputs";
    private static readonly ComponentCatalog Catalog = ComponentCatalog.Default;
    private static readonly ComponentDescriptor Descriptor = Catalog.Get(Operator);

    /// <summary>CO2 cycle whose UA evaporator sets the suction pressure from the duty, with an unconnected operator.</summary>
    private static (EditorState State, NodeModel Compressor, NodeModel Operator) Editor()
    {
        DocumentBuilder builder = new(Catalog, "Operator", new FluidSelection(CoolPropFluids.CarbonDioxide, CoolPropBackends.HEOS));
        NodeModel comp = builder.Add("SimpleCompressor", "Comp1", 0, 0, ("dischargePressure", 90));
        builder.Choice(comp, "flowSpecification", "capacityControl").Choice(comp, "controlSign", "negative");
        NodeModel gasCooler = builder.Add("TemperatureBoundary", "GC", 200, 0, ("outletTemperature", 30));
        NodeModel valve = builder.Add("ExpansionValve", "EXV", 400, 0);
        builder.Choice(valve, "mode", "free");
        NodeModel evaporator = builder.Add("FixedUaEvaporator", "Evap", 600, 0, ("ua", 10));
        builder.Connect(comp, "Outlet", gasCooler, "Inlet").Connect(gasCooler, "Outlet", valve, "Inlet")
            .Connect(valve, "Outlet", evaporator, "Inlet").Connect(evaporator, "Outlet", comp, "Inlet");
        NodeModel op = builder.Add(Operator, "Op1", 0, -200);

        EditorState state = new(Catalog, new SolveService(Catalog));
        state.Load(builder.Build());
        return (state, state.Document.FindNode(comp.Id)!, state.Document.FindNode(op.Id)!);
    }

    private static PortRef Port(NodeModel node, string key) => new(node.Id, key);

    private static NodeModel Fresh(EditorState state, NodeModel node) => state.Document.FindNode(node.Id)!;

    private static string[] PortKeys(EditorState state, NodeModel node) =>
        Descriptor.PortsFor(Fresh(state, node)).Select(p => p.Key).ToArray();

    /// <summary>Minimum of the suction and discharge pressure, followed by the compressor.</summary>
    private static (EditorState State, NodeModel Compressor, NodeModel Operator) MinimumPressureControl()
    {
        (EditorState state, NodeModel comp, NodeModel op) = Editor();
        state.SetChoice(op.Id, "inputSignal", "pressure");
        state.SetChoice(op.Id, "inputSignal#2", "pressure");
        Assert.That(state.Connect(Port(comp, "Inlet"), Port(op, "In")), Is.Null);
        Assert.That(state.Connect(Port(op, "In#2"), Port(comp, "Outlet")), Is.Null, "either order works");
        Assert.That(state.Connect(Port(op, "Out"), Port(comp, "Signal")), Is.Null);
        state.SetNumber(comp.Id, Measurements.OperatorOutput.SetpointKey, 38);
        return (state, Fresh(state, comp), Fresh(state, op));
    }

    [Test]
    public async Task Compressor_FollowsTheMinimumOfItsInputs()
    {
        (EditorState state, NodeModel comp, NodeModel op) = MinimumPressureControl();
        Assert.That(comp.Choices[BuiltInComponents.ControlSignalKey], Is.EqualTo(Measurements.OperatorOutput.Key),
            "connecting to an operator output selects the only quantity it provides");
        Assert.That(state.Issues.Where(i => i.Severity == IssueSeverity.Error), Is.Empty);

        SolveResult result = await new SolveService(Catalog).SolveAsync(state.Document);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Port(comp.Id, "Inlet")!.Pressure, Is.EqualTo(38).Within(1e-5));
        IReadOnlyList<OutputValue> outputs = result.Outputs[op.Id];
        Assert.That(outputs[0].Value, Is.EqualTo(38).Within(1e-5));
        Assert.That(outputs[0].Unit, Is.EqualTo("bar"));
        Assert.That(outputs.Single(o => o.Label == "Input 2").Value, Is.EqualTo(90).Within(1e-6));
    }

    [Test]
    public void SavedDocument_LoadsAndValidates()
    {
        (EditorState state, _, _) = MinimumPressureControl();

        SystemDocument loaded = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(state.Document));

        Assert.That(new ModelBuilder(Catalog).Validate(loaded), Is.Empty);
    }

    [Test]
    public void Output_OnlyConnectsToSignalInputsAndCanFeedSeveral()
    {
        (EditorState state, NodeModel comp, NodeModel op) = Editor();

        Assert.That(state.Connect(Port(op, "Out"), Port(comp, "Inlet")), Does.Contain("signal input"));
        Assert.That(state.Connect(Port(op, "Out"), Port(op, "In")), Does.Contain("own output"));

        NodeModel second = state.AddNode(Operator, 200, -200);
        Assert.That(state.Connect(Port(op, "Out"), Port(second, "Out")), Does.Contain("Two signal outputs"));
        Assert.That(state.Connect(Port(op, "Out"), Port(comp, "Signal")), Is.Null);
        Assert.That(state.Connect(Port(op, "Out"), Port(second, "In")), Is.Null);
        Assert.That(Fresh(state, second).Choices["inputSignal"], Is.EqualTo(Measurements.OperatorOutput.Key));
    }

    [Test]
    public void ControllerMeasuringAPortQuantityOnAnOutput_IsAValidationError()
    {
        (EditorState state, NodeModel comp, NodeModel op) = MinimumPressureControl();
        state.SetChoice(comp.Id, BuiltInComponents.ControlSignalKey, "pressure");

        ValidationIssue issue = state.Issues.Single(i => i.NodeId == comp.Id && i.Severity == IssueSeverity.Error);

        Assert.That(issue.Message, Does.Contain(Measurements.OperatorOutput.Label));
        Assert.That(Catalog.Get("SimpleCompressor").FindParameter(BuiltInComponents.ControlSignalKey)!
            .OptionsShown(Fresh(state, comp), state.Document, Catalog).Select(o => o.Value),
            Is.EquivalentTo(new[] { "pressure", Measurements.OperatorOutput.Key }), "the output and the current choice");

        state.SetChoice(op.Id, "inputSignal", Measurements.OperatorOutput.Key);
        Assert.That(state.Issues.Any(i => i.NodeId == op.Id && i.PortKey == "In"), Is.True,
            "a fluid port provides no operator output");
    }

    [Test]
    public void Inputs_FollowTheInputCountAndConnectionsFollowTheirInput()
    {
        (EditorState state, NodeModel comp, NodeModel op) = MinimumPressureControl();
        Assert.That(PortKeys(state, op), Is.EqualTo(new[] { "In", "In#2", "Out" }));

        state.AddGroupItem(op.Id, Inputs);
        Assert.That(PortKeys(state, op), Is.EqualTo(new[] { "In", "In#2", "In#3", "Out" }));
        Assert.That(state.Issues.Any(i => i.NodeId == op.Id && i.PortKey == "In#3"), Is.True, "the new input is unconnected");

        state.RemoveGroupItem(op.Id, Inputs, 0);
        Assert.That(PortKeys(state, op), Is.EqualTo(new[] { "In", "In#2", "Out" }));
        ConnectionModel moved = state.Document.Connections.Single(c => c.To.NodeId == op.Id);
        Assert.That(moved.To.PortKey, Is.EqualTo("In"), "the discharge measurement moved up with its input");
        Assert.That(moved.From, Is.EqualTo(Port(comp, "Outlet")));

        state.Undo();
        Assert.That(state.Document.Connections.Count(c => c.To.NodeId == op.Id), Is.EqualTo(2));
    }

    [Test]
    public void ConstantOperation_HasOneInputAndDropsTheOthers()
    {
        (EditorState state, NodeModel comp, NodeModel op) = MinimumPressureControl();

        state.SetChoice(op.Id, BuiltInComponents.OperationKey, "Subtract");
        Assert.That(PortKeys(state, op), Is.EqualTo(new[] { "In", "Out" }));
        Assert.That(state.Document.Connections.Where(c => c.To.NodeId == op.Id).Select(c => c.To.PortKey),
            Is.EqualTo(new[] { "In" }));

        state.AddGroupItem(op.Id, Inputs);
        Assert.That(PortKeys(state, op), Is.EqualTo(new[] { "In", "Out" }), "the count is fixed");

        state.SetNumber(op.Id, "constant", 0);
        state.SetChoice(op.Id, BuiltInComponents.OperationKey, "Divide");
        BuildResult build = new ModelBuilder(Catalog).Build(state.Document);
        Assert.That(build.Issues.Any(i => i.NodeId == op.Id && i.ParameterKey == "constant"), Is.True);
    }

    [Test]
    public async Task ChainedOperators_SubtractAConstantFromTheMinimum()
    {
        (EditorState state, NodeModel comp, NodeModel min) = MinimumPressureControl();
        NodeModel minus = state.AddNode(Operator, 200, -200);
        state.SetChoice(minus.Id, BuiltInComponents.OperationKey, "Subtract");
        state.SetNumber(minus.Id, "constant", 3);
        state.Edit(document => document.Connections.RemoveAll(c => c.To == Port(comp, "Signal")));
        Assert.That(state.Connect(Port(min, "Out"), Port(minus, "In")), Is.Null);
        Assert.That(state.Connect(Port(minus, "Out"), Port(comp, "Signal")), Is.Null);
        state.SetNumber(comp.Id, Measurements.OperatorOutput.SetpointKey, 35);

        SolveResult result = await new SolveService(Catalog).SolveAsync(state.Document);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Port(comp.Id, "Inlet")!.Pressure, Is.EqualTo(38).Within(1e-5));
    }

    [Test]
    public void OperatorsFeedingEachOther_IsAValidationError()
    {
        (EditorState state, _, NodeModel a) = Editor();
        NodeModel b = state.AddNode(Operator, 200, -200);
        state.SetChoice(a.Id, BuiltInComponents.OperationKey, "Add");
        state.SetChoice(b.Id, BuiltInComponents.OperationKey, "Add");
        Assert.That(state.Connect(Port(a, "Out"), Port(b, "In")), Is.Null);
        Assert.That(state.Connect(Port(b, "Out"), Port(a, "In")), Is.Null);

        Assert.That(state.Issues.Select(i => i.Message), Has.Some.Contains("loop"));
    }
}
