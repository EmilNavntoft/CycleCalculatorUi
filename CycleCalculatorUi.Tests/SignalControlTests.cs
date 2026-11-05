using System.Text.Json.Nodes;
using CycleCalculator.CycleModel.Fluids;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Examples;
using CycleCalculatorUi.Core.Persistence;
using CycleCalculatorUi.Core.Solving;
using CycleCalculatorUi.Core.Studies;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

/// <summary>Controllers that choose what their signal input measures, and loading files from before that.</summary>
[TestFixture]
public class SignalControlTests
{
    private static readonly ComponentCatalog Catalog = ComponentCatalog.Default;
    private readonly SolveService _service = new(Catalog);

    /// <summary>Rewrites a current document as format 1 saved it: one control mode per measured quantity.</summary>
    private static string ToFormat1(SystemDocument document, Action<JsonObject> editNode)
    {
        JsonObject json = JsonNode.Parse(DocumentSerializer.Serialize(document))!.AsObject();
        json["version"] = 1;
        foreach (JsonObject node in json["nodes"]!.AsArray().Cast<JsonObject>())
            editNode(node);
        return json.ToJsonString();
    }

    private static void MoveSignalToMode(JsonObject node)
    {
        JsonObject choices = node["choices"]!.AsObject();
        if (choices["mode"]?.GetValue<string>() != "signal")
            return;
        choices["mode"] = choices["controlSignal"]!.GetValue<string>();
        choices.Remove("controlSignal");
    }

    [Test]
    public void Format1File_LoadsWithTheSameSettingsAsTheCurrentExample()
    {
        SystemDocument current = ExampleSystems.EjectorHeatPump();
        NodeModel ihx = current.Nodes.Single(n => n.TypeId == "FixedEfficiencyHeatExchanger");
        current.Study = new StudyDefinition
        {
            Variables = [new StudyVariable { NodeId = ihx.Id, ParameterKey = "temperatureSetpoint", Start = 100, End = 120 }],
            Outputs = [new StudyOutput { Kind = StudyOutputKind.Component, NodeId = ihx.Id, Label = "Control error" }]
        };

        string format1 = ToFormat1(current, node =>
        {
            MoveSignalToMode(node);
            if (node["typeId"]!.GetValue<string>() == "FixedEfficiencyHeatExchanger")
            {
                JsonObject numbers = node["numbers"]!.AsObject();
                numbers["targetTemperatureSetpoint"] = numbers["temperatureSetpoint"]!.DeepClone();
                numbers.Remove("temperatureSetpoint");
            }
        });
        format1 = format1.Replace("\"parameterKey\":\"temperatureSetpoint\"", "\"parameterKey\":\"targetTemperatureSetpoint\"")
            .Replace("\"Control error\"", "\"Temperature error\"");
        Assert.That(format1, Does.Contain("\"parameterKey\":\"targetTemperatureSetpoint\""));
        Assert.That(format1, Does.Contain("\"mode\":\"superheat\"").And.Contain("\"mode\":\"temperature\""));

        SystemDocument loaded = DocumentSerializer.Deserialize(format1);

        foreach (NodeModel expected in current.Nodes)
        {
            NodeModel actual = loaded.FindNode(expected.Id)!;
            Assert.That(actual.Choices, Is.EquivalentTo(expected.Choices), expected.Name);
            Assert.That(actual.Numbers, Is.EquivalentTo(expected.Numbers), expected.Name);
        }

        Assert.That(loaded.Version, Is.EqualTo(SystemDocument.CurrentVersion));
        Assert.That(loaded.Study!.Variables.Single().ParameterKey, Is.EqualTo("temperatureSetpoint"));
        Assert.That(loaded.Study.Outputs.Single().Label, Is.EqualTo("Control error"));
        Assert.That(new ModelBuilder(Catalog).Validate(loaded), Is.Empty);
    }

    [Test]
    public void Format1PumpEnvelopeSignal_LoadsAsTheExposedValue()
    {
        DocumentBuilder builder = new(Catalog, "Old", new FluidSelection(CoolPropFluids.Water, CoolPropBackends.HEOS));
        NodeModel comp = builder.Add("SimpleCompressor", "Comp1", 0, 0);
        builder.Choice(comp, "flowSpecification", "capacityControl").Choice(comp, "controlSignal", "Pump.envelopePosition");

        string format1 = ToFormat1(builder.Build(), _ => { }).Replace("Pump.envelopePosition", "pumpEnvelope");

        Assert.That(DocumentSerializer.Deserialize(format1).Nodes.Single().Choices["controlSignal"],
            Is.EqualTo("Pump.envelopePosition"));
    }

    /// <summary>A pump and a compressor in a water line; returns the compressor's measured-quantity parameter.</summary>
    private static (SystemDocument Document, NodeModel Compressor, NodeModel Pump, NodeModel Pipe) PumpLine()
    {
        DocumentBuilder builder = new(Catalog, "Line", new FluidSelection(CoolPropFluids.Water, CoolPropBackends.HEOS));
        NodeModel source = builder.Add("BoundaryCondition", "Source", 0, 0, ("pressure", 2), ("temperature", 20));
        builder.Choice(source, "thermal", "temperature");
        NodeModel comp = builder.Add("SimpleCompressor", "Comp1", 200, 0, ("dischargePressure", 3), ("massFlow", 1));
        NodeModel pump = builder.Add("Pump", "Pump1", 400, 0);
        builder.Choice(pump, "headUnit", "Bar");
        pump.Arrays["maxCurve"] = [2, 0, -0.01];
        pump.Arrays["minCurve"] = [0.5, 0, -0.01];
        NodeModel pipe = builder.Add("Pipe", "Pipe1", 600, 0, ("nominalPressureDrop", 1), ("nominalMassFlow", 2));
        NodeModel sink = builder.Add("BoundaryCondition", "Sink", 800, 0, ("pressure", 3));
        builder.Connect(source, "Port", comp, "Inlet").Connect(comp, "Outlet", pump, "Inlet")
            .Connect(pump, "Outlet", pipe, "Inlet").Connect(pipe, "Outlet", sink, "Port");
        return (builder.Build(), comp, pump, pipe);
    }

