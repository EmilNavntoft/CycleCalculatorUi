using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Persistence;
using CycleCalculatorUi.Core.Solving;

namespace CycleCalculatorUi.Core.Editing;

/// <summary>
/// The open document plus selection, undo/redo and the latest calculation result.
/// All edits go through this class so undo and change notification work everywhere.
/// </summary>
public sealed class EditorState(ComponentCatalog catalog, SolveService solver)
{
    private const int MaxUndo = 200;

    private readonly ModelBuilder _builder = new(catalog);
    private readonly LinkedList<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private CancellationTokenSource? _solveCancellation;

    private int _revision;
    private int _cachedRevision = -1;
    private string _fingerprint = "";
    private IReadOnlyList<ValidationIssue> _issues = [];
    private IReadOnlyList<FluidCircuit> _circuits = [];

    public ComponentCatalog Catalog { get; } = catalog;

    public SystemDocument Document { get; private set; } = new();

    /// <summary>Raised after any change to the document, selection or results.</summary>
    public event Action? Changed;

    #region selection

    public IReadOnlySet<string> SelectedNodeIds => _selectedNodes;

    private readonly HashSet<string> _selectedNodes = [];

    public string? SelectedConnectionId { get; private set; }

    public NodeModel? SelectedNode => _selectedNodes.Count == 1 ? Document.FindNode(_selectedNodes.First()) : null;

    public void Select(string? nodeId, bool additive = false)
    {
        if (!additive)
            _selectedNodes.Clear();
        SelectedConnectionId = null;
        if (nodeId is not null)
        {
            // Additive selection toggles the node.
            if (!additive || !_selectedNodes.Remove(nodeId))
                _selectedNodes.Add(nodeId);
        }

        Notify();
    }

    public void SelectNodes(IEnumerable<string> nodeIds, bool additive = false)
    {
        if (!additive)
            _selectedNodes.Clear();
        _selectedNodes.UnionWith(nodeIds);
        SelectedConnectionId = null;
        Notify();
    }

    public void SelectConnection(string? connectionId)
    {
        _selectedNodes.Clear();
        SelectedConnectionId = connectionId;
        Notify();
    }

    #endregion

    #region derived state

    /// <summary>Validation issues of the current document.</summary>
    public IReadOnlyList<ValidationIssue> Issues
    {
        get
        {
            Refresh();
            return _issues;
        }
    }

    public IReadOnlyList<ValidationIssue> IssuesFor(string nodeId) =>
        Issues.Where(issue => issue.NodeId == nodeId).ToList();

    public IReadOnlyList<FluidCircuit> Circuits
    {
        get
        {
            Refresh();
            return _circuits;
        }
    }

    public FluidSelection FluidOf(string nodeId, int stream) =>
        FluidCircuitResolver.CircuitOf(Circuits, new StreamRef(nodeId, stream))?.Fluid ?? Document.DefaultFluid;

    public SolveResult? Result { get; private set; }

    public bool IsSolving { get; private set; }

    /// <summary>True when the document changed in a way that affects the calculation since the last solve.</summary>
    public bool IsResultStale
    {
        get
        {
            Refresh();
            return Result is not null && Result.Fingerprint != _fingerprint;
        }
    }

    private void Refresh()
    {
        if (_cachedRevision == _revision)
            return;
        _issues = _builder.Validate(Document);
        _circuits = FluidCircuitResolver.Resolve(Document, Catalog);
        _fingerprint = DocumentSerializer.Fingerprint(Document);
        _cachedRevision = _revision;
    }

    #endregion

    #region document lifecycle

    public void Load(SystemDocument document)
    {
        Document = document;
        _undo.Clear();
        _redo.Clear();
        _selectedNodes.Clear();
        SelectedConnectionId = null;
        Result = null;
        Touch();
    }

