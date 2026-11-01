using CycleCalculator.CycleModel.Fluids;

namespace CycleCalculatorUi.Core.Document;

/// <summary>
/// The editable description of a cycle as drawn in the editor. It holds no backend objects;
/// <see cref="Building.ModelBuilder"/> turns it into fresh solver components on every solve.
/// </summary>
public sealed class SystemDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Name { get; set; } = "Untitled system";

    /// <summary>Fluid given to streams that are not connected to anything with a fluid yet.</summary>
    public FluidSelection DefaultFluid { get; set; } = new(CoolPropFluids.CarbonDioxide, CoolPropBackends.HEOS);

    public List<NodeModel> Nodes { get; set; } = [];

    public List<ConnectionModel> Connections { get; set; } = [];

    public NodeModel? FindNode(string nodeId) => Nodes.FirstOrDefault(node => node.Id == nodeId);
}

/// <summary>One placed component on the canvas.</summary>
public sealed class NodeModel
{
    public string Id { get; set; } = NewId();

    /// <summary>Key of the component type in the <see cref="Catalog.ComponentCatalog"/>.</summary>
    public string TypeId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Top-left position on the canvas in canvas units.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Rotation in degrees, a multiple of 90.</summary>
    public int Rotation { get; set; }

    public bool FlipHorizontal { get; set; }

    /// <summary>Numeric parameters; a null value means "not set / free for the solver".</summary>
    public Dictionary<string, double?> Numbers { get; set; } = [];

    /// <summary>Coefficient lists such as EN 12900 polynomials; null means "not given".</summary>
    public Dictionary<string, double[]?> Arrays { get; set; } = [];

    /// <summary>Choice parameters such as an operating mode.</summary>
    public Dictionary<string, string> Choices { get; set; } = [];

    /// <summary>Fluid of each stream of the component (one entry for most components, two for heat exchangers).</summary>
    public List<FluidSelection> Fluids { get; set; } = [];

    /// <summary>Optional solver start guesses.</summary>
    public List<GuessModel> Guesses { get; set; } = [];

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public enum ConnectionKind
{
    /// <summary>Physical fluid connection between two fluid ports.</summary>
    Fluid,

    /// <summary>Read-only measurement from a fluid port (source) to a signal input (target).</summary>
    Signal
}

public sealed record PortRef(string NodeId, string PortKey);

/// <summary>
/// A connection between two ports. For signal connections <see cref="From"/> is the measured fluid port
/// and <see cref="To"/> is the signal input.
/// </summary>
public sealed class ConnectionModel
{
    public string Id { get; set; } = NodeModel.NewId();

    public ConnectionKind Kind { get; set; }

    public PortRef From { get; set; } = new("", "");

    public PortRef To { get; set; } = new("", "");

    public bool Touches(string nodeId) => From.NodeId == nodeId || To.NodeId == nodeId;

    public bool Touches(PortRef port) => From == port || To == port;
}

public sealed record FluidSelection(string Fluid, string Backend);

/// <summary>A start guess for the solver. <see cref="PortKey"/> null applies the guess to the whole component.</summary>
public sealed record GuessModel(CycleCalculator.CycleModel.Model.BaseComponents.GuessQuantity Quantity, double Value, string? PortKey = null);
