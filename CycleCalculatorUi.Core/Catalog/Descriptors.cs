using CycleCalculator.CycleModel.Model;
using CycleCalculator.CycleModel.Model.BaseComponents;
using CycleCalculator.CycleModel.Model.Connectors;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Catalog;

public enum PortKind
{
    Fluid,
    SignalInput,

    /// <summary>A computed value other components' signal inputs can measure, e.g. a signal operator's result.</summary>
    SignalOutput
}

/// <summary>
/// A port of a component type. <see cref="X"/> and <see cref="Y"/> are the anchor on the symbol,
/// normalised to 0..1 of the symbol's width and height.
/// </summary>
public sealed record PortDescriptor(
    string Key,
    string Label,
    PortKind Kind,
    PortDirection Direction,
    int Stream,
    double X,
    double Y)
{
    /// <summary>When set, the port only has to be connected if this returns true (used for signal inputs).</summary>
    public Func<NodeModel, bool>? IsRequired { get; init; }

    public bool Required(NodeModel node) => IsRequired?.Invoke(node) ?? Kind == PortKind.Fluid;

    /// <summary>
    /// Key of the <see cref="RepeatGroupDescriptor"/> this port is repeated with: a node then has one port per group
    /// item, keyed like grouped parameters ("In", "In#2"…) and spread along the edge; see
    /// <see cref="ComponentDescriptor.PortsFor"/>.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// For signal inputs: checks what a connection measures (owner node, key of the input on it, and the source) and
    /// returns a problem, or null when the source is acceptable. E.g. a pump envelope position must measure a pump.
    /// </summary>
    public Func<NodeModel, string, SignalSource, string?>? CheckSource { get; init; }
}

/// <summary>What a signal input is connected to: a fluid port or a signal output of <see cref="Node"/>.</summary>
public sealed record SignalSource(NodeModel Node, ComponentDescriptor Descriptor, PortDescriptor Port)
{
    public bool IsSignalOutput => Port.Kind == PortKind.SignalOutput;
}

public enum ParameterKind
{
    Number,
    Choice,

    /// <summary>A fixed-length list of numbers, e.g. polynomial coefficients.</summary>
    Coefficients
}

public sealed record ChoiceOption(string Value, string Label);

/// <summary>An editable parameter of a component type.</summary>
public sealed record ParameterDescriptor(string Key, string Label, ParameterKind Kind)
{
    public string Unit { get; init; } = "";

    public double? DefaultNumber { get; init; }

    public string? DefaultChoice { get; init; }

    public double[]? DefaultCoefficients { get; init; }

    /// <summary>Number of values of a <see cref="ParameterKind.Coefficients"/> parameter.</summary>
    public int Length { get; init; }

    /// <summary>Names of the individual coefficients, shown next to the inputs.</summary>
    public IReadOnlyList<string> CoefficientLabels { get; init; } = [];

    /// <summary>Optional numbers may be left empty, which leaves the value free for the solver.</summary>
    public bool Optional { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>True when <see cref="Min"/> itself is not allowed.</summary>
    public bool MinExclusive { get; init; }

    public string? Help { get; init; }

    public IReadOnlyList<ChoiceOption> Options { get; init; } = [];

    /// <summary>
    /// For choices whose sensible options depend on the document, e.g. the quantities a signal input's source can
    /// provide: the options to show for a node, given the storage key (which tells the item of a repeated group).
    /// <see cref="Options"/> stays the full list of valid values.
    /// </summary>
    public Func<NodeModel, string, SystemDocument, ComponentCatalog, IReadOnlyList<ChoiceOption>>? OptionsFor { get; init; }

    /// <summary>The options to show for <paramref name="node"/>; see <see cref="OptionsFor"/>.</summary>
    public IReadOnlyList<ChoiceOption> OptionsShown(NodeModel node, SystemDocument document, ComponentCatalog catalog) =>
        OptionsShown(node, Key, document, catalog);

    /// <summary>The options to show for the value stored under <paramref name="key"/> (e.g. "key#2").</summary>
    public IReadOnlyList<ChoiceOption> OptionsShown(NodeModel node, string key, SystemDocument document,
        ComponentCatalog catalog) =>
        OptionsFor?.Invoke(node, key, document, catalog) ?? Options;

    /// <summary>Parameters are hidden and ignored when this returns false, e.g. when another mode is chosen.</summary>
    public Func<NodeModel, bool>? IsActive { get; init; }

    /// <summary>
    /// Key of the <see cref="RepeatGroupDescriptor"/> this parameter belongs to, if any. A node then holds one value
    /// per group item, stored under <see cref="RepeatGroupDescriptor.ItemKey"/>.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>Drawn indented under the parameter it depends on, e.g. the settings of a chosen signal control.</summary>
    public bool Nested { get; init; }

    /// <summary>
    /// For grouped parameters: hides the parameter for single group items, given the node and the zero-based item
    /// index, e.g. an operating frequency only for the variable-speed compressors. Applies on top of <see cref="IsActive"/>.
    /// </summary>
    public Func<NodeModel, int, bool>? IsActiveForItem { get; init; }

    public bool Active(NodeModel node) => IsActive?.Invoke(node) ?? true;

    /// <summary>Whether the parameter is active for the group item at zero-based <paramref name="index"/>.</summary>
    public bool Active(NodeModel node, int index) => Active(node) && (IsActiveForItem?.Invoke(node, index) ?? true);

    /// <summary>Whether the value stored under <paramref name="key"/> (e.g. "key#2") is active.</summary>
    public bool ActiveForKey(NodeModel node, string key) => Active(node, RepeatGroupDescriptor.Parse(key).Index);
}

/// <summary>
/// A set of parameters a node can hold several copies of, e.g. the polynomials of each compressor in parallel.
/// Item 1 uses the plain parameter keys; item n &gt; 1 uses "key#n".
/// </summary>
public sealed record RepeatGroupDescriptor(string Key, string ItemLabel)
{
    public const char Separator = '#';

