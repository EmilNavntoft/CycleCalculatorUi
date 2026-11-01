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

    public bool Active(NodeModel node) => IsActive?.Invoke(node) ?? true;
}

/// <summary>A calculated value of a component other than its port states, e.g. a valve's outlet pressure.</summary>
public sealed record OutputValue(string Label, double? Value, string Unit);

public static class Categories
{
    public const string Boundaries = "Boundaries";
    public const string Compressors = "Compressors";
    public const string Valves = "Valves";
    public const string HeatExchangers = "Heat exchangers";
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

    /// <summary>Creates the backend component; throws ArgumentException for invalid parameters.</summary>
    public required Func<BuildContext, CycleComponent> Create { get; init; }

    public required Func<CycleComponent, string, Port> GetPort { get; init; }

    public Func<CycleComponent, string, SignalPort>? GetSignalPort { get; init; }

    public Func<CycleComponent, IEnumerable<OutputValue>>? ReadOutputs { get; init; }

    public PortDescriptor? FindPort(string key) => Ports.FirstOrDefault(port => port.Key == key);

    public ParameterDescriptor? FindParameter(string key) => Parameters.FirstOrDefault(p => p.Key == key);

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
