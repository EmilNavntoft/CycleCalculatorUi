using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Building;

/// <summary>One stream of one node, e.g. the hot side of a heat exchanger.</summary>
public sealed record StreamRef(string NodeId, int Stream);

/// <summary>A set of streams joined by fluid connections. All of them must use the same fluid.</summary>
public sealed class FluidCircuit
{
    public required int Index { get; init; }

    public required IReadOnlyList<StreamRef> Members { get; init; }

    public required FluidSelection Fluid { get; init; }

    /// <summary>True when members were given different fluids, e.g. right after connecting two circuits.</summary>
    public required bool HasConflict { get; init; }
}

public static class FluidCircuitResolver
{
    /// <summary>Groups all streams of the document into circuits using union-find over fluid connections.</summary>
    public static IReadOnlyList<FluidCircuit> Resolve(SystemDocument document, ComponentCatalog catalog)
    {
        Dictionary<StreamRef, StreamRef> parent = [];
        foreach (NodeModel node in document.Nodes)
        {
            if (catalog.Find(node.TypeId) is not { } descriptor)
                continue;
            for (int stream = 0; stream < descriptor.Streams.Count; stream++)
            {
                StreamRef reference = new(node.Id, stream);
                parent[reference] = reference;
            }
        }

        StreamRef Root(StreamRef reference)
        {
            while (parent[reference] != reference)
            {
                parent[reference] = parent[parent[reference]];
                reference = parent[reference];
            }

            return reference;
        }

        foreach (ConnectionModel connection in document.Connections.Where(c => c.Kind == ConnectionKind.Fluid))
        {
            if (StreamOf(document, catalog, connection.From) is not { } a ||
                StreamOf(document, catalog, connection.To) is not { } b ||
                !parent.ContainsKey(a) || !parent.ContainsKey(b))
                continue;
            parent[Root(a)] = Root(b);
        }

        // Keep the circuit order stable: by the first node in document order.
        List<FluidCircuit> circuits = [];
        foreach (IGrouping<StreamRef, StreamRef> group in parent.Keys.GroupBy(Root))
        {
            List<FluidSelection> fluids = group
                .Select(member => FluidOf(document, member))
                .OfType<FluidSelection>()
                .Distinct()
                .ToList();
            circuits.Add(new FluidCircuit
            {
                Index = circuits.Count,
                Members = group.ToList(),
                Fluid = fluids.FirstOrDefault() ?? document.DefaultFluid,
                HasConflict = fluids.Count > 1
            });
        }

        return circuits;
    }

    public static FluidCircuit? CircuitOf(IEnumerable<FluidCircuit> circuits, StreamRef stream) =>
        circuits.FirstOrDefault(circuit => circuit.Members.Contains(stream));

    public static StreamRef? StreamOf(SystemDocument document, ComponentCatalog catalog, PortRef port)
    {
        if (document.FindNode(port.NodeId) is not { } node ||
            catalog.Find(node.TypeId)?.FindPort(port.PortKey) is not { } descriptor)
            return null;
        return new StreamRef(node.Id, descriptor.Stream);
    }

    /// <summary>Sets the fluid of every stream in the circuit that contains <paramref name="stream"/>.</summary>
    public static void SetCircuitFluid(SystemDocument document, ComponentCatalog catalog, StreamRef stream,
        FluidSelection fluid)
    {
        FluidCircuit? circuit = CircuitOf(Resolve(document, catalog), stream);
        IEnumerable<StreamRef> members = circuit?.Members ?? [stream];
        foreach (StreamRef member in members)
        {
            NodeModel? node = document.FindNode(member.NodeId);
            if (node is null)
                continue;
            while (node.Fluids.Count <= member.Stream)
                node.Fluids.Add(document.DefaultFluid);
            node.Fluids[member.Stream] = fluid;
        }
    }

    private static FluidSelection? FluidOf(SystemDocument document, StreamRef stream)
    {
        NodeModel? node = document.FindNode(stream.NodeId);
        return node is not null && stream.Stream < node.Fluids.Count ? node.Fluids[stream.Stream] : null;
    }
}
