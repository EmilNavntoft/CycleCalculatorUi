using System.Diagnostics;
using CycleCalculator.CycleModel.Model;
using CycleCalculator.CycleModel.Model.BaseComponents;
using CycleCalculator.CycleModel.Model.Connectors;
using CycleCalculator.CycleModel.Solver;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Persistence;

namespace CycleCalculatorUi.Core.Solving;

/// <summary>Solved state of one port, in backend units.</summary>
public sealed record PortResult(double MassFlow, double Pressure, double Temperature, double Enthalpy);

public sealed class SolveResult
{
    /// <summary>True when the solver converged.</summary>
    public required bool Success { get; init; }

    /// <summary>True when port values are available. A solve that did not converge still carries the solver's
    /// last iterate (with Success = false) so it can be inspected.</summary>
    public bool HasResults => Ports.Count > 0;

    public required string Message { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    /// <summary>Node id → port key → result.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, PortResult>> Ports { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, PortResult>>();

    /// <summary>Node id → component outputs such as a valve's outlet pressure.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<OutputValue>> Outputs { get; init; } =
        new Dictionary<string, IReadOnlyList<OutputValue>>();

    /// <summary>Node id → temperatures of both streams along a heat exchanger.</summary>
    public IReadOnlyDictionary<string, TemperatureProfile> TemperatureProfiles { get; init; } =
        new Dictionary<string, TemperatureProfile>();

    /// <summary>Fingerprint of the document that was solved; compare to detect stale results.</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>Largest scaled residual of every nonlinear solver iteration, in order.</summary>
    public IReadOnlyList<SolverIteration> ResidualHistory { get; init; } = [];

    public PortResult? Port(string nodeId, string portKey) =>
        Ports.TryGetValue(nodeId, out var ports) ? ports.GetValueOrDefault(portKey) : null;
}

public sealed class SolveOptions
{
    /// <summary>Cancel the solver after this time. The backend stops at its next cancellation check (between
    /// solver iterations), so a single slow property call can still finish in the background.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(240);

