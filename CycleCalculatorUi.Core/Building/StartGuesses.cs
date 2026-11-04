using CycleCalculator.CycleModel.Model.BaseComponents;
using CycleCalculator.CycleModel.Model.Connectors;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Solving;

namespace CycleCalculatorUi.Core.Building;

/// <summary>Where a start guess applies: one port, the ports of one stream, or the whole component.</summary>
public sealed record GuessScope(string Label, string? PortKey, int? Stream)
{
    public bool Matches(GuessModel guess) => guess.PortKey == PortKey && (PortKey is not null || guess.Stream == Stream);
}

/// <summary>Display information for a guess quantity.</summary>
public sealed record GuessQuantityInfo(GuessQuantity Quantity, string Label, string Unit);

/// <summary>Helpers for editing and checking the solver start guesses of a node.</summary>
public static class StartGuesses
{
    public static IReadOnlyList<GuessQuantityInfo> Quantities { get; } =
    [
        new(GuessQuantity.Pressure, "Pressure", "bar"),
        new(GuessQuantity.SaturationTemperature, "Saturation temperature", "°C"),
        new(GuessQuantity.MassFlow, "Mass flow", "kg/s"),
        new(GuessQuantity.Enthalpy, "Enthalpy", "kJ/kg"),
        new(GuessQuantity.Temperature, "Temperature", "°C"),
        new(GuessQuantity.Quality, "Vapour quality", "-")
    ];

    public static GuessQuantityInfo Info(GuessQuantity quantity) => Quantities.First(q => q.Quantity == quantity);

    /// <summary>Issue key of the guess at <paramref name="index"/>, so its message can be shown next to it.</summary>
    public static string IssueKey(int index) => $"guess:{index}";

    /// <summary>
    /// Scopes a guess can have on this component type: the whole component (or each stream, when there are
    /// several), then each fluid port.
    /// </summary>
    public static IReadOnlyList<GuessScope> Scopes(ComponentDescriptor descriptor)
    {
        List<PortDescriptor> ports = FluidPorts(descriptor).ToList();
        List<int> streams = ports.Select(p => p.Stream).Distinct().Order().ToList();
        List<GuessScope> scopes = [];
        if (streams.Count > 1)
        {
            foreach (int stream in streams)
            {
                string name = stream < descriptor.Streams.Count ? descriptor.Streams[stream] : $"Stream {stream + 1}";
                scopes.Add(new GuessScope($"{name}, all ports", null, stream));
            }
        }
        else
        {
            scopes.Add(new GuessScope("All ports", null, null));
        }

        scopes.AddRange(ports.Select(p => new GuessScope($"Port: {p.Label}", p.Key, null)));
        return scopes;
    }

    public static GuessScope ScopeOf(ComponentDescriptor descriptor, GuessModel guess) =>
        Scopes(descriptor).FirstOrDefault(scope => scope.Matches(guess))
        ?? new GuessScope(guess.PortKey ?? (guess.Stream is { } s ? $"Stream {s + 1}" : "All ports"), guess.PortKey, guess.Stream);

    /// <summary>Checks one guess without building the model; null when it looks valid.</summary>
    public static string? Check(GuessModel guess, ComponentDescriptor descriptor)
    {
        List<PortDescriptor> ports = FluidPorts(descriptor).ToList();
        List<PortDescriptor> scoped;
        if (guess.PortKey is not null)
        {
            if (ports.FirstOrDefault(p => p.Key == guess.PortKey) is not { } port)
                return $"The start guess refers to a port '{guess.PortKey}' this component does not have.";
            scoped = [port];
        }
        else if (guess.Stream is { } stream)
        {
            scoped = ports.Where(p => p.Stream == stream).ToList();
            if (scoped.Count == 0)
                return $"The start guess refers to stream {stream + 1}, which has no ports.";
        }
        else
        {
            scoped = ports;
        }

        if (!double.IsFinite(guess.Value))
            return "Enter a number.";

        return guess.Quantity switch
        {
            GuessQuantity.Pressure when guess.Value <= 0 => "A pressure guess must be above zero.",
            GuessQuantity.Temperature or GuessQuantity.SaturationTemperature when guess.Value <= -273.15 =>
                "A temperature guess must be above absolute zero.",
            GuessQuantity.Quality when guess.Value is < 0 or > 1 => "A quality guess must be between 0 and 1.",
            GuessQuantity.MassFlow when guess.PortKey is null && !IsThroughflow(scoped) =>
                "A mass flow guess here is ambiguous: these ports are not exactly one inlet and one outlet. " +
                "Put it on a single port instead.",
            _ => null
        };
    }