    private static string[] Shown(SystemDocument document, NodeModel node) =>
        Catalog.Find(node.TypeId)!.FindParameter(BuiltInComponents.ControlSignalKey)!
            .OptionsShown(node, document, Catalog).Select(o => o.Value).ToArray();

    private static void ConnectSignal(SystemDocument document, NodeModel measured, string port, NodeModel controller) =>
        document.Connections.Add(new ConnectionModel
        {
            Kind = ConnectionKind.Signal, From = new PortRef(measured.Id, port), To = new PortRef(controller.Id, "Signal")
        });

    [Test]
    public void MeasuredQuantityOptions_FollowTheSignalSource()
    {
        (SystemDocument document, NodeModel comp, NodeModel pump, NodeModel pipe) = PumpLine();
        comp.Choices["flowSpecification"] = "capacityControl";

        Assert.That(Shown(document, comp), Is.EquivalentTo(new[] { "pressure", "temperature", "superheat", "massFlow", "heatTransfer" }),
            "without a signal connection only port values are offered");

        ConnectSignal(document, pump, "Outlet", comp);
        Assert.That(Shown(document, comp), Does.Contain("Pump.envelopePosition").And.Not.Contain("heatTransfer"));

        document.Connections.RemoveAll(c => c.Kind == ConnectionKind.Signal);
        ConnectSignal(document, pipe, "Outlet", comp);
        Assert.That(Shown(document, comp), Does.Not.Contain("Pump.envelopePosition"));

        comp.Choices["controlSignal"] = "Pump.envelopePosition";
        Assert.That(Shown(document, comp), Does.Contain("Pump.envelopePosition"), "the current choice stays visible");
        ValidationIssue issue = new ModelBuilder(Catalog).Validate(document).Single(i => i.NodeId == comp.Id);
        Assert.That(issue.PortKey, Is.EqualTo("Signal"));
        Assert.That(issue.Message, Does.Contain("can only be measured on a pump"));
    }

    [Test]
    public void EverySetpointController_OffersTheSameQuantities()
    {
        foreach (string type in new[] { "SimpleCompressor", "En12900Compressor", "Pump", "ExpansionValve", "ThreeWayValve", "FixedEfficiencyHeatExchanger" })
        {
            ParameterDescriptor parameter = Catalog.Find(type)!.FindParameter(BuiltInComponents.ControlSignalKey)!;
            Assert.That(parameter.Options.Select(o => o.Value), Is.EqualTo(Measurements.All.Select(q => q.Key)), type);
            foreach (MeasuredQuantity quantity in Measurements.All)
                Assert.That(Catalog.Find(type)!.FindParameter(quantity.SetpointKey), Is.Not.Null, $"{type}: {quantity.Key}");
        }
    }

    [Test]
    public async Task Pump_HoldsAMassFlowMeasuredDownstream()
    {
        (SystemDocument document, NodeModel comp, NodeModel pump, NodeModel pipe) = PumpLine();
        comp.Numbers.Remove("massFlow");
        pump.Choices["control"] = "signal";
        pump.Choices["controlSignal"] = "massFlow";
        pump.Numbers["massFlowSetpoint"] = 1.5;

        Assert.That(new ModelBuilder(Catalog).Validate(document).Single().PortKey, Is.EqualTo("Signal"));
        ConnectSignal(document, pipe, "Inlet", pump);

        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Outputs[pump.Id].Single(o => o.Label == "Measured value").Value, Is.EqualTo(1.5).Within(1e-5));
        OutputValue error = result.Outputs[pump.Id].Single(o => o.Label == "Control error");
        Assert.That(error.Value, Is.EqualTo(0).Within(1e-5));
        Assert.That(error.Unit, Is.EqualTo("kg/s"));
    }

    [Test]
    public void ThreeWayValve_BuildsAPressureControl_WhichItsOldModesDidNotOffer()
    {
        NodeModel valve = new() { TypeId = "ThreeWayValve", Name = "3WV" };
        valve.Choices["mode"] = "signal";
        valve.Choices["controlSignal"] = "pressure";
        valve.Numbers["minimumOpening"] = 0.1;
        BuildContext context = new(valve, Catalog.Find("ThreeWayValve")!,
            [new FluidSelection(CoolPropFluids.CarbonDioxide, CoolPropBackends.HEOS)]);

        var component = (CycleCalculator.CycleModel.Model.ThreeWayValve)Catalog.Find("ThreeWayValve")!.Create(context);

        Assert.That(component.Control!.Measurement, Is.EqualTo(CycleCalculator.CycleModel.Model.Control.Measurement.Pressure));
        Assert.That(component.Control.Setpoint, Is.EqualTo(Measurements.Pressure.DefaultSetpoint));
        Assert.That((component.Control.Minimum, component.Control.Maximum), Is.EqualTo((0.1, 0.9)));
    }
}