    /// <summary>Maximum number of solves running at once (protects a shared web server).</summary>
    public int MaxConcurrentSolves { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Report solver progress every this many iterations…</summary>
    public int ProgressInterval { get; init; } = 10;

    /// <summary>…but not more often than this, so a fast solve does not flood the UI with updates.</summary>
    public TimeSpan MinProgressPeriod { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>Builds and solves documents on a background thread.</summary>
public sealed class SolveService
{
    private readonly ComponentCatalog _catalog;
    private readonly ModelBuilder _builder;
    private readonly SolveOptions _options;
    private readonly SemaphoreSlim _slots;

    public SolveService(ComponentCatalog catalog, SolveOptions? options = null)
    {
        _catalog = catalog;
        _builder = new ModelBuilder(catalog);
        _options = options ?? new SolveOptions();
        _slots = new SemaphoreSlim(_options.MaxConcurrentSolves);
    }

    /// <param name="onProgress">Receives the residual history so far while the solver runs, called from the
    /// solving thread every <see cref="SolveOptions.ProgressInterval"/> iterations.</param>
    public async Task<SolveResult> SolveAsync(SystemDocument document, CancellationToken cancellationToken = default,
        Action<IReadOnlyList<SolverIteration>>? onProgress = null)
    {
        // Work on a copy so edits made while solving cannot affect this solve.
        SystemDocument snapshot = DocumentSerializer.Clone(document);
        string fingerprint = DocumentSerializer.Fingerprint(snapshot);
        Stopwatch stopwatch = Stopwatch.StartNew();
        ResidualRecorder recorder = new(_options, onProgress);

        BuildResult build = _builder.Build(snapshot);
        if (!build.Success)
        {
            return new SolveResult
            {
                Success = false,
                Message = build.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message
                          ?? "The system could not be built.",
                Issues = build.Issues,
                Fingerprint = fingerprint,
                Duration = stopwatch.Elapsed
            };
        }

        try
        {
            await _slots.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure("The calculation was cancelled.");
        }

        // Cancels the backend solve when the caller cancels or the timeout elapses.
        CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(_options.Timeout);
        Task<SolveOutcome> solve = Task.Run(() => Orchestrator.TrySolve(build.Components, stop.Token, recorder.Add),
            CancellationToken.None);
        // Free the slot when the solver really finishes, even if we stopped waiting for it earlier.
        _ = solve.ContinueWith(_ =>
        {
            stop.Dispose();
            _slots.Release();
        }, TaskScheduler.Default);
        SolveOutcome outcome;
        try
        {
            // Return as soon as cancellation is requested rather than waiting for the backend's next check.
            outcome = await solve.WaitAsync(stop.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure($"The solver did not finish within {_options.Timeout.TotalSeconds:0} s.");
        }
        catch (OperationCanceledException)
        {
            return Failure("The calculation was cancelled.");
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }

        return new SolveResult
        {
            Success = outcome.Converged,
            Message = outcome.Converged
                ? $"Solved in {stopwatch.Elapsed.TotalSeconds:0.00} s."
                : outcome.ErrorMessage!,
            Duration = stopwatch.Elapsed,
            Issues = build.Issues,
            Ports = ReadPorts(snapshot, build),
            Outputs = ReadOutputs(snapshot, build),
            TemperatureProfiles = outcome.Converged ? ReadTemperatureProfiles(snapshot, build) : [],
            Fingerprint = fingerprint,
            ResidualHistory = recorder.Finish()
        };

        SolveResult Failure(string message) => new()
        {
            Success = false,
            Message = message,
            Issues = build.Issues,
            Fingerprint = fingerprint,
            Duration = stopwatch.Elapsed,
            ResidualHistory = recorder.Finish()
        };
    }

    /// <summary>Collects the solver's iterations and forwards them to the caller at a limited rate.</summary>
    private sealed class ResidualRecorder(SolveOptions options, Action<IReadOnlyList<SolverIteration>>? onProgress)
    {
        private readonly List<SolverIteration> _history = [];
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();
        private bool _finished;

        public void Add(SolverIteration iteration)
        {
            SolverIteration[] snapshot;
            lock (_history)
            {
                // A solve we stopped waiting for (timeout, cancel) can keep running; ignore it.
                if (_finished)
                    return;
                _history.Add(iteration);
                if (onProgress is null || iteration.Iteration % options.ProgressInterval != 0 ||
                    _sinceReport.Elapsed < options.MinProgressPeriod)
                    return;
                _sinceReport.Restart();
                snapshot = _history.ToArray();
            }

            onProgress(snapshot);
        }

        public IReadOnlyList<SolverIteration> Finish()
        {
            lock (_history)
            {
                _finished = true;
                return _history.ToArray();
            }
        }
    }

    private Dictionary<string, IReadOnlyDictionary<string, PortResult>> ReadPorts(SystemDocument document, BuildResult build)
    {
        Dictionary<string, IReadOnlyDictionary<string, PortResult>> results = [];
        foreach (NodeModel node in document.Nodes)
        {
            ComponentDescriptor descriptor = _catalog.Get(node.TypeId);
            CycleComponent component = build.ComponentsByNode[node.Id];
            Dictionary<string, PortResult> ports = [];
            foreach (PortDescriptor port in descriptor.Ports.Where(p => p.Kind == PortKind.Fluid))
            {
                Port solved = descriptor.GetPort(component, port.Key);
                ports[port.Key] = new PortResult(solved.MassFlow, solved.Pressure, solved.Temperature, solved.Enthalpy);
            }

            results[node.Id] = ports;
        }

        return results;
    }

    private Dictionary<string, IReadOnlyList<OutputValue>> ReadOutputs(SystemDocument document, BuildResult build)
    {
        Dictionary<string, IReadOnlyList<OutputValue>> results = [];
        foreach (NodeModel node in document.Nodes)
        {
            ComponentDescriptor descriptor = _catalog.Get(node.TypeId);
            if (descriptor.ReadOutputs is not { } read)
                continue;
            try
            {
                results[node.Id] = read(build.ComponentsByNode[node.Id]).ToList();
            }
            catch (Exception)
            {
                // Outputs are extra information; a component that cannot report them should not fail the solve.
            }
        }

        return results;
    }

    private Dictionary<string, TemperatureProfile> ReadTemperatureProfiles(SystemDocument document, BuildResult build)
    {
        Dictionary<string, TemperatureProfile> results = [];
        foreach (NodeModel node in document.Nodes)
        {
            if (_catalog.Get(node.TypeId).ReadTemperatureProfile is not { } read)
                continue;
            try
            {
                results[node.Id] = read(build.ComponentsByNode[node.Id]);
            }
            catch (Exception)
            {
                // A profile is extra information; a failed property call should not fail the solve.
            }
        }

        return results;
    }
}
