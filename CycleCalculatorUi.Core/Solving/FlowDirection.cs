using CycleCalculator.CycleModel.Model.Connectors;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Solving;

/// <summary>
/// Mass flow as the user expects to read it: positive in the design flow direction (into an inlet, out of an outlet),
/// negative when the solver found the flow running backwards. The backend's own sign is "positive into the component".
/// </summary>
public static class FlowDirection
{
    /// <summary>Flows smaller than this (kg/s) count as zero, not as reversed.</summary>
    public const double Tolerance = 1e-9;

    /// <summary>
    /// +1 when positive backend mass flow at this port is the design direction (an inlet), -1 for an outlet,
    /// 0 when unknown. A port without a direction (a boundary condition) takes the opposite of the port it is connected to.
    /// </summary>
    public static int DesignSign(SystemDocument document, ComponentCatalog catalog, PortRef port)
    {
        switch (ConnectionRules.Describe(document, catalog, port)?.Direction)
        {
            case PortDirection.Inlet:
                return 1;
            case PortDirection.Outlet:
                return -1;
            case PortDirection.Undefined:
                ConnectionModel? connection = document.Connections.FirstOrDefault(c =>
                    c.Kind == ConnectionKind.Fluid && c.Touches(port));
                if (connection is null)
                    return 0;
                PortRef partner = connection.From == port ? connection.To : connection.From;
                return ConnectionRules.Describe(document, catalog, partner)?.Direction switch
                {
                    PortDirection.Inlet => -1,
                    PortDirection.Outlet => 1,
                    _ => 0
                };
            default:
                return 0;
        }
    }

    /// <summary>Mass flow in the design direction, or the backend value when the direction is unknown.</summary>
    public static FlowValue Of(SystemDocument document, ComponentCatalog catalog, PortRef port, PortResult value)
    {
        int sign = DesignSign(document, catalog, port);
        return sign == 0
            ? new FlowValue(value.MassFlow, false)
            : new FlowValue(sign * value.MassFlow, sign * value.MassFlow < -Tolerance);
    }
}

/// <param name="MassFlow">kg/s, positive in the design direction.</param>
/// <param name="Reversed">True when the flow runs against the design direction.</param>
public readonly record struct FlowValue(double MassFlow, bool Reversed);
