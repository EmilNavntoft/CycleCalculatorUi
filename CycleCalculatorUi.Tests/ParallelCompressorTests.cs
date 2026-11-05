using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Editing;
using CycleCalculatorUi.Core.Examples;
using CycleCalculatorUi.Core.Persistence;
using CycleCalculatorUi.Core.Solving;
using CycleCalculator.CycleModel.Model;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

/// <summary>EN 12900 compressors with several polynomial sets (compressors in parallel).</summary>
[TestFixture]
public class ParallelCompressorTests
{
    private const string Group = "compressors";

    private static (EditorState State, NodeModel Node) EditorWithCompressor()
    {
        EditorState state = new(ComponentCatalog.Default, new SolveService(ComponentCatalog.Default));
        state.Load(ExampleSystems.EjectorHeatPump());
        return (state, state.Document.Nodes.Single(n => n.Name == "Comp1"));
    }

    [Test]
    public void AddGroupItem_CopiesTheLastCompressorAndUndoRemovesIt()
    {
        (EditorState state, NodeModel node) = EditorWithCompressor();
        double[] massFlow = node.Arrays["massFlowPolynomial"]!;

        state.AddGroupItem(node.Id, Group);
        node = state.Document.FindNode(node.Id)!;

        Assert.That(node.GroupCounts[Group], Is.EqualTo(2));
        Assert.That(node.Arrays["massFlowPolynomial#2"], Is.EqualTo(massFlow));
        Assert.That(node.Arrays["massFlowPolynomial#2"], Is.Not.SameAs(node.Arrays["massFlowPolynomial"]));

        state.Undo();
        Assert.That(ComponentCatalog.Default.Find(node.TypeId)!.FindGroup(Group)!.Count(state.Document.FindNode(node.Id)!),
            Is.EqualTo(1));
    }

    [Test]
    public void RemoveGroupItem_MovesLaterCompressorsUpAndKeepsAtLeastOne()
    {
        (EditorState state, NodeModel node) = EditorWithCompressor();
        state.AddGroupItem(node.Id, Group);
        state.AddGroupItem(node.Id, Group);
        double[] third = [3, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        state.SetCoefficients(node.Id, "massFlowPolynomial#3", third);

        state.RemoveGroupItem(node.Id, Group, 1);
        node = state.Document.FindNode(node.Id)!;
        Assert.That(node.GroupCounts[Group], Is.EqualTo(2));
        Assert.That(node.Arrays["massFlowPolynomial#2"], Is.EqualTo(third));
        Assert.That(node.Arrays.ContainsKey("massFlowPolynomial#3"), Is.False);

        state.RemoveGroupItem(node.Id, Group, 0);
        state.RemoveGroupItem(node.Id, Group, 0); // the last compressor cannot be removed
        node = state.Document.FindNode(node.Id)!;
        Assert.That(node.GroupCounts[Group], Is.EqualTo(1));
        Assert.That(node.Arrays["massFlowPolynomial"], Is.EqualTo(third));
    }

    [Test]
    public void Validate_PointsAtTheInvalidCompressor()
    {
        (EditorState state, NodeModel node) = EditorWithCompressor();
        state.AddGroupItem(node.Id, Group);
        state.SetCoefficients(node.Id, "powerPolynomial#2", null);

        IReadOnlyList<ValidationIssue> issues = new ModelBuilder(ComponentCatalog.Default).Validate(state.Document);

        ValidationIssue issue = issues.Single(i => i.NodeId == node.Id);
        Assert.That(issue.ParameterKey, Is.EqualTo("powerPolynomial#2"));
        Assert.That(issue.Message, Does.Contain("Compressor 2: Power polynomial"));
    }

    [Test]
    public void OldDocumentsWithoutGroupCounts_HaveOneCompressor()
    {
        SystemDocument document = ExampleSystems.EjectorHeatPump();
        NodeModel node = document.Nodes.Single(n => n.Name == "Comp1");
        node.GroupCounts.Clear();
        document = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(document));

        Assert.That(new ModelBuilder(ComponentCatalog.Default).Validate(document), Is.Empty);
        Assert.That(ComponentCatalog.Default.Find(node.TypeId)!.FindGroup(Group)!.Count(document.Nodes.Single(n => n.Id == node.Id)),
            Is.EqualTo(1));
    }