    public void New() => Load(new SystemDocument());

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Undo()
    {
        if (_undo.Last is not { } last)
            return;
        _redo.Push(DocumentSerializer.Serialize(Document));
        _undo.RemoveLast();
        Restore(last.Value);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out string? next))
            return;
        PushUndo();
        Restore(next);
    }

    private void Restore(string json)
    {
        Document = DocumentSerializer.Deserialize(json);
        _selectedNodes.RemoveWhere(id => Document.FindNode(id) is null);
        if (SelectedConnectionId is not null && Document.Connections.All(c => c.Id != SelectedConnectionId))
            SelectedConnectionId = null;
        Touch();
    }

    /// <summary>Applies an edit as one undoable step.</summary>
    public void Edit(Action<SystemDocument> change)
    {
        PushUndo();
        _redo.Clear();
        change(Document);
        Touch();
    }

    private void PushUndo()
    {
        _undo.AddLast(DocumentSerializer.Serialize(Document));
        if (_undo.Count > MaxUndo)
            _undo.RemoveFirst();
    }

    private void Touch()
    {
        _revision++;
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    #endregion

    #region edits

    public NodeModel AddNode(string typeId, double x, double y)
    {
        ComponentDescriptor descriptor = Catalog.Get(typeId);
        NodeModel node = descriptor.CreateNode(UniqueName(descriptor.NamePrefix), x, y, Document.DefaultFluid);
        Edit(document => document.Nodes.Add(node));
        Select(node.Id);
        return node;
    }

    public void MoveNodes(IReadOnlyDictionary<string, (double X, double Y)> positions)
    {
        Edit(document =>
        {
            Dictionary<string, (double Dx, double Dy)> deltas = [];
            foreach ((string id, (double x, double y)) in positions)
            {
                if (document.FindNode(id) is { } node)
                {
                    deltas[id] = (x - node.X, y - node.Y);
                    node.X = x;
                    node.Y = y;
                }
            }

            // A shaped route between two components that move together moves with them.
            // When only one end moves, the route keeps its corners and the end segments stretch.
            foreach (ConnectionModel connection in document.Connections)
            {
                if (connection.Points is null ||
                    !deltas.TryGetValue(connection.From.NodeId, out (double Dx, double Dy) from) ||
                    !deltas.TryGetValue(connection.To.NodeId, out (double Dx, double Dy) to) ||
                    from != to)
                {
                    continue;
                }

                connection.Points = connection.Points.Select(p => new CanvasPoint(p.X + from.Dx, p.Y + from.Dy)).ToList();
            }
        });
    }

    /// <summary>Sets the corners of a connection's route; null or empty restores the automatic route.</summary>
    public void SetConnectionPoints(string connectionId, IReadOnlyList<CanvasPoint>? points)
    {
        if (Document.Connections.All(c => c.Id != connectionId))
            return;
        Edit(document =>
        {
            if (document.Connections.FirstOrDefault(c => c.Id == connectionId) is { } connection)
                connection.Points = points is { Count: > 0 } ? points.ToList() : null;
        });
    }

    public void RemoveSelection()
    {
        if (SelectedConnectionId is { } connectionId)
        {
            Edit(document => document.Connections.RemoveAll(c => c.Id == connectionId));
            SelectedConnectionId = null;
            Notify();
            return;
        }

        if (_selectedNodes.Count == 0)
            return;
        HashSet<string> removed = [.. _selectedNodes];
        Edit(document =>
        {
            document.Nodes.RemoveAll(node => removed.Contains(node.Id));
            document.Connections.RemoveAll(c => removed.Contains(c.From.NodeId) || removed.Contains(c.To.NodeId));
        });
        _selectedNodes.Clear();
        Notify();
    }

    /// <summary>
    /// Connects two ports if allowed. When the two fluid circuits use different fluids,
    /// the circuit of <paramref name="b"/> adopts the fluid of <paramref name="a"/>'s circuit.
    /// Returns null on success, otherwise a reason to show the user.
    /// </summary>
    public string? Connect(PortRef a, PortRef b)
    {
        ConnectionProposal? proposal = ConnectionRules.TryConnect(Document, Catalog, a, b, out string? reason);
        if (proposal is null)
            return reason;

        Edit(document =>
        {
            if (proposal.Kind == ConnectionKind.Fluid &&
                FluidCircuitResolver.StreamOf(document, Catalog, a) is { } streamA &&
                FluidCircuitResolver.StreamOf(document, Catalog, b) is { } streamB)
            {
                IReadOnlyList<FluidCircuit> circuits = FluidCircuitResolver.Resolve(document, Catalog);
                FluidSelection fluidA = FluidCircuitResolver.CircuitOf(circuits, streamA)?.Fluid ?? document.DefaultFluid;
                FluidSelection fluidB = FluidCircuitResolver.CircuitOf(circuits, streamB)?.Fluid ?? document.DefaultFluid;
                if (fluidA != fluidB)
                    FluidCircuitResolver.SetCircuitFluid(document, Catalog, streamB, fluidA);
            }

            document.Connections.Add(new ConnectionModel { Kind = proposal.Kind, From = proposal.From, To = proposal.To });
        });
        return null;
    }

    public void SetNumber(string nodeId, string key, double? value) =>
        EditNode(nodeId, node => node.Numbers[key] = value);

    public void SetCoefficients(string nodeId, string key, double[]? values) =>
        EditNode(nodeId, node => node.Arrays[key] = values);

    public void SetChoice(string nodeId, string key, string value) =>
        EditNode(nodeId, node => node.Choices[key] = value);

    public void Rename(string nodeId, string name) => EditNode(nodeId, node => node.Name = name.Trim());

    /// <summary>Rotates the selected components by a multiple of 90 degrees, as one undo step.</summary>
    public void RotateSelection(int degrees) => EditSelectedNodes(node =>
        node.Rotation = ((node.Rotation + degrees) % 360 + 360) % 360);

    /// <summary>Mirrors the selected components horizontally, as one undo step.</summary>
    public void FlipSelection() => EditSelectedNodes(node => node.FlipHorizontal = !node.FlipHorizontal);

    public void SelectAll() => SelectNodes(Document.Nodes.Select(node => node.Id));

    private void EditSelectedNodes(Action<NodeModel> change)
    {
        if (_selectedNodes.Count == 0)
            return;
        Edit(document =>
        {
            foreach (NodeModel node in document.Nodes.Where(node => _selectedNodes.Contains(node.Id)))
                change(node);
        });
    }

    public void SetCircuitFluid(StreamRef stream, FluidSelection fluid) =>
        Edit(document => FluidCircuitResolver.SetCircuitFluid(document, Catalog, stream, fluid));

    public void SetDocumentName(string name) => Edit(document => document.Name = name.Trim());

    private void EditNode(string nodeId, Action<NodeModel> change)
    {
        if (Document.FindNode(nodeId) is null)
            return;
        Edit(document => change(document.FindNode(nodeId)!));
    }

    private string UniqueName(string prefix)
    {
        HashSet<string> used = Document.Nodes.Select(node => node.Name).ToHashSet();
        int index = 1;
        while (used.Contains($"{prefix}{index}"))
            index++;
        return $"{prefix}{index}";
    }

    #endregion

    #region solving

    public async Task SolveAsync()
    {
        if (IsSolving)
            return;
        IsSolving = true;
        _solveCancellation = new CancellationTokenSource();
        Notify();
        try
        {
            Result = await solver.SolveAsync(Document, _solveCancellation.Token);
        }
        finally
        {
            IsSolving = false;
            _solveCancellation.Dispose();
            _solveCancellation = null;
            Notify();
        }
    }

    public void CancelSolve() => _solveCancellation?.Cancel();

    #endregion
}