    public int MinCount { get; init; } = 1;

    public int MaxCount { get; init; } = 20;

    public string? Help { get; init; }

    /// <summary>
    /// When this returns a count for a node, the group has exactly that many items and cannot be added to or removed
    /// from, e.g. a signal operator that applies a constant to a single input.
    /// </summary>
    public Func<NodeModel, int?>? FixedCount { get; init; }

    /// <summary>True when <see cref="FixedCount"/> currently decides the number of items of <paramref name="node"/>.</summary>
    public bool IsFixed(NodeModel node) => FixedCount?.Invoke(node) is not null;

    /// <summary>The storage key of a parameter for the item at zero-based <paramref name="index"/>.</summary>
    public static string ItemKey(string parameterKey, int index) =>
        index == 0 ? parameterKey : $"{parameterKey}{Separator}{index + 1}";

    /// <summary>Splits a storage key into the parameter key and the zero-based item index.</summary>
    public static (string ParameterKey, int Index) Parse(string key)
    {
        int separator = key.LastIndexOf(Separator);
        return separator > 0 && int.TryParse(key[(separator + 1)..], out int number) && number > 1
            ? (key[..separator], number - 1)
            : (key, 0);
    }

    /// <summary>Number of items a node has, within <see cref="MinCount"/> and <see cref="MaxCount"/>.</summary>
    public int Count(NodeModel node) =>
        FixedCount?.Invoke(node) ?? Math.Clamp(node.GroupCounts.GetValueOrDefault(Key, MinCount), MinCount, MaxCount);
}

/// <summary>One stored value of a parameter: the parameter itself for ungrouped ones, otherwise one group item.</summary>
public sealed record ParameterInstance(ParameterDescriptor Parameter, string Key, RepeatGroupDescriptor? Group, int Index)
{
    /// <summary>The label with the group item in front, e.g. "Compressor 2: Mass flow polynomial".</summary>
    public string FullLabel => Group is null ? Parameter.Label : $"{Group.ItemLabel} {Index + 1}: {Parameter.Label}";
}

/// <summary>A calculated value of a component other than its port states, e.g. a valve's outlet pressure.</summary>
public sealed record OutputValue(string Label, double? Value, string Unit);

/// <summary>How a component type takes part in the system's energy balance (see <c>Solving.EnergyBalance</c>).</summary>
public enum EnergyKind
{
    /// <summary>No heat or work crosses the system boundary (valves, splits, ejectors, boundaries…).</summary>
    None,

    /// <summary>Work is added to the fluid between "Inlet" and "Outlet" (compressors).</summary>
    Power,

    /// <summary>Heat from outside the system is added to or removed from the fluid between "Inlet" and "Outlet".</summary>
    Heat,

    /// <summary>
    /// Heat moves between stream 1 ("Inlet"/"Outlet") and stream 2 ("Inlet2"/"Outlet2"). It only crosses the system
    /// boundary when the two streams belong to different fluid circuits.
    /// </summary>
    Exchanger
}

public static class Categories
{
    public const string Boundaries = "Boundaries";
    public const string Compressors = "Compressors";
    public const string Valves = "Valves";
    public const string HeatExchangers = "Heat exchangers";
    public const string Pumps = "Pumps";
    public const string Flow = "Flow";
    public const string Signals = "Signals";
}

/// <summary>Everything the editor needs to know about one backend component type.</summary>
public sealed class ComponentDescriptor
{
    public required string TypeId { get; init; }

