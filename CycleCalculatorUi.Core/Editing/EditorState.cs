using CycleCalculator.CycleModel.Model.BaseComponents;
using CycleCalculator.CycleModel.Solver;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Persistence;
using CycleCalculatorUi.Core.Solving;
using CycleCalculatorUi.Core.Studies;

namespace CycleCalculatorUi.Core.Editing;

/// <summary>
/// The open document plus selection, undo/redo and the latest calculation result.
/// All edits go through this class so undo and change notification work everywhere.
/// </summary>
public sealed class EditorState(ComponentCatalog catalog, SolveService solver)
{
    private const int MaxUndo = 200;

    private readonly ModelBuilder _builder = new(catalog);
    private readonly StudyRunner _studyRunner = new(catalog, solver);
    private CancellationTokenSource? _studyCancellation;
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

    /// <summary>Max residual per solver iteration: live while solving, otherwise from the latest result.</summary>
    public IReadOnlyList<SolverIteration> ResidualHistory =>
        IsSolving ? _liveResidualHistory : Result?.ResidualHistory ?? [];

    private IReadOnlyList<SolverIteration> _liveResidualHistory = [];

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
        StudyResult = null;
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

    /// <summary>Adds an item to a repeated parameter group, starting as a copy of the last item.</summary>
    public void AddGroupItem(string nodeId, string groupKey)
    {
        if (Document.FindNode(nodeId) is not { } node || Catalog.Find(node.TypeId) is not { } descriptor ||
            descriptor.FindGroup(groupKey) is not { } group || group.Count(node) >= group.MaxCount)
            return;

        EditNode(nodeId, n =>
        {
            int count = group.Count(n);
            foreach (ParameterDescriptor parameter in descriptor.Parameters.Where(p => p.Group == groupKey))
            {
                string last = RepeatGroupDescriptor.ItemKey(parameter.Key, count - 1);
                string added = RepeatGroupDescriptor.ItemKey(parameter.Key, count);
                if (n.Numbers.TryGetValue(last, out double? number))
                    n.Numbers[added] = number;
                if (n.Arrays.TryGetValue(last, out double[]? values))
                    n.Arrays[added] = values?.ToArray();
                if (n.Choices.TryGetValue(last, out string? choice))
                    n.Choices[added] = choice;
            }

            n.GroupCounts[groupKey] = count + 1;
        });
    }

