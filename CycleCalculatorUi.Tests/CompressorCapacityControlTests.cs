using CycleCalculator.CycleModel.Fluids;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Solving;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

[TestFixture]
public class CompressorCapacityControlTests
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
            node.Choices["flowSpecification"] = "capacityControl";
            node.Numbers["pressureSetpoint"] = 38;
            node.Choices["controlSign"] = "negative";
        });

        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Port(comp.Id, "Inlet")!.Pressure, Is.EqualTo(38).Within(1e-5));
        Assert.That(result.Outputs[comp.Id].Single(o => o.Label == "Control error").Value, Is.EqualTo(0).Within(1e-5));
    }

    [Test]
    public async Task En12900Compressor_WithInterpolatedData_HoldsTheSuctionPressure()
    {
        // Example polynomials measured at 30–70 Hz, with a slightly non-linear frequency dependence.
        CompressorPoint example = CompressorLibrary.ExampleModel.Variants[0].Points[0];
        static double Factor(double f) => f / 50 * (1 + 0.002 * (f - 50));
        CompressorModel model = CompressorLibrary.ExampleModel with
        {
            Name = "Interpolated",
            Variants =
            [
                new CompressorVariant
                {
                    Speed = CompressorSpeed.Variable, NetFrequency = 50,
                    Scaling = CycleCalculator.CycleModel.Model.En12900.En12900FrequencyScaling.Interpolated,
                    Points = [.. new[] { 30.0, 40, 50, 60, 70 }.Select(f => new CompressorPoint(f,
                        [.. example.MassFlow.Select(c => c * Factor(f))], [.. example.Power.Select(c => c * Factor(f))], null))]
                }
            ]
        };

        DocumentBuilder builder = new(ComponentCatalog.Default, "EN 12900 suction control",
            new FluidSelection(CoolPropFluids.CarbonDioxide, CoolPropBackends.HEOS));
        NodeModel comp = builder.Add("En12900Compressor", "Comp1", 0, 0, ("dischargePressure", 90), ("pressureSetpoint", 38));
        builder.Choice(comp, "capacityControl", "signal").Choice(comp, "controlSign", "negative")
            .Choice(comp, BuiltInComponents.SpeedControlKey, "variable");
        En12900Library.Apply(comp, 0, model);
        NodeModel gasCooler = builder.Add("TemperatureBoundary", "GC", 200, 0, ("outletTemperature", 30));
        NodeModel valve = builder.Add("ExpansionValve", "EXV", 400, 0);
        builder.Choice(valve, "mode", "free");
        NodeModel evaporator = builder.Add("FixedUaEvaporator", "Evap", 600, 0, ("ua", 10));
        builder.Connect(comp, "Outlet", gasCooler, "Inlet").Connect(gasCooler, "Outlet", valve, "Inlet")
            .Connect(valve, "Outlet", evaporator, "Inlet").Connect(evaporator, "Outlet", comp, "Inlet")
            .Connect(comp, "Inlet", comp, "Signal", ConnectionKind.Signal);

        SolveResult result = await _service.SolveAsync(builder.Build());

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Port(comp.Id, "Inlet")!.Pressure, Is.EqualTo(38).Within(1e-5));
        double frequency = result.Outputs[comp.Id].Single(o => o.Label == "Controlled frequency").Value!.Value;
        Assert.That(frequency, Is.InRange(25, 70));
        TestContext.Out.WriteLine($"Controlled frequency: {frequency:F2} Hz");
    }

    [Test]
    public void PressureControlWithoutMeasurement_IsAValidationError()
    {
        (SystemDocument document, NodeModel comp) = Cycle(node => node.Choices["flowSpecification"] = "capacityControl",
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

    /// <summary>
    /// Water line: source (2 bar) → compressor (3 bar) → pump (free) → pipe → sink (3 bar), with the compressor's
    /// signal input on <paramref name="measured"/>.
    /// </summary>
    private static (SystemDocument Document, NodeModel Compressor, NodeModel Pump) PumpLine(string measured)
    {
        DocumentBuilder builder = new(ComponentCatalog.Default, "Envelope control",
            new FluidSelection(CoolPropFluids.Water, CoolPropBackends.HEOS));
        NodeModel source = builder.Add("BoundaryCondition", "Source", 0, 0, ("pressure", 2), ("temperature", 20));
        builder.Choice(source, "thermal", "temperature");
        NodeModel comp = builder.Add("SimpleCompressor", "Comp1", 200, 0, ("dischargePressure", 3), ("envelopeSetpoint", 0.5));
        builder.Choice(comp, "flowSpecification", "capacityControl").Choice(comp, "controlSignal", "Pump.envelopePosition");
        NodeModel pump = builder.Add("Pump", "Pump1", 400, 0);
        builder.Choice(pump, "headUnit", "Bar").Choice(pump, "control", "free");
        pump.Arrays["maxCurve"] = [2, 0, -0.01];
        pump.Arrays["minCurve"] = [0.5, 0, -0.01];
        NodeModel pipe = builder.Add("Pipe", "Pipe1", 600, 0, ("nominalPressureDrop", 1), ("nominalMassFlow", 2));
        NodeModel sink = builder.Add("BoundaryCondition", "Sink", 800, 0, ("pressure", 3));
        builder.Connect(source, "Port", comp, "Inlet").Connect(comp, "Outlet", pump, "Inlet")
            .Connect(pump, "Outlet", pipe, "Inlet").Connect(pipe, "Outlet", sink, "Port")
            .Connect(measured == "pump" ? pump : pipe, "Outlet", comp, "Signal", ConnectionKind.Signal);
        return (builder.Build(), comp, pump);
    }

    [Test]
    public async Task SimpleCompressor_HoldsThePumpEnvelopePosition()
    {
        (SystemDocument document, NodeModel comp, NodeModel pump) = PumpLine("pump");

        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Outputs[pump.Id].Single(o => o.Label == "Envelope position").Value, Is.EqualTo(0.5).Within(1e-5));
        OutputValue error = result.Outputs[comp.Id].Single(o => o.Label == "Control error");
        Assert.That(error.Value, Is.EqualTo(0).Within(1e-5));
        Assert.That(error.Unit, Is.EqualTo("-"));
    }

    [Test]
    public void PumpEnvelopeSignalFromAnotherComponent_IsAValidationError()
    {
        (SystemDocument document, NodeModel comp, _) = PumpLine("pipe");

        IReadOnlyList<ValidationIssue> issues = new ModelBuilder(ComponentCatalog.Default).Validate(document);

        ValidationIssue issue = issues.Single(i => i.NodeId == comp.Id);
        Assert.That(issue.PortKey, Is.EqualTo("Signal"));
        Assert.That(issue.Message, Does.Contain("can only be measured on a pump"));
    }

    [Test]
    public async Task SimpleCompressor_HoldsTheEvaporatorCoolingCapacity()
    {
        (SystemDocument document, NodeModel comp) = Cycle(node =>
        {
            node.Choices["flowSpecification"] = "capacityControl";
            node.Choices["controlSignal"] = "heatTransfer";
            node.Numbers["heatTransferSetpoint"] = 50;
        }, connectSignal: false);
        NodeModel evaporator = document.Nodes.Single(n => n.Name == "Evap");
        document.Connections.Add(new ConnectionModel
        {
            Kind = ConnectionKind.Signal,
            From = new PortRef(evaporator.Id, "Outlet"),
            To = new PortRef(comp.Id, "Signal")
        });

        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Outputs[evaporator.Id].Single(o => o.Label == "Cooling capacity").Value, Is.EqualTo(50).Within(1e-3));
        Assert.That(result.Outputs[comp.Id].Single(o => o.Label == "Control error").Unit, Is.EqualTo("kW"));
    }

    [Test]
    public void HeatTransferSignalFromAComponentWithoutHeat_IsAValidationError()
    {
        // The default signal connection measures the compressor's own inlet.
        (SystemDocument document, NodeModel comp) = Cycle(node =>
        {
            node.Choices["flowSpecification"] = "capacityControl";
            node.Choices["controlSignal"] = "heatTransfer";
        });

        IReadOnlyList<ValidationIssue> issues = new ModelBuilder(ComponentCatalog.Default).Validate(document);

        Assert.That(issues.Single(i => i.NodeId == comp.Id).Message, Does.Contain("does not exchange heat"));
    }

    [Test]
    public async Task SimpleCompressor_HoldsTheTemperatureBoundaryHeatRejection()
    {
        (SystemDocument document, NodeModel comp) = Cycle(node =>
        {
            node.Choices["flowSpecification"] = "capacityControl";
            node.Choices["controlSignal"] = "heatTransfer";
            node.Numbers["heatTransferSetpoint"] = 60;
        }, connectSignal: false);
        NodeModel gasCooler = document.Nodes.Single(n => n.Name == "GC");
        document.Connections.Add(new ConnectionModel
        {
            Kind = ConnectionKind.Signal,
            From = new PortRef(gasCooler.Id, "Outlet"),
            To = new PortRef(comp.Id, "Signal")
        });

        Assert.That(new ModelBuilder(ComponentCatalog.Default).Validate(document), Is.Empty);
        SolveResult result = await _service.SolveAsync(document);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Outputs[gasCooler.Id].Single(o => o.Label == "Heat flow").Value, Is.EqualTo(60).Within(1e-3));
        Assert.That(result.Port(gasCooler.Id, "Outlet")!.Temperature, Is.EqualTo(30).Within(1e-6), "the boundary still sets its outlet temperature");
    }
}
