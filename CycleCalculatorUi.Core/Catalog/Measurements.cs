using CycleCalculator.CycleModel.Model.Control;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Catalog;

/// <summary>
/// A quantity a controller can measure through its signal input, with the parameter that holds its setpoint.
/// </summary>
/// <param name="Key">Value of the <c>controlSignal</c> choice.</param>
/// <param name="SetpointKey">Parameter key of the setpoint. Shared by all controllers, so values and studies carry over.</param>
public sealed record MeasuredQuantity(
    string Key,
    string Label,
    string Unit,
    string SetpointKey,
    double DefaultSetpoint,
    Func<Measurement> ToBackend)
{
    public double? Min { get; init; }

    public double? Max { get; init; }

    public bool MinExclusive { get; init; }

    public string Help { get; init; } = "";

    /// <summary>Unit of a difference of this quantity, e.g. a control error: K for a temperature in °C.</summary>
    public string DifferenceUnit => Unit == "°C" ? "K" : Unit;

    /// <summary>
    /// For a value exposed by one component type (e.g. the pump's envelope position): that type. Null for values
    /// that every fluid port provides.
    /// </summary>
    public string? SourceTypeId { get; init; }

    /// <summary>Only offered when the measured component exchanges heat (see <see cref="EnergyKind"/>).</summary>
    public bool NeedsHeatStream { get; init; }

    /// <summary>
    /// The value of a signal output (e.g. a signal operator's result) rather than of a fluid port. Signal outputs
    /// provide only such quantities, fluid ports only the others.
    /// </summary>
    public bool FromSignalOutput { get; init; }

    /// <summary>Whether the value can be read through a signal connection to <paramref name="source"/>.</summary>
    public bool IsAvailableFrom(SignalSource source) =>
        source.IsSignalOutput
            ? FromSignalOutput
            : !FromSignalOutput &&
              (SourceTypeId is null || source.Node.TypeId == SourceTypeId) &&
              (!NeedsHeatStream || source.Descriptor.EnergyKind is EnergyKind.Heat or EnergyKind.Exchanger);

    /// <summary>Why the value cannot be read from <paramref name="source"/>, or null when it can.</summary>
    public string? Problem(SignalSource source)
    {
        if (IsAvailableFrom(source))
            return null;
        NodeModel measured = source.Node;
        if (source.IsSignalOutput)
            return $"the signal input is connected to the output of '{measured.Name}', which only provides its " +
                   $"result. Measure '{Measurements.OperatorOutput.Label}' instead.";
        if (FromSignalOutput)
            return $"the signal input measures a port of '{measured.Name}', but the {Label.ToLowerInvariant()} " +
                   "can only be read from a signal operator. Connect it to an operator's output.";
        if (NeedsHeatStream)
            return $"the signal input measures '{measured.Name}', which does not exchange heat. Connect it to a " +
                   "port of the heat exchanger stream whose heat transfer should be controlled.";
        return $"the signal input measures '{measured.Name}', but the {Label.ToLowerInvariant()} can only be " +
               $"measured on a {SourceDisplayName}. Connect it to one of its ports.";
    }

    private string SourceDisplayName => SourceTypeId is null
        ? "fluid port"
        : ComponentCatalog.Default.Find(SourceTypeId)?.DisplayName.ToLowerInvariant() ?? SourceTypeId;
}

/// <summary>
/// Everything a controller can measure. Adding a value a backend component exposes
/// (<c>CycleComponent.ExposedValues</c>) takes one entry here with its <see cref="MeasuredQuantity.SourceTypeId"/>.
/// </summary>
public static class Measurements
{
    public static MeasuredQuantity Pressure { get; } = new("pressure", "Pressure", "bar", "pressureSetpoint", 30,
        () => Measurement.Pressure)
    {
        Min = 0, MinExclusive = true,
        Help = "Pressure of the measured port, e.g. a suction or receiver pressure."
    };

    public static MeasuredQuantity Temperature { get; } = new("temperature", "Temperature", "°C", "temperatureSetpoint", 0,
        () => Measurement.Temperature)
    {
        Min = -273.15, MinExclusive = true,
        Help = "Temperature of the measured port."
    };

