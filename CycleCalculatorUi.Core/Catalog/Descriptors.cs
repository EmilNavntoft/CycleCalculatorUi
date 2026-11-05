using CycleCalculator.CycleModel.Model;
using CycleCalculator.CycleModel.Model.BaseComponents;
using CycleCalculator.CycleModel.Model.Connectors;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Catalog;

public enum PortKind
{
    Fluid,
    SignalInput
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

    /// <summary>Parameters are hidden and ignored when this returns false, e.g. when another mode is chosen.</summary>
    public Func<NodeModel, bool>? IsActive { get; init; }

    /// <summary>
    /// Key of the <see cref="RepeatGroupDescriptor"/> this parameter belongs to, if any. A node then holds one value
    /// per group item, stored under <see cref="RepeatGroupDescriptor.ItemKey"/>.
    /// </summary>
    public string? Group { get; init; }

    public bool Active(NodeModel node) => IsActive?.Invoke(node) ?? true;
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
        Math.Clamp(node.GroupCounts.GetValueOrDefault(Key, MinCount), MinCount, MaxCount);
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

    public Func<CycleComponent, IEnumerable<OutputValue>>? ReadOutputs { get; init; }

    /// <summary>Temperatures of both streams along a heat exchanger, read after a solve; null for other components.
    /// Stream labels come from <see cref="Streams"/>.</summary>
    public Func<CycleComponent, TemperatureProfile>? ReadTemperatureProfile { get; init; }

    public EnergyKind EnergyKind { get; init; } = EnergyKind.None;

    public PortDescriptor? FindPort(string key) => Ports.FirstOrDefault(port => port.Key == key);

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
                yield return new ParameterInstance(parameter, RepeatGroupDescriptor.ItemKey(parameter.Key, index), group, index);
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
        if (!parameter.Active(Node))
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
        if (!parameter.Active(Node))
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
