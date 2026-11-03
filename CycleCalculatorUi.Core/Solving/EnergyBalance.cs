using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Solving;

/// <summary>How a component counts in the energy totals.</summary>
public enum EnergyRole
{
    /// <summary>Decided from the component type and how it is connected.</summary>
    Auto,
    HeatInput,
    HeatOutput,
    PowerInput,

    /// <summary>Left out of the totals.</summary>
    Ignore
}

/// <summary>One component's contribution to the totals, in kW (always ≥ 0).</summary>
public sealed record EnergyFlow(string NodeId, string Name, EnergyRole Role, double Value, bool Overridden);

/// <summary>System-level heat and power totals of one solved operating point, in kW.</summary>
public sealed class EnergySummary
{
    public required IReadOnlyList<EnergyFlow> Flows { get; init; }

    public double HeatIn => Sum(EnergyRole.HeatInput);

    public double HeatOut => Sum(EnergyRole.HeatOutput);

    public double PowerIn => Sum(EnergyRole.PowerInput);

    /// <summary>Heat output per power input; null without power input.</summary>
    public double? CopHeating => PowerIn > 1e-9 ? HeatOut / PowerIn : null;

    /// <summary>Heat input per power input; null without power input.</summary>
    public double? CopCooling => PowerIn > 1e-9 ? HeatIn / PowerIn : null;

    /// <summary>Heat in + power in − heat out; close to zero for a closed cycle with all exchanges counted.</summary>
    public double Imbalance => HeatIn + PowerIn - HeatOut;

    private double Sum(EnergyRole role) => Flows.Where(f => f.Role == role).Sum(f => f.Value);
}

/// <summary>
/// Works out heat input, heat output and power input from solved port states. Each component type declares an
/// <see cref="EnergyKind"/>; a node's <see cref="NodeModel.EnergyRole"/> overrides the automatic choice.
/// </summary>
public static class EnergyBalance
{
    public static EnergySummary Compute(SystemDocument document, ComponentCatalog catalog, SolveResult result)
    {
        IReadOnlyList<FluidCircuit> circuits = FluidCircuitResolver.Resolve(document, catalog);
        List<EnergyFlow> flows = [];
        foreach (NodeModel node in document.Nodes)
        {
            if (catalog.Find(node.TypeId) is not { } descriptor ||
                !result.Ports.TryGetValue(node.Id, out IReadOnlyDictionary<string, PortResult>? ports))
            {
                continue;
            }

            if (Contribution(document, catalog, circuits, node, descriptor, ports) is { } flow)
                flows.Add(flow);
        }

        return new EnergySummary { Flows = flows };
    }

    /// <summary>
    /// Explains what <see cref="EnergyRole.Auto"/> does for this node, for the inspector. Null when the component
    /// type never exchanges heat or work with the surroundings.
    /// </summary>
    public static string? DescribeAuto(SystemDocument document, ComponentCatalog catalog, NodeModel node)
    {
        if (catalog.Find(node.TypeId) is not { } descriptor)
            return null;
        return descriptor.EnergyKind switch
        {
            EnergyKind.Power => "Counts as power input.",
            EnergyKind.Heat => "Counts as heat input or output, depending on whether it heats or cools the fluid.",
            EnergyKind.Exchanger => CycleStream(document, catalog, FluidCircuitResolver.Resolve(document, catalog), node) is { } stream
                ? $"Counts as heat input or output of the cycle on {descriptor.Streams[stream].ToLowerInvariant()}."
                : "Not counted: both streams are in the same circuit, or it is unclear which stream belongs to the " +
                  "cycle. Choose a role to count it.",
            _ => null
        };
    }

    private static EnergyFlow? Contribution(SystemDocument document, ComponentCatalog catalog,
        IReadOnlyList<FluidCircuit> circuits, NodeModel node, ComponentDescriptor descriptor,
        IReadOnlyDictionary<string, PortResult> ports)
    {
        if (node.EnergyRole == EnergyRole.Ignore)
            return null;

        if (node.EnergyRole != EnergyRole.Auto)
        {
            // Overrides count stream 1 between its inlet and outlet.
            return HeatAdded(ports, "Inlet", "Outlet") is { } value
                ? new EnergyFlow(node.Id, node.Name, node.EnergyRole, Math.Abs(value), true)
                : null;
        }

        switch (descriptor.EnergyKind)
        {
            case EnergyKind.Power:
                return HeatAdded(ports, "Inlet", "Outlet") is { } power
                    ? new EnergyFlow(node.Id, node.Name, EnergyRole.PowerInput, Math.Abs(power), false)
                    : null;
            case EnergyKind.Heat:
                return HeatAdded(ports, "Inlet", "Outlet") is { } heat ? Heat(node, heat) : null;
            case EnergyKind.Exchanger:
                if (CycleStream(document, catalog, circuits, node) is not { } stream)
                    return null;
                double? q = stream == 0 ? HeatAdded(ports, "Inlet", "Outlet") : HeatAdded(ports, "Inlet2", "Outlet2");
                return q is { } exchanged ? Heat(node, exchanged) : null;
            default:
                return null;
        }
    }

    private static EnergyFlow Heat(NodeModel node, double added) =>
        new(node.Id, node.Name, added >= 0 ? EnergyRole.HeatInput : EnergyRole.HeatOutput, Math.Abs(added), false);

    /// <summary>
    /// Heat added to the fluid between two ports in kW. The backend's mass flow at an inlet is positive into the
    /// component, so the sign stays correct when the solver finds the flow reversed.
    /// </summary>
    private static double? HeatAdded(IReadOnlyDictionary<string, PortResult> ports, string inlet, string outlet) =>
        ports.TryGetValue(inlet, out PortResult? a) && ports.TryGetValue(outlet, out PortResult? b)
            ? a.MassFlow * (b.Enthalpy - a.Enthalpy)
            : null;

    /// <summary>
    /// The stream of a two-stream heat exchanger that belongs to the cycle: the one whose circuit contains a
    /// compressor while the other's does not. Null when that is ambiguous or both streams share a circuit.
    /// </summary>
    private static int? CycleStream(SystemDocument document, ComponentCatalog catalog,
        IReadOnlyList<FluidCircuit> circuits, NodeModel node)
    {
        FluidCircuit? first = FluidCircuitResolver.CircuitOf(circuits, new StreamRef(node.Id, 0));
        FluidCircuit? second = FluidCircuitResolver.CircuitOf(circuits, new StreamRef(node.Id, 1));
        if (first is null || second is null || first.Index == second.Index)
            return null;

        bool firstHasCompressor = HasCompressor(first);
        bool secondHasCompressor = HasCompressor(second);
        if (firstHasCompressor == secondHasCompressor)
            return null;
        return firstHasCompressor ? 0 : 1;

        bool HasCompressor(FluidCircuit circuit) => circuit.Members.Any(member =>
            document.FindNode(member.NodeId) is { } n && catalog.Find(n.TypeId)?.EnergyKind == EnergyKind.Power);
    }
}