    [Test]
    public async Task TwoIdenticalCompressors_ShareTheFlowAndAddUpThePower()
    {
        (EditorState state, NodeModel node) = EditorWithCompressor();
        state.AddGroupItem(node.Id, Group);

        SolveResult result = await new SolveService(ComponentCatalog.Default).SolveAsync(state.Document);

        Assert.That(result.Success, Is.True, result.Message);
        IReadOnlyList<OutputValue> outputs = result.Outputs[node.Id];
        double Output(string label) => outputs.Single(o => o.Label == label).Value!.Value;
        Assert.That(Output("Compressor 1 power"), Is.EqualTo(Output("Compressor 2 power")).Within(1e-9));
        Assert.That(Output("Power"), Is.EqualTo(2 * Output("Compressor 1 power")).Within(1e-9));
        Assert.That(result.Port(node.Id, "Inlet")!.MassFlow,
            Is.EqualTo(Output("Compressor 1 mass flow") + Output("Compressor 2 mass flow")).Within(1e-6));
    }

    private static En12900Compressor Build(SystemDocument document, NodeModel node)
    {
        BuildResult build = new ModelBuilder(ComponentCatalog.Default).Build(document);
        Assert.That(build.Success, Is.True, string.Join("; ", build.Issues.Select(i => i.Message)));
        return (En12900Compressor)build.ComponentsByNode[node.Id];
    }

    [Test]
    public void OnlyVariableSpeedCompressors_ShowAndUseAnOperatingFrequency()
    {
        (EditorState state, NodeModel node) = EditorWithCompressor();
        state.AddGroupItem(node.Id, Group);
        node = state.Document.FindNode(node.Id)!;
        node.Numbers["operatingFrequency"] = 40;    // compressor 1 stays fixed speed: ignored
        node.Choices["speedControl#2"] = "variable";
        node.Numbers["operatingFrequency#2"] = 60;

        ComponentDescriptor descriptor = ComponentCatalog.Default.Get(node.TypeId);
        List<string> keys = descriptor.ParameterInstances(node).Select(i => i.Key).ToList();
        Assert.That(keys, Does.Not.Contain("operatingFrequency"));
        Assert.That(keys, Does.Contain("operatingFrequency#2"));

        En12900Compressor compressor = Build(state.Document, node);
        Assert.That(compressor.Maps[0].IsFrequencyControlled, Is.False);
        Assert.That(compressor.Maps[0].OperatingFrequency, Is.Null);
        Assert.That(compressor.Maps[1].IsFrequencyControlled, Is.True);
        Assert.That(compressor.Maps[1].OperatingFrequency, Is.EqualTo(60));
    }

    [Test]
    public void SpeedControlWithOnlyFixedSpeedCompressors_IsAnError()
    {
        (EditorState state, NodeModel node) = EditorWithCompressor();
        node = state.Document.FindNode(node.Id)!;
        node.Choices["capacityControl"] = "signal";
        state.Document.Connections.Add(new ConnectionModel
        {
            Kind = ConnectionKind.Signal, From = new PortRef(node.Id, "Inlet"), To = new PortRef(node.Id, "Signal")
        });

        BuildResult build = new ModelBuilder(ComponentCatalog.Default).Build(state.Document);

        Assert.That(build.Success, Is.False);
        Assert.That(build.Issues.Single(i => i.NodeId == node.Id).Message, Does.Contain("variable speed"));

        node.Choices["speedControl"] = "variable";
        node.Numbers["operatingFrequency"] = 40;    // hidden while the controller sets the speed
        Assert.That(Build(state.Document, node).Maps[0].OperatingFrequency, Is.Null);
    }

    [Test]
    public void OldDocumentsWithAnOperatingFrequency_LoadAsVariableSpeed()
    {
        SystemDocument document = ExampleSystems.EjectorHeatPump();
        NodeModel node = document.Nodes.Single(n => n.Name == "Comp1");
        node.GroupCounts[Group] = 2;
        node.Numbers["operatingFrequency#2"] = 45;
        node.Arrays["massFlowPolynomial#2"] = node.Arrays["massFlowPolynomial"];
        node.Arrays["powerPolynomial#2"] = node.Arrays["powerPolynomial"];
        node.Choices.Remove("speedControl");
        string json = DocumentSerializer.Serialize(document);

        document = DocumentSerializer.Deserialize(json);
        node = document.Nodes.Single(n => n.Id == node.Id);

        Assert.That(node.Choices["speedControl"], Is.EqualTo("fixed"));
        Assert.That(node.Choices["speedControl#2"], Is.EqualTo("variable"));
        Assert.That(Build(document, node).Maps[1].OperatingFrequency, Is.EqualTo(45));
    }
}