    /// <summary>Removes the item at zero-based <paramref name="index"/> from a repeated group; later items move up.</summary>
    public void RemoveGroupItem(string nodeId, string groupKey, int index)
    {
        if (Document.FindNode(nodeId) is not { } node || Catalog.Find(node.TypeId) is not { } descriptor ||
            descriptor.FindGroup(groupKey) is not { } group || group.Count(node) <= group.MinCount ||
            index < 0 || index >= group.Count(node))
            return;

        Edit(document =>
        {
            NodeModel n = document.FindNode(nodeId)!;
            int count = group.Count(n);
            foreach (ParameterDescriptor parameter in descriptor.Parameters.Where(p => p.Group == groupKey))
            {
                for (int i = index; i < count - 1; i++)
                {
                    string target = RepeatGroupDescriptor.ItemKey(parameter.Key, i);
                    string source = RepeatGroupDescriptor.ItemKey(parameter.Key, i + 1);
                    MoveValue(n.Numbers, source, target);
                    MoveValue(n.Arrays, source, target);
                    MoveValue(n.Choices, source, target);
                }

                string removed = RepeatGroupDescriptor.ItemKey(parameter.Key, count - 1);
                n.Numbers.Remove(removed);
                n.Arrays.Remove(removed);
                n.Choices.Remove(removed);
            }

            n.GroupCounts[groupKey] = count - 1;

            // Study variables on the removed item go away; those on later items follow their item.
            if (document.Study is { } study)
            {
                study.Variables.RemoveAll(v => v.NodeId == nodeId && GroupIndex(v.ParameterKey) == index);
                foreach (StudyVariable variable in study.Variables.Where(v => v.NodeId == nodeId))
                {
                    (string baseKey, int i) = RepeatGroupDescriptor.Parse(variable.ParameterKey);
                    if (i > index && descriptor.FindParameter(baseKey)?.Group == groupKey)
                        variable.ParameterKey = RepeatGroupDescriptor.ItemKey(baseKey, i - 1);
                }
            }

            int? GroupIndex(string key) =>
                RepeatGroupDescriptor.Parse(key) is var (baseKey, i) && descriptor.FindParameter(baseKey)?.Group == groupKey
                    ? i
                    : null;
        });

        static void MoveValue<T>(Dictionary<string, T> values, string source, string target)
        {
            if (values.Remove(source, out T? value))
                values[target] = value;
            else
                values.Remove(target);
        }
    }

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
        _liveResidualHistory = [];
        _solveCancellation = new CancellationTokenSource();
        Notify();
        try
        {
            Result = await solver.SolveAsync(Document, _solveCancellation.Token, history =>
            {
                if (!IsSolving)
                    return;
                _liveResidualHistory = history;
                Notify();
            });
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

    /// <summary>Energy totals of the latest successful calculation.</summary>
    public EnergySummary? Energy => Result is { Success: true } result ? EnergyBalance.Compute(Document, Catalog, result) : null;

    public void SetEnergyRole(string nodeId, EnergyRole role) => EditNode(nodeId, node => node.EnergyRole = role);

    #endregion

    #region start guesses

    /// <summary>Adds a start guess to a node, valued from the latest result when possible.</summary>
    public void AddGuess(string nodeId)
    {
        if (Document.FindNode(nodeId) is not { } node || Catalog.Find(node.TypeId) is not { } descriptor)
            return;
        GuessScope scope = StartGuesses.Scopes(descriptor)[0];
        // Suggest a quantity this scope does not have a guess for yet.
        GuessQuantity quantity = StartGuesses.Quantities.Select(q => q.Quantity)
            .FirstOrDefault(q => !node.Guesses.Any(g => g.Quantity == q && scope.Matches(g)), GuessQuantity.Pressure);
        GuessModel guess = StartGuesses.Create(scope, quantity, node, descriptor, Result);
        EditNode(nodeId, n => n.Guesses.Add(guess));
    }

    public void SetGuess(string nodeId, int index, GuessModel guess) => EditNode(nodeId, node =>
    {
        if (index >= 0 && index < node.Guesses.Count)
            node.Guesses[index] = guess;
    });

    public void RemoveGuess(string nodeId, int index) => EditNode(nodeId, node =>
    {
        if (index >= 0 && index < node.Guesses.Count)
            node.Guesses.RemoveAt(index);
    });

    /// <summary>Replaces a node's guesses with the pressure, enthalpy and mass flow of each port in the latest result.</summary>
    public void SetGuessesFromResult(string nodeId)
    {
        if (Result is not { HasResults: true } result || Document.FindNode(nodeId) is not { } node ||
            Catalog.Find(node.TypeId) is not { } descriptor)
            return;
        List<GuessModel> guesses = StartGuesses.AllFromResult(node, descriptor, result);
        if (guesses.Count > 0)
            EditNode(nodeId, n => n.Guesses = guesses);
    }

    public void ClearGuesses(string nodeId) => EditNode(nodeId, node => node.Guesses.Clear());

    #endregion

    #region parametric study

    public StudyResult? StudyResult { get; private set; }

    public bool IsStudyRunning { get; private set; }

    public StudyProgress StudyProgress { get; private set; }

    /// <summary>True when the document changed since the study ran (apart from the varied parameters).</summary>
    public bool IsStudyStale => StudyResult is { } result &&
                                StudyRunner.Fingerprint(Document, result.Definition) != result.Fingerprint;

    /// <summary>Raised when a parameter was added to the study from elsewhere, so the study view can be shown.</summary>
    public event Action? StudyRequested;

    /// <summary>Changes the study setup as one undoable step, creating it if needed.</summary>
    public void EditStudy(Action<StudyDefinition> change) => Edit(document => change(document.Study ??= new StudyDefinition()));

    /// <summary>Adds a numeric parameter to the study, spanning ±20 % of its current value in 5 steps.</summary>
    public void AddStudyVariable(string nodeId, string parameterKey)
    {
        if (Document.FindNode(nodeId) is not { } node || Catalog.Find(node.TypeId)?.FindParameter(parameterKey) is not { } parameter)
            return;
        if (Document.Study?.Variables.Any(v => v.NodeId == nodeId && v.ParameterKey == parameterKey) == true)
            return;
        double value = node.Numbers.GetValueOrDefault(parameterKey, parameter.DefaultNumber) ?? parameter.DefaultNumber ?? 1;
        double span = Math.Abs(value) * 0.2;
        if (span == 0)
            span = 1;
        double start = value - span, end = value + span;
        if (parameter.Min is { } min && start < min)
            start = parameter.MinExclusive ? (value + min) / 2 : min;
        if (parameter.Max is { } max && end > max)
            end = max;
        EditStudy(study => study.Variables.Add(new StudyVariable
        {
            NodeId = nodeId, ParameterKey = parameterKey, Start = start, End = end, Steps = 5
        }));
        StudyRequested?.Invoke();
    }

    public async Task RunStudyAsync()
    {
        if (IsStudyRunning || Document.Study is not { } study)
            return;
        IsStudyRunning = true;
        StudyProgress = new StudyProgress(0, (int)Math.Min(int.MaxValue, study.PointCount()), 0);
        _studyCancellation = new CancellationTokenSource();
        Notify();
        try
        {
            StudyResult = await _studyRunner.RunAsync(Document, study, new CallbackProgress(p =>
            {
                StudyProgress = p;
                Notify();
            }), _studyCancellation.Token);
        }
        finally
        {
            IsStudyRunning = false;
            _studyCancellation.Dispose();
            _studyCancellation = null;
            Notify();
        }
    }

    public void CancelStudy() => _studyCancellation?.Cancel();

    /// <summary>Sets the document's parameters to the values of one study point, as one undoable step.</summary>
    public void ApplyStudyPoint(StudyResult result, StudyPoint point) => Edit(document =>
    {
        for (int i = 0; i < point.Values.Length; i++)
        {
            StudyVariable variable = result.Definition.Variables[i];
            if (document.FindNode(variable.NodeId) is { } node)
                node.Numbers[variable.ParameterKey] = point.Values[i];
        }
    });

    /// <summary>Reports progress on the calling thread (unlike Progress&lt;T&gt;, which posts to a captured context).</summary>
    private sealed class CallbackProgress(Action<StudyProgress> report) : IProgress<StudyProgress>
    {
        public void Report(StudyProgress value) => report(value);
    }

    #endregion
}