    public static MeasuredQuantity Superheat { get; } = new("superheat", "Superheat", "K", "superheatSetpoint", 5,
        () => Measurement.Superheat)
    {
        Help = "Superheat of the measured port: its temperature above the saturation temperature."
    };

    public static MeasuredQuantity MassFlow { get; } = new("massFlow", "Mass flow", "kg/s", "massFlowSetpoint", 0.1,
        () => Measurement.MassFlow)
    {
        Min = 0,
        Help = "Mass flow through the measured port, counted positive in the port's design direction (into an " +
               "inlet, out of an outlet)."
    };

    public static MeasuredQuantity HeatTransfer { get; } = new("heatTransfer", "Heat transfer", "kW", "heatTransferSetpoint", 10,
        () => Measurement.HeatTransferRate)
    {
        Min = 0, MinExclusive = true, NeedsHeatStream = true,
        Help = "Heat transferred in the measured stream, e.g. an evaporator's cooling capacity, the heat a " +
               "condenser gives to its water stream, or the heat a fixed-outlet-temperature heater/cooler adds or " +
               "removes (connect to a port of that stream)."
    };

    public static MeasuredQuantity PumpEnvelopePosition { get; } = new("Pump.envelopePosition", "Pump envelope position",
        "-", "envelopeSetpoint", 0.8,
        () => Measurement.Exposed(CycleCalculator.CycleModel.Model.Pump.EnvelopePositionKey))
    {
        Min = 0, Max = 1, SourceTypeId = nameof(CycleCalculator.CycleModel.Model.Pump),
        Help = "Envelope position of the measured pump: 0 on its minimum curve, 1 on its maximum curve."
    };

    public static MeasuredQuantity OperatorOutput { get; } = new("operatorOutput", "Operator output", "",
        "operatorOutputSetpoint", 0, () => Measurement.Signal)
    {
        FromSignalOutput = true,
        Help = "Result of the signal operator the signal input is connected to, e.g. the minimum superheat of " +
               "several evaporators, in the unit of the operator's inputs."
    };

    public static IReadOnlyList<MeasuredQuantity> All { get; } =
        [Pressure, Temperature, Superheat, MassFlow, HeatTransfer, PumpEnvelopePosition, OperatorOutput];

    public static MeasuredQuantity? Find(string key) => All.FirstOrDefault(q => q.Key == key);

    /// <summary>The quantity of a backend measurement, e.g. for the unit of a control error.</summary>
    public static MeasuredQuantity? For(Measurement? measurement) =>
        measurement is null ? null : All.FirstOrDefault(q => q.ToBackend() == measurement);

    /// <summary>
    /// The quantities the signal input <paramref name="inputKey"/> of <paramref name="node"/> can choose from: those
    /// its source provides. Without a signal connection, the values every port or operator provides plus the current
    /// choice, so it doesn't disappear.
    /// </summary>
    public static IEnumerable<MeasuredQuantity> AvailableFor(NodeModel node, string inputKey, string currentKey,
        SystemDocument document, ComponentCatalog catalog) =>
        SourceOf(document, catalog, new PortRef(node.Id, inputKey)) is { } source
            ? All.Where(q => q.IsAvailableFrom(source) || q.Key == currentKey)
            : All.Where(q => (q.SourceTypeId is null && !q.FromSignalOutput) || q.Key == currentKey);

    /// <summary>What the signal input <paramref name="input"/> is connected to, or null when it is not connected.</summary>
    public static SignalSource? SourceOf(SystemDocument document, ComponentCatalog catalog, PortRef input)
    {
        ConnectionModel? signal = document.Connections.FirstOrDefault(c =>
            c.Kind == ConnectionKind.Signal && c.To == input);
        return signal is not null && document.FindNode(signal.From.NodeId) is { } measured &&
               catalog.Find(measured.TypeId) is { } descriptor &&
               descriptor.FindPort(measured, signal.From.PortKey) is { } port
            ? new SignalSource(measured, descriptor, port)
            : null;
    }
}
