using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Shared.Components.Diagram;

/// <summary>Node size and port positions in canvas units. Must match the transforms rendered by DiagramCanvas.</summary>
public static class Geometry
{
    public const double NodeWidth = 96;
    public const double NodeHeight = 64;

    public readonly record struct Point(double X, double Y);

    /// <summary>Port position on the unrotated symbol.</summary>
    public static Point Local(PortDescriptor port) => new(port.X * NodeWidth, port.Y * NodeHeight);

    /// <summary>Outward direction of a port on the unrotated symbol, as "dx,dy" for the script.</summary>
    public static Point LocalDirection(PortDescriptor port) => port switch
    {
        { X: <= 0 } => new Point(-1, 0),
        { X: >= 1 } => new Point(1, 0),
        { Y: <= 0 } => new Point(0, -1),
        { Y: >= 1 } => new Point(0, 1),
        _ => new Point(0, 0)
    };

    /// <summary>SVG transform for a node's symbol: flip first, then rotate about the centre.</summary>
    public static string SymbolTransform(NodeModel node) =>
        $"rotate({node.Rotation} {NodeWidth / 2} {NodeHeight / 2})" +
        (node.FlipHorizontal ? $" translate({NodeWidth} 0) scale(-1 1)" : "");

    /// <summary>World position and outward direction of a port, applying flip and rotation.</summary>
    public static (Point Position, Point Direction) World(NodeModel node, PortDescriptor port)
    {
        Point local = Local(port);
        Point direction = LocalDirection(port);
        if (node.FlipHorizontal)
        {
            local = local with { X = NodeWidth - local.X };
            direction = direction with { X = -direction.X };
        }

        double angle = node.Rotation * Math.PI / 180;
        double cos = Math.Round(Math.Cos(angle)), sin = Math.Round(Math.Sin(angle));
        double cx = NodeWidth / 2, cy = NodeHeight / 2;
        double dx = local.X - cx, dy = local.Y - cy;
        Point position = new(node.X + cx + dx * cos - dy * sin, node.Y + cy + dx * sin + dy * cos);
        Point rotated = new(direction.X * cos - direction.Y * sin, direction.X * sin + direction.Y * cos);
        return (position, rotated);
    }

    /// <summary>Bottom of the node's footprint, below which the name label goes.</summary>
    public static double LabelOffset(NodeModel node) =>
        node.Rotation % 180 == 0 ? NodeHeight + 16 : NodeHeight / 2 + NodeWidth / 2 + 16;

    public static string F(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