    public required string DisplayName { get; init; }

    public required string Category { get; init; }

    public string Description { get; init; } = "";

    /// <summary>Default name prefix for new instances, e.g. "Comp" gives Comp1, Comp2…</summary>
    public required string NamePrefix { get; init; }

    /// <summary>Labels of the fluid streams. Most components have one stream.</summary>
    public IReadOnlyList<string> Streams { get; init; } = ["Fluid"];

    /// <summary>Fluid a new node gets per stream; null uses the document's default fluid.</summary>
    public IReadOnlyList<FluidSelection?> StreamDefaults { get; init; } = [];

    public required IReadOnlyList<PortDescriptor> Ports { get; init; }

    public IReadOnlyList<ParameterDescriptor> Parameters { get; init; } = [];

    /// <summary>Groups of parameters a node can repeat; see <see cref="ParameterDescriptor.Group"/>.</summary>
    public IReadOnlyList<RepeatGroupDescriptor> RepeatGroups { get; init; } = [];

    /// <summary>Creates the backend component; throws ArgumentException for invalid parameters.</summary>
    public required Func<BuildContext, CycleComponent> Create { get; init; }

    public required Func<CycleComponent, string, Port> GetPort { get; init; }

    public Func<CycleComponent, string, SignalPort>? GetSignalPort { get; init; }

    /// <summary>The backend output behind a <see cref="PortKind.SignalOutput"/> port.</summary>
    public Func<CycleComponent, string, SignalOutput>? GetSignalOutput { get; init; }

    public Func<CycleComponent, IEnumerable<OutputValue>>? ReadOutputs { get; init; }

    /// <summary>Temperatures of both streams along a heat exchanger, read after a solve; null for other components.
    /// Stream labels come from <see cref="Streams"/>.</summary>
    public Func<CycleComponent, TemperatureProfile>? ReadTemperatureProfile { get; init; }

    public EnergyKind EnergyKind { get; init; } = EnergyKind.None;

    /// <summary>
    /// Finds a port by key. Keys of repeated ports ("In#2") resolve to that item even when a node has fewer items;
    /// use <see cref="FindPort(NodeModel, string)"/> to check a node actually has the port.
    /// </summary>
    public PortDescriptor? FindPort(string key)
    {
        if (Ports.FirstOrDefault(port => port.Key == key && port.Group is null) is { } port)
            return port;
        (string baseKey, int index) = RepeatGroupDescriptor.Parse(key);
        return Ports.FirstOrDefault(p => p.Key == baseKey && p.Group is not null) is { } template
            ? GroupPort(template, index, index + 1)
            : null;
    }

    /// <summary>The port <paramref name="key"/> if <paramref name="node"/> has it.</summary>
    public PortDescriptor? FindPort(NodeModel node, string key) => PortsFor(node).FirstOrDefault(p => p.Key == key);

    /// <summary>The ports of a node, with repeated ports expanded per group item.</summary>
    public IEnumerable<PortDescriptor> PortsFor(NodeModel node)
    {
        foreach (PortDescriptor port in Ports)
        {
            if (port.Group is null || FindGroup(port.Group) is not { } group)
            {
                yield return port;
                continue;
            }

            int count = group.Count(node);
            for (int index = 0; index < count; index++)
                yield return GroupPort(port, index, count);
        }
    }

    /// <summary>
    /// Item <paramref name="index"/> of a repeated port, spread evenly along the edge of the template's anchor
    /// (left/right edges spread vertically, top/bottom horizontally).
    /// </summary>
    private static PortDescriptor GroupPort(PortDescriptor template, int index, int count)
    {
        double along = (index + 1.0) / (count + 1);
        bool vertical = template.X is 0 or 1;
        return template with
        {
            Key = RepeatGroupDescriptor.ItemKey(template.Key, index),
            Label = $"{template.Label} {index + 1}",
            X = vertical ? template.X : along,
            Y = vertical ? along : template.Y
        };
    }

    /// <summary>Finds a parameter by key. Keys of repeated group items ("key#2") resolve to their parameter.</summary>
    public ParameterDescriptor? FindParameter(string key) =>
        Parameters.FirstOrDefault(p => p.Key == key) ??
        (RepeatGroupDescriptor.Parse(key) is var (baseKey, index) && index > 0
            ? Parameters.FirstOrDefault(p => p.Key == baseKey && p.Group is not null)
            : null);

