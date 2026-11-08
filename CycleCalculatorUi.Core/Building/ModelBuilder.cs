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
            if (descriptor.Check is { } check)
                issues.AddRange(check(node));

            for (int i = 0; i < node.Guesses.Count; i++)
            {
                if (StartGuesses.Check(node.Guesses[i], descriptor) is { } problem)
                    issues.Add(new ValidationIssue(IssueSeverity.Error, $"{node.Name}: start guess {i + 1}: {problem}",
                        node.Id, StartGuesses.IssueKey(i)));
            }

            foreach (PortDescriptor port in descriptor.PortsFor(node).Where(port => port.Required(node)))
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
            else if (connection.Kind == ConnectionKind.Signal &&
                     document.FindNode(connection.To.NodeId) is { } target &&
                     Measurements.SourceOf(document, catalog, connection.To) is { } source &&
                     catalog.Find(target.TypeId)?.FindPort(target, connection.To.PortKey)?.CheckSource is { } check &&
                     check(target, connection.To.PortKey, source) is { } problem)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"{target.Name}: {problem}", target.Id,
                    PortKey: connection.To.PortKey, ConnectionId: connection.Id));
            }
        }

        if (FindSignalLoop(document, catalog) is { } loop)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error,
                $"The signal operators {string.Join(" → ", loop.Select(n => n.Name))} feed each other in a loop. " +
                "Remove one of their connections.", loop[0].Id));
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
        CycleComponent from = byNode[connection.From.NodeId];
        ComponentDescriptor fromDescriptor = descriptors[connection.From.NodeId];
        CycleComponent target = byNode[connection.To.NodeId];
        ComponentDescriptor targetDescriptor = descriptors[connection.To.NodeId];

        if (connection.Kind == ConnectionKind.Fluid)
        {
            fromDescriptor.GetPort(from, connection.From.PortKey)
                .ConnectTo(targetDescriptor.GetPort(target, connection.To.PortKey));
            return;
        }

        SignalPort input = targetDescriptor.GetSignalPort?.Invoke(target, connection.To.PortKey)
                           ?? throw new InvalidOperationException($"{target.Name} has no signal input '{connection.To.PortKey}'.");
        if (fromDescriptor.FindPort(connection.From.PortKey)?.Kind == PortKind.SignalOutput)
        {
            SignalOutput output = fromDescriptor.GetSignalOutput?.Invoke(from, connection.From.PortKey)
                                  ?? throw new InvalidOperationException($"{from.Name} has no signal output '{connection.From.PortKey}'.");
            input.ConnectTo(output);
        }
        else
        {
            input.ConnectTo(fromDescriptor.GetPort(from, connection.From.PortKey).SignalTapIn);
        }
    }

    /// <summary>
    /// Nodes whose signal outputs feed each other in a loop (first node repeated at the end), or null. The backend
    /// would only notice while evaluating.
    /// </summary>
    public static IReadOnlyList<NodeModel>? FindSignalLoop(SystemDocument document, ComponentCatalog catalog)
    {
        // Edge: node owning a signal output → node whose input it feeds.
        ILookup<string, string> feeds = document.Connections
            .Where(c => c.Kind == ConnectionKind.Signal &&
                        ConnectionRules.Describe(document, catalog, c.From)?.Kind == PortKind.SignalOutput)
            .ToLookup(c => c.From.NodeId, c => c.To.NodeId);

        Dictionary<string, int> state = []; // 1: on the current path, 2: done
        List<string> path = [];

        List<string>? Visit(string nodeId)
        {
            if (state.GetValueOrDefault(nodeId) == 2)
                return null;
            if (state.GetValueOrDefault(nodeId) == 1)
                return [.. path.Skip(path.IndexOf(nodeId)), nodeId];

            state[nodeId] = 1;
            path.Add(nodeId);
            foreach (string next in feeds[nodeId])
            {
                if (Visit(next) is { } loop)
                    return loop;
            }

            path.RemoveAt(path.Count - 1);
            state[nodeId] = 2;
            return null;
        }

        foreach (IGrouping<string, string> start in feeds)
        {
            if (Visit(start.Key) is { } loop)
                return loop.Select(document.FindNode).OfType<NodeModel>().ToList();
        }

        return null;
    }

    private static void ApplyGuesses(NodeModel node, CycleComponent component, ComponentDescriptor descriptor,
        List<ValidationIssue> issues)
    {
        for (int i = 0; i < node.Guesses.Count; i++)
        {
            GuessModel guess = node.Guesses[i];
            try
            {
                if (guess.PortKey is not null)
                    component.AddGuess(guess.Quantity, guess.Value, descriptor.GetPort(component, guess.PortKey));
                else if (guess.Stream is { } stream)
                    component.AddGuess(guess.Quantity, guess.Value, StreamSide(component, descriptor, stream));
                else
                    component.AddGuess(guess.Quantity, guess.Value);
            }
            catch (ArgumentException ex)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"{node.Name}: start guess {i + 1}: {CleanMessage(ex)}",
                    node.Id, StartGuesses.IssueKey(i), guess.PortKey));
            }
        }
    }

    /// <summary>The fluid ports of one stream of a component, as a backend side for scoped guesses.</summary>
    private static ComponentSide StreamSide(CycleComponent component, ComponentDescriptor descriptor, int stream)
    {
        Port[] ports = descriptor.Ports
            .Where(p => p.Kind == PortKind.Fluid && p.Stream == stream)
            .Select(p => descriptor.GetPort(component, p.Key))
            .ToArray();
        if (ports.Length == 0)
            throw new ArgumentException($"{component.Name} has no ports on stream {stream + 1} for a start guess.");
        string name = stream < descriptor.Streams.Count ? descriptor.Streams[stream] : $"Stream {stream + 1}";
        return new ComponentSide(name, ports);
    }

    private static void ValidateParameters(NodeModel node, ComponentDescriptor descriptor, List<ValidationIssue> issues)
    {
        List<ParameterInstance> instances = descriptor.ParameterInstances(node).ToList();
        foreach (ParameterInstance instance in instances.Where(i => i.Parameter.Kind == ParameterKind.Coefficients))
        {
            ParameterDescriptor parameter = instance.Parameter;
            string label = instance.FullLabel;
            double[]? values = node.Arrays.TryGetValue(instance.Key, out double[]? stored) ? stored : parameter.DefaultCoefficients;
            if (values is null)
            {
                if (!parameter.Optional)
                    issues.Add(Issue(node, instance, $"{label} needs {parameter.Length} coefficients."));
            }
            else if (values.Length != parameter.Length)
                issues.Add(Issue(node, instance, $"{label} needs {parameter.Length} coefficients, got {values.Length}."));
            else if (values.Any(v => !double.IsFinite(v)))
                issues.Add(Issue(node, instance, $"{label} coefficients must be finite numbers."));
        }

        foreach (ParameterInstance instance in instances.Where(i => i.Parameter.Kind == ParameterKind.Number))
        {
            ParameterDescriptor parameter = instance.Parameter;
            string label = instance.FullLabel;
            double? value = node.Numbers.TryGetValue(instance.Key, out double? stored) ? stored : parameter.DefaultNumber;
            if (value is null)
            {
                if (!parameter.Optional)
                    issues.Add(Issue(node, instance, $"{label} needs a value."));
                continue;
            }

            if (!double.IsFinite(value.Value))
                issues.Add(Issue(node, instance, $"{label} must be a finite number."));
            else if (parameter.Min is { } min && (value < min || (parameter.MinExclusive && value <= min)))
                issues.Add(Issue(node, instance,
                    $"{label} must be {(parameter.MinExclusive ? "above" : "at least")} {Format(min)} {parameter.Unit}."));
            else if (parameter.Max is { } max && value > max)
                issues.Add(Issue(node, instance, $"{label} must be at most {Format(max)} {parameter.Unit}."));
        }
    }

    private static ValidationIssue Issue(NodeModel node, ParameterInstance instance, string message) =>
        new(IssueSeverity.Error, $"{node.Name}: {message}", node.Id, instance.Key);

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
