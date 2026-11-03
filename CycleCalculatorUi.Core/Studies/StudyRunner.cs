using System.Diagnostics;
using CycleCalculator.CycleModel.Model.BaseComponents;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Persistence;
using CycleCalculatorUi.Core.Solving;

namespace CycleCalculatorUi.Core.Studies;

/// <summary>One solved (or failed) combination of a study.</summary>
public sealed class StudyPoint
{
    /// <summary>Parameter values, in the order of <see cref="StudyDefinition.Variables"/>.</summary>
    public required double[] Values { get; init; }

    public required bool Success { get; init; }

    public required string Message { get; init; }

    public EnergySummary? Energy { get; init; }

    /// <summary>Values of <see cref="StudyDefinition.Outputs"/>, in order; null where not available.</summary>
    public IReadOnlyList<double?> Outputs { get; init; } = [];

    public TimeSpan Duration { get; init; }
}

public sealed class StudyResult
{
    /// <summary>Copy of the study setup the points were calculated with.</summary>
    public required StudyDefinition Definition { get; init; }

    /// <summary>Copy of the document at the start of the run, for labels and units.</summary>
    public required SystemDocument Document { get; init; }

    public required IReadOnlyList<StudyPoint> Points { get; init; }

    /// <summary>False when the run was cancelled before every point was calculated.</summary>
    public required bool Completed { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary><see cref="StudyRunner.Fingerprint"/> of the document the study ran on.</summary>
    public string Fingerprint { get; init; } = "";
}

public readonly record struct StudyProgress(int Done, int Total, int Failed);

/// <summary>Solves every combination of a <see cref="StudyDefinition"/>, one after the other.</summary>
public sealed class StudyRunner(ComponentCatalog catalog, SolveService solver)
{
    /// <summary>
    /// Runs the study defined by <paramref name="definition"/> on a copy of <paramref name="document"/>.
    /// Cancelling returns the points calculated so far.
    /// </summary>
    public async Task<StudyResult> RunAsync(SystemDocument document, StudyDefinition definition,
        IProgress<StudyProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        SystemDocument baseDocument = DocumentSerializer.Clone(document);
        StudyDefinition setup = Copy(definition);
        IReadOnlyList<string> problems = setup.Validate(baseDocument, catalog);
        if (problems.Count > 0)
            throw new InvalidOperationException(problems[0]);

        IReadOnlyList<double[]> grid = setup.Grid();
        List<StudyPoint> points = [];
        SolveResult? previous = null;
        int failed = 0;
        Stopwatch total = Stopwatch.StartNew();
        progress?.Report(new StudyProgress(0, grid.Count, 0));

        foreach (double[] values in grid)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            SystemDocument pointDocument = DocumentSerializer.Clone(baseDocument);
            for (int i = 0; i < values.Length; i++)
                pointDocument.FindNode(setup.Variables[i].NodeId)!.Numbers[setup.Variables[i].ParameterKey] = values[i];

            SolveResult result;
            if (setup.WarmStart && previous is not null)
            {
                result = await solver.SolveAsync(WithWarmStart(pointDocument, previous), cancellationToken);
                // A poor start from a distant point can fail where the solver's own estimate succeeds.
                if (!result.Success && !cancellationToken.IsCancellationRequested && result.Issues.All(i => i.Severity != Building.IssueSeverity.Error))
                    result = await solver.SolveAsync(pointDocument, cancellationToken);
            }
            else
            {
                result = await solver.SolveAsync(pointDocument, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            if (result.Success)
                previous = result;
            else
                failed++;

            points.Add(new StudyPoint
            {
                Values = values,
                Success = result.Success,
                Message = result.Success
                    ? result.Message
                    : result.Issues.FirstOrDefault(i => i.Severity == Building.IssueSeverity.Error)?.Message ?? result.Message,
                Energy = result.Success ? EnergyBalance.Compute(pointDocument, catalog, result) : null,
                Outputs = result.Success ? setup.Outputs.Select(output => Read(result, output)).ToList() : [],
                Duration = result.Duration
            });
            progress?.Report(new StudyProgress(points.Count, grid.Count, failed));
        }

        return new StudyResult
        {
            Definition = setup,
            Document = baseDocument,
            Points = points,
            Completed = points.Count == grid.Count,
            Duration = total.Elapsed,
            Fingerprint = Fingerprint(baseDocument, setup)
        };
    }

    /// <summary>
    /// Fingerprint of everything that affects a study's results except the varied parameters themselves, so
    /// applying a study point to the document does not make the study look out of date.
    /// </summary>
    public static string Fingerprint(SystemDocument document, StudyDefinition definition)
    {
        SystemDocument copy = DocumentSerializer.Clone(document);
        foreach (StudyVariable variable in definition.Variables)
            copy.FindNode(variable.NodeId)?.Numbers.Remove(variable.ParameterKey);
        return DocumentSerializer.Fingerprint(copy);
    }

    /// <summary>
    /// A copy of the document whose start guesses are the solved port states of <paramref name="previous"/>.
    /// The user's own guesses are replaced, since the neighbouring solution is a closer start.
    /// </summary>
    public static SystemDocument WithWarmStart(SystemDocument document, SolveResult previous)
    {
        SystemDocument warm = DocumentSerializer.Clone(document);
        foreach (NodeModel node in warm.Nodes)
        {
            if (!previous.Ports.TryGetValue(node.Id, out IReadOnlyDictionary<string, PortResult>? ports))
                continue;
            List<GuessModel> guesses = [];
            foreach ((string portKey, PortResult state) in ports)
            {
                if (!double.IsFinite(state.Pressure) || !double.IsFinite(state.Enthalpy) || !double.IsFinite(state.MassFlow))
                    continue;
                guesses.Add(new GuessModel(GuessQuantity.Pressure, state.Pressure, portKey));
                guesses.Add(new GuessModel(GuessQuantity.Enthalpy, state.Enthalpy, portKey));
                guesses.Add(new GuessModel(GuessQuantity.MassFlow, state.MassFlow, portKey));
            }

            if (guesses.Count > 0)
                node.Guesses = guesses;
        }

        return warm;
    }

    private static double? Read(SolveResult result, StudyOutput output)
    {
        if (output.Kind == StudyOutputKind.Component)
        {
            return result.Outputs.TryGetValue(output.NodeId, out IReadOnlyList<OutputValue>? values)
                ? values.FirstOrDefault(v => v.Label == output.Label)?.Value
                : null;
        }

        if (result.Port(output.NodeId, output.PortKey ?? "") is not { } port)
            return null;
        return output.Quantity switch
        {
            PortQuantity.MassFlow => port.MassFlow,
            PortQuantity.Pressure => port.Pressure,
            PortQuantity.Temperature => port.Temperature,
            _ => port.Enthalpy
        };
    }

    private static StudyDefinition Copy(StudyDefinition definition) => new()
    {
        WarmStart = definition.WarmStart,
        Variables = definition.Variables.Select(v => new StudyVariable
        {
            NodeId = v.NodeId, ParameterKey = v.ParameterKey, Start = v.Start, End = v.End, Steps = v.Steps, Spacing = v.Spacing
        }).ToList(),
        Outputs = definition.Outputs.Select(o => new StudyOutput
        {
            Kind = o.Kind, NodeId = o.NodeId, PortKey = o.PortKey, Quantity = o.Quantity, Label = o.Label, Unit = o.Unit
        }).ToList()
    };
}
