using CycleCalculator.CycleModel.Fluids;

namespace CycleCalculatorUi.Core.Document;

/// <summary>
/// The editable description of a cycle as drawn in the editor. It holds no backend objects;
/// <see cref="Building.ModelBuilder"/> turns it into fresh solver components on every solve.
/// </summary>
public sealed class SystemDocument
{
    /// <summary>
    /// 2: controllers select what they measure (<c>controlSignal</c>) instead of having one mode per measured quantity.
    /// </summary>
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;

    public string Name { get; set; } = "Untitled system";

    /// <summary>Fluid given to streams that are not connected to anything with a fluid yet.</summary>
    public FluidSelection DefaultFluid { get; set; } = new(CoolPropFluids.CarbonDioxide, CoolPropBackends.HEOS);

    public List<NodeModel> Nodes { get; set; } = [];

    public List<ConnectionModel> Connections { get; set; } = [];

    /// <summary>Setup of the parametric study, if one has been defined. It does not affect single calculations.</summary>
    public Studies.StudyDefinition? Study { get; set; }

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

    /// <summary>
    /// Number of items of each repeated parameter group, e.g. compressors in parallel. Missing groups have their
    /// minimum count. The values of item n &gt; 1 are stored under "key#n" in the dictionaries above.
    /// </summary>
    public Dictionary<string, int> GroupCounts { get; set; } = [];

    /// <summary>Choice parameters such as an operating mode.</summary>
    public Dictionary<string, string> Choices { get; set; } = [];

    /// <summary>
    /// Embedded data stored next to a parameter under its key, e.g. the whole EN 12900 compressor model chosen from the
    /// library (JSON), so the document does not depend on the library.
    /// </summary>
    public Dictionary<string, string> Texts { get; set; } = [];

    /// <summary>Fluid of each stream of the component (one entry for most components, two for heat exchangers).</summary>
    public List<FluidSelection> Fluids { get; set; } = [];

    /// <summary>How the component counts in the energy totals; <see cref="Solving.EnergyRole.Auto"/> follows its type.</summary>
    public Solving.EnergyRole EnergyRole { get; set; } = Solving.EnergyRole.Auto;

    /// <summary>Optional solver start guesses.</summary>
    public List<GuessModel> Guesses { get; set; } = [];

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public enum ConnectionKind
{
    /// <summary>Physical fluid connection between two fluid ports.</summary>
    Fluid,

    /// <summary>Read-only measurement from a fluid port or a signal output (source) to a signal input (target).</summary>
    Signal
}

public sealed record PortRef(string NodeId, string PortKey);

/// <summary>
/// A connection between two ports. For signal connections <see cref="From"/> is the measured fluid port or signal
/// output and <see cref="To"/> is the signal input.
/// </summary>
public sealed class ConnectionModel
{
    public string Id { get; set; } = NodeModel.NewId();

    public ConnectionKind Kind { get; set; }

    public PortRef From { get; set; } = new("", "");

    public PortRef To { get; set; } = new("", "");

    /// <summary>
    /// Corners of a route the user has shaped by dragging, in canvas units, between the short stubs that
    /// leave each port. Null means the route is drawn automatically.
    /// </summary>
    public List<CanvasPoint>? Points { get; set; }

    public bool Touches(string nodeId) => From.NodeId == nodeId || To.NodeId == nodeId;

    public bool Touches(PortRef port) => From == port || To == port;
}

public sealed record CanvasPoint(double X, double Y);

public sealed record FluidSelection(string Fluid, string Backend);

/// <summary>
/// A start guess for the solver. It applies to one port when <see cref="PortKey"/> is set, otherwise to the fluid
/// ports of one stream when <see cref="Stream"/> is set (e.g. one side of a heat exchanger), otherwise to the whole
/// component. A mass flow guess on a port is positive into the component; on a stream or component it is the throughput.
/// </summary>
public sealed record GuessModel(
    CycleCalculator.CycleModel.Model.BaseComponents.GuessQuantity Quantity,
    double Value,
    string? PortKey = null,
    int? Stream = null);
