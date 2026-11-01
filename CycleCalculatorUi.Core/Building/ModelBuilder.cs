using System.Globalization;
using CycleCalculator.CycleModel.Model.BaseComponents;
using CycleCalculator.CycleModel.Model.Connectors;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Building;

public enum IssueSeverity
{
    Warning,
    Error
}

/// <summary>A problem found in the document, pointing at the node, parameter or connection it concerns.</summary>
public sealed record ValidationIssue(
    IssueSeverity Severity,
    string Message,
    string? NodeId = null,
    string? ParameterKey = null,
    string? PortKey = null,
    string? ConnectionId = null);

/// <summary>The backend components built from a document, with a map back to the editor's nodes.</summary>
public sealed class BuildResult
{
    public required IReadOnlyList<ValidationIssue> Issues { get; init; }

    public required IReadOnlyList<CycleComponent> Components { get; init; }

    /// <summary>Node id → backend component. Empty when the build failed.</summary>
    public required IReadOnlyDictionary<string, CycleComponent> ComponentsByNode { get; init; }

    public bool Success => Issues.All(issue => issue.Severity != IssueSeverity.Error) && Components.Count > 0;
}

/// <summary>Turns a <see cref="SystemDocument"/> into fresh backend components ready for the solver.</summary>
public sealed class ModelBuilder(ComponentCatalog catalog)
{
    /// <summary>Checks the document without creating backend components (cheap enough to run on every edit).</summary>
    public IReadOnlyList<ValidationIssue> Validate(SystemDocument document)
    {
        List<ValidationIssue> issues = [];

        if (document.Nodes.Count == 0)
            issues.Add(new ValidationIssue(IssueSeverity.Error, "The system is empty. Add components to the canvas."));

        foreach (IGrouping<string, NodeModel> duplicate in document.Nodes.GroupBy(n => n.Name).Where(g => g.Count() > 1))
        {
            foreach (NodeModel node in duplicate)
                issues.Add(new ValidationIssue(IssueSeverity.Warning,
                    $"The name '{duplicate.Key}' is used by more than one component.", node.Id));
        }

        foreach (NodeModel node in document.Nodes)
        {
            if (catalog.Find(node.TypeId) is not { } descriptor)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"Unknown component type '{node.TypeId}'.", node.Id));
                continue;
            }

            if (string.IsNullOrWhiteSpace(node.Name))
                issues.Add(new ValidationIssue(IssueSeverity.Error, "The component needs a name.", node.Id));

            ValidateParameters(node, descriptor, issues);

            foreach (PortDescriptor port in descriptor.Ports.Where(port => port.Required(node)))
            {
                if (!ConnectionRules.IsConnected(document, new PortRef(node.Id, port.Key)))
                {
                    string what = port.Kind == PortKind.SignalInput ? "signal input" : "port";
                    issues.Add(new ValidationIssue(IssueSeverity.Error,
                        $"{node.Name}: the {what} '{port.Label}' is not connected.", node.Id, PortKey: port.Key));
                }
            }
        }

        foreach (ConnectionModel connection in document.Connections)
        {
            if (ConnectionRules.Describe(document, catalog, connection.From) is null ||
                ConnectionRules.Describe(document, catalog, connection.To) is null)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "A connection refers to a missing port.",
                    ConnectionId: connection.Id));
            }
        }

        foreach (FluidCircuit circuit in FluidCircuitResolver.Resolve(document, catalog).Where(c => c.HasConflict))
        {
            string names = string.Join(", ", circuit.Members
                .Select(m => document.FindNode(m.NodeId)?.Name).OfType<string>().Distinct());
            issues.Add(new ValidationIssue(IssueSeverity.Error,
                $"Connected components use different fluids ({names}). Choose one fluid for the circuit.",
                circuit.Members[0].NodeId));
        }

        return issues;
    }

    /// <summary>Validates the document and, if there are no errors, creates and connects the backend components.</summary>
    public BuildResult Build(SystemDocument document)
    {
        List<ValidationIssue> issues = [.. Validate(document)];
        if (issues.Any(issue => issue.Severity == IssueSeverity.Error))
            return Failed(issues);

        IReadOnlyList<FluidCircuit> circuits = FluidCircuitResolver.Resolve(document, catalog);
        Dictionary<string, CycleComponent> byNode = [];
        Dictionary<string, ComponentDescriptor> descriptors = [];

        foreach (NodeModel node in document.Nodes)
        {
            ComponentDescriptor descriptor = catalog.Get(node.TypeId);
            List<FluidSelection> fluids = Enumerable.Range(0, descriptor.Streams.Count)
                .Select(stream => FluidCircuitResolver.CircuitOf(circuits, new StreamRef(node.Id, stream))?.Fluid
                                  ?? document.DefaultFluid)
                .ToList();
            try
            {
                CycleComponent component = descriptor.Create(new BuildContext(node, descriptor, fluids));
                byNode[node.Id] = component;
                descriptors[node.Id] = descriptor;
            }
            catch (ParameterException ex)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"{node.Name}: {ex.Message}", node.Id, ex.ParameterKey));
            }
            catch (ArgumentException ex)
            {
                // Backend constructors name the offending argument, which matches our parameter keys.
                string? key = descriptor.FindParameter(ex.ParamName ?? "")?.Key;
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"{node.Name}: {CleanMessage(ex)}", node.Id, key));
            }
            catch (Exception ex)
            {
                // E.g. CoolProp rejecting an unknown fluid name.
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"{node.Name}: {ex.Message}", node.Id));
            }
        }

        if (issues.Any(issue => issue.Severity == IssueSeverity.Error))
            return Failed(issues);

        foreach (ConnectionModel connection in document.Connections)
        {
            try
            {
                Connect(connection, byNode, descriptors);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, ex.Message, ConnectionId: connection.Id));
            }
        }

        foreach (NodeModel node in document.Nodes)
            ApplyGuesses(node, byNode[node.Id], descriptors[node.Id], issues);

        if (issues.Any(issue => issue.Severity == IssueSeverity.Error))
            return Failed(issues);

        return new BuildResult
        {
            Issues = issues,
            Components = document.Nodes.Select(node => byNode[node.Id]).ToList(),
            ComponentsByNode = byNode
        };
    }

    private static void Connect(ConnectionModel connection, Dictionary<string, CycleComponent> byNode,
        Dictionary<string, ComponentDescriptor> descriptors)
    {
        Port source = descriptors[connection.From.NodeId].GetPort(byNode[connection.From.NodeId], connection.From.PortKey);
        CycleComponent target = byNode[connection.To.NodeId];
        ComponentDescriptor targetDescriptor = descriptors[connection.To.NodeId];

        if (connection.Kind == ConnectionKind.Fluid)
        {
            source.ConnectTo(targetDescriptor.GetPort(target, connection.To.PortKey));
            return;
        }

        SignalPort input = targetDescriptor.GetSignalPort?.Invoke(target, connection.To.PortKey)
                           ?? throw new InvalidOperationException($"{target.Name} has no signal input '{connection.To.PortKey}'.");
        input.ConnectTo(source.SignalTapIn);
    }

    private static void ApplyGuesses(NodeModel node, CycleComponent component, ComponentDescriptor descriptor,
        List<ValidationIssue> issues)
    {
        foreach (GuessModel guess in node.Guesses)
        {
            try
            {
                if (guess.PortKey is null)
                    component.AddGuess(guess.Quantity, guess.Value);
                else
                    component.AddGuess(guess.Quantity, guess.Value, descriptor.GetPort(component, guess.PortKey));
            }
            catch (ArgumentException ex)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"{node.Name}: {CleanMessage(ex)}", node.Id,
                    PortKey: guess.PortKey));
            }
        }
    }

    private static void ValidateParameters(NodeModel node, ComponentDescriptor descriptor, List<ValidationIssue> issues)
    {
        foreach (ParameterDescriptor parameter in descriptor.Parameters.Where(p => p.Kind == ParameterKind.Coefficients))
        {
            if (!parameter.Active(node))
                continue;
            double[]? values = node.Arrays.TryGetValue(parameter.Key, out double[]? stored) ? stored : parameter.DefaultCoefficients;
            if (values is null)
            {
                if (!parameter.Optional)
                    issues.Add(Issue(node, parameter, $"{parameter.Label} needs {parameter.Length} coefficients."));
            }
            else if (values.Length != parameter.Length)
                issues.Add(Issue(node, parameter, $"{parameter.Label} needs {parameter.Length} coefficients, got {values.Length}."));
            else if (values.Any(v => !double.IsFinite(v)))
                issues.Add(Issue(node, parameter, $"{parameter.Label} coefficients must be finite numbers."));
        }

        foreach (ParameterDescriptor parameter in descriptor.Parameters.Where(p => p.Kind == ParameterKind.Number))
        {
            if (!parameter.Active(node))
                continue;

            double? value = node.Numbers.TryGetValue(parameter.Key, out double? stored) ? stored : parameter.DefaultNumber;
            if (value is null)
            {
                if (!parameter.Optional)
                    issues.Add(Issue(node, parameter, $"{parameter.Label} needs a value."));
                continue;
            }

            if (!double.IsFinite(value.Value))
                issues.Add(Issue(node, parameter, $"{parameter.Label} must be a finite number."));
            else if (parameter.Min is { } min && (value < min || (parameter.MinExclusive && value <= min)))
                issues.Add(Issue(node, parameter,
                    $"{parameter.Label} must be {(parameter.MinExclusive ? "above" : "at least")} {Format(min)} {parameter.Unit}."));
            else if (parameter.Max is { } max && value > max)
                issues.Add(Issue(node, parameter, $"{parameter.Label} must be at most {Format(max)} {parameter.Unit}."));
        }
    }

    private static ValidationIssue Issue(NodeModel node, ParameterDescriptor parameter, string message) =>
        new(IssueSeverity.Error, $"{node.Name}: {message}", node.Id, parameter.Key);

    private static string Format(double value) => value.ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>Removes the " (Parameter 'x')" suffix .NET adds to argument exception messages.</summary>
    private static string CleanMessage(ArgumentException ex)
    {
        string message = ex.Message;
        int index = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
        return index >= 0 ? message[..index] : message;
    }

    private static BuildResult Failed(List<ValidationIssue> issues) => new()
    {
        Issues = issues,
        Components = [],
        ComponentsByNode = new Dictionary<string, CycleComponent>()
    };
}