    /// <summary>
    /// A new guess for the scope, valued from the latest result when there is one, otherwise a typical value.
    /// </summary>
    public static GuessModel Create(GuessScope scope, GuessQuantity quantity, NodeModel node, ComponentDescriptor descriptor,
        SolveResult? result)
    {
        double value = FromResult(scope, quantity, node, descriptor, result) ?? quantity switch
        {
            GuessQuantity.Pressure => 30,
            GuessQuantity.SaturationTemperature => 0,
            GuessQuantity.MassFlow => 0.1,
            GuessQuantity.Enthalpy => 400,
            GuessQuantity.Temperature => 20,
            _ => 0.5
        };
        return new GuessModel(quantity, value, scope.PortKey, scope.PortKey is null ? scope.Stream : null);
    }

    /// <summary>
    /// The value a guess would have in the latest result: the port's value, or for a stream or the whole component
    /// the inlet's value (mass flow as throughput). Null when the result has no matching value.
    /// </summary>
    public static double? FromResult(GuessScope scope, GuessQuantity quantity, NodeModel node, ComponentDescriptor descriptor,
        SolveResult? result)
    {
        if (result is not { HasResults: true } || !result.Ports.TryGetValue(node.Id, out var ports))
            return null;

        PortDescriptor? port = scope.PortKey is not null
            ? descriptor.FindPort(scope.PortKey)
            : FluidPorts(descriptor).Where(p => scope.Stream is null || p.Stream == scope.Stream)
                .OrderBy(p => p.Direction == PortDirection.Inlet ? 0 : 1).FirstOrDefault();
        if (port is null || !ports.TryGetValue(port.Key, out PortResult? state))
            return null;

        double? value = quantity switch
        {
            GuessQuantity.Pressure => state.Pressure,
            GuessQuantity.MassFlow => scope.PortKey is null ? Math.Abs(state.MassFlow) : state.MassFlow,
            GuessQuantity.Enthalpy => state.Enthalpy,
            GuessQuantity.Temperature => state.Temperature,
            _ => null
        };
        return value is { } v && double.IsFinite(v) ? v : null;
    }

    /// <summary>Guesses of pressure, enthalpy and mass flow on every port, taken from the latest result.</summary>
    public static List<GuessModel> AllFromResult(NodeModel node, ComponentDescriptor descriptor, SolveResult result)
    {
        List<GuessModel> guesses = [];
        if (!result.Ports.TryGetValue(node.Id, out var ports))
            return guesses;
        foreach (PortDescriptor port in FluidPorts(descriptor))
        {
            if (!ports.TryGetValue(port.Key, out PortResult? state) ||
                !double.IsFinite(state.Pressure) || !double.IsFinite(state.Enthalpy) || !double.IsFinite(state.MassFlow))
                continue;
            guesses.Add(new GuessModel(GuessQuantity.Pressure, state.Pressure, port.Key));
            guesses.Add(new GuessModel(GuessQuantity.Enthalpy, state.Enthalpy, port.Key));
            guesses.Add(new GuessModel(GuessQuantity.MassFlow, state.MassFlow, port.Key));
        }

        return guesses;
    }

    private static IEnumerable<PortDescriptor> FluidPorts(ComponentDescriptor descriptor) =>
        descriptor.Ports.Where(p => p.Kind == PortKind.Fluid);

    private static bool IsThroughflow(IReadOnlyList<PortDescriptor> ports) =>
        ports.Count == 2 &&
        ports.Count(p => p.Direction == PortDirection.Inlet) == 1 &&
        ports.Count(p => p.Direction == PortDirection.Outlet) == 1;
}