    public RepeatGroupDescriptor? FindGroup(string key) => RepeatGroups.FirstOrDefault(g => g.Key == key);

    /// <summary>Every stored value of the active parameters of a node, with repeated groups expanded per item.</summary>
    public IEnumerable<ParameterInstance> ParameterInstances(NodeModel node)
    {
        foreach (ParameterDescriptor parameter in Parameters.Where(p => p.Active(node)))
        {
            if (parameter.Group is null || FindGroup(parameter.Group) is not { } group)
            {
                yield return new ParameterInstance(parameter, parameter.Key, null, 0);
                continue;
            }

            for (int index = 0; index < group.Count(node); index++)
            {
                if (parameter.Active(node, index))
                    yield return new ParameterInstance(parameter, RepeatGroupDescriptor.ItemKey(parameter.Key, index), group, index);
            }
        }
    }

    /// <summary>Creates a node of this type with default parameter values.</summary>
    public NodeModel CreateNode(string name, double x, double y, FluidSelection fluid)
    {
        NodeModel node = new() { TypeId = TypeId, Name = name, X = x, Y = y };
        foreach (ParameterDescriptor parameter in Parameters)
        {
            switch (parameter.Kind)
            {
                case ParameterKind.Number:
                    node.Numbers[parameter.Key] = parameter.DefaultNumber;
                    break;
                case ParameterKind.Coefficients:
                    node.Arrays[parameter.Key] = parameter.DefaultCoefficients?.ToArray();
                    break;
                default:
                    node.Choices[parameter.Key] = parameter.DefaultChoice ?? parameter.Options.FirstOrDefault()?.Value ?? "";
                    break;
            }
        }

        foreach (RepeatGroupDescriptor group in RepeatGroups)
            node.GroupCounts[group.Key] = group.MinCount;

        node.Fluids = Streams
            .Select((_, stream) => (stream < StreamDefaults.Count ? StreamDefaults[stream] : null) ?? fluid)
            .ToList();
        return node;
    }
}

/// <summary>Typed access to a node's parameters while creating the backend component.</summary>
public sealed class BuildContext(NodeModel node, ComponentDescriptor descriptor, IReadOnlyList<FluidSelection> fluids)
{
    public NodeModel Node { get; } = node;

    public string Name => Node.Name;

    public string Fluid(int stream = 0) => fluids[stream].Fluid;

    public string Backend(int stream = 0) => fluids[stream].Backend;

    /// <summary>The value of an active numeric parameter, or null when it is empty or inactive.</summary>
    public double? OptionalNumber(string key)
    {
        ParameterDescriptor parameter = descriptor.FindParameter(key)
                                        ?? throw new InvalidOperationException($"Unknown parameter '{key}'.");
        if (!parameter.ActiveForKey(Node, key))
            return null;
        return Node.Numbers.TryGetValue(key, out double? value) ? value : parameter.DefaultNumber;
    }

    /// <summary>Number of items of a repeated parameter group; see <see cref="RepeatGroupDescriptor"/>.</summary>
    public int Count(string groupKey) =>
        (descriptor.FindGroup(groupKey) ?? throw new InvalidOperationException($"Unknown parameter group '{groupKey}'."))
        .Count(Node);

    /// <summary>The value of a required numeric parameter.</summary>
    public double Number(string key) =>
        OptionalNumber(key) ?? throw new ParameterException(key, "A value is required.");

    /// <summary>The coefficients of an active parameter, or null when they are not given or inactive.</summary>
    public double[]? OptionalCoefficients(string key)
    {
        ParameterDescriptor parameter = descriptor.FindParameter(key)
                                        ?? throw new InvalidOperationException($"Unknown parameter '{key}'.");
        if (!parameter.ActiveForKey(Node, key))
            return null;
        return Node.Arrays.TryGetValue(key, out double[]? value) ? value : parameter.DefaultCoefficients;
    }

    public double[] Coefficients(string key) =>
        OptionalCoefficients(key) ?? throw new ParameterException(key, "Coefficients are required.");

    public string Choice(string key)
    {
        ParameterDescriptor parameter = descriptor.FindParameter(key)
                                        ?? throw new InvalidOperationException($"Unknown parameter '{key}'.");
        return Node.Choices.TryGetValue(key, out string? value) && parameter.Options.Any(o => o.Value == value)
            ? value
            : parameter.DefaultChoice ?? parameter.Options[0].Value;
    }
}

/// <summary>Thrown while building when a specific parameter is invalid.</summary>
public sealed class ParameterException(string parameterKey, string message) : Exception(message)
{
    public string ParameterKey { get; } = parameterKey;
}
