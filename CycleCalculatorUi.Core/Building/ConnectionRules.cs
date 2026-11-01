using CycleCalculator.CycleModel.Model.Connectors;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Building;

/// <summary>A connection the user may create, already normalised so From/To follow <see cref="ConnectionModel"/>.</summary>
public sealed record ConnectionProposal(ConnectionKind Kind, PortRef From, PortRef To);

public static class ConnectionRules
{
    /// <summary>
    /// Checks whether ports <paramref name="a"/> and <paramref name="b"/> can be connected, in either order.
    /// Returns the proposal, or null with a reason the user can read.
    /// </summary>
    public static ConnectionProposal? TryConnect(SystemDocument document, ComponentCatalog catalog,
        PortRef a, PortRef b, out string? reason)
    {
        reason = null;
        if (a == b)
        {
            reason = "A port cannot be connected to itself.";
            return null;
        }

        if (Describe(document, catalog, a) is not { } portA || Describe(document, catalog, b) is not { } portB)
        {
            reason = "Unknown port.";
            return null;
        }

        if (portA.Kind == PortKind.SignalInput && portB.Kind == PortKind.SignalInput)
        {
            reason = "Two signal inputs cannot be connected; connect a signal input to a fluid port to measure it.";
            return null;
        }

        if (portA.Kind == PortKind.SignalInput || portB.Kind == PortKind.SignalInput)
        {
            (PortRef source, PortRef input) = portA.Kind == PortKind.SignalInput ? (b, a) : (a, b);
            if (document.Connections.Any(c => c.Kind == ConnectionKind.Signal && c.To == input))
            {
                reason = "The signal input is already connected.";
                return null;
            }

            return new ConnectionProposal(ConnectionKind.Signal, source, input);
        }

        if (IsFluidConnected(document, a) || IsFluidConnected(document, b))
        {
            reason = "A fluid port can only have one connection.";
            return null;
        }

        if (portA.Direction != PortDirection.Undefined && portA.Direction == portB.Direction)
        {
            reason = portA.Direction == PortDirection.Inlet
                ? "Two inlets cannot be connected."
                : "Two outlets cannot be connected.";
            return null;
        }

        // Keep the flow direction readable: from an outlet to an inlet when known.
        bool swap = portA.Direction == PortDirection.Inlet || portB.Direction == PortDirection.Outlet;
        return swap
            ? new ConnectionProposal(ConnectionKind.Fluid, b, a)
            : new ConnectionProposal(ConnectionKind.Fluid, a, b);
    }

    public static bool IsFluidConnected(SystemDocument document, PortRef port) =>
        document.Connections.Any(c => c.Kind == ConnectionKind.Fluid && c.Touches(port));

    public static bool IsConnected(SystemDocument document, PortRef port) =>
        document.Connections.Any(c => c.Touches(port));

    public static PortDescriptor? Describe(SystemDocument document, ComponentCatalog catalog, PortRef port) =>
        document.FindNode(port.NodeId) is { } node ? catalog.Find(node.TypeId)?.FindPort(port.PortKey) : null;
}
