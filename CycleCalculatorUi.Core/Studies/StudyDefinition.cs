using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Studies;

public enum StudySpacing
{
    Linear,

    /// <summary>Equal ratios between steps; start and end must be positive.</summary>
    Logarithmic
}

/// <summary>A numeric parameter varied from <see cref="Start"/> to <see cref="End"/> in <see cref="Steps"/> values.</summary>
public sealed class StudyVariable
{
    public string NodeId { get; set; } = "";

    public string ParameterKey { get; set; } = "";

    public double Start { get; set; }

    public double End { get; set; }

    public int Steps { get; set; } = 5;

    public StudySpacing Spacing { get; set; } = StudySpacing.Linear;

    /// <summary>The values of this variable, from start to end.</summary>
    public IReadOnlyList<double> Values()
    {
        if (Steps <= 1)
            return [Start];
        bool log = Spacing == StudySpacing.Logarithmic && Start > 0 && End > 0;
        return Enumerable.Range(0, Steps).Select(i =>
        {
            double t = (double)i / (Steps - 1);
            return log ? Start * Math.Pow(End / Start, t) : Start + (End - Start) * t;
        }).ToList();
    }
}

public enum StudyOutputKind
{
    /// <summary>A solved port state (<see cref="StudyOutput.Quantity"/> at <see cref="StudyOutput.PortKey"/>).</summary>
    Port,

    /// <summary>A component output such as a valve's opening, matched by <see cref="StudyOutput.Label"/>.</summary>
    Component
}

public enum PortQuantity
{
    MassFlow,
    Pressure,
    Temperature,
    Enthalpy
}

/// <summary>An extra value recorded for every point of a study, besides the energy totals.</summary>
public sealed class StudyOutput
{
    public StudyOutputKind Kind { get; set; }

    public string NodeId { get; set; } = "";

    public string? PortKey { get; set; }

    public PortQuantity Quantity { get; set; }

    public string? Label { get; set; }

    /// <summary>Unit of a component output, remembered when it was picked.</summary>
    public string? Unit { get; set; }

    public static string QuantityUnit(PortQuantity quantity) => quantity switch
    {
        PortQuantity.MassFlow => "kg/s",
        PortQuantity.Pressure => "bar",
        PortQuantity.Temperature => "°C",
        _ => "kJ/kg"
    };

    public static string QuantitySymbol(PortQuantity quantity) => quantity switch
    {
        PortQuantity.MassFlow => "ṁ",
        PortQuantity.Pressure => "p",
        PortQuantity.Temperature => "T",
        _ => "h"
    };
}

/// <summary>Which parameters a parametric study varies and which results it records.</summary>
public sealed class StudyDefinition
{
    /// <summary>Largest grid a study may have.</summary>
    public const int MaxPoints = 5000;

    /// <summary>Above this many points the UI asks for confirmation.</summary>
    public const int LargeStudy = 200;

    public List<StudyVariable> Variables { get; set; } = [];

    public List<StudyOutput> Outputs { get; set; } = [];

    /// <summary>Start each calculation from the previous point's solution.</summary>
    public bool WarmStart { get; set; } = true;

    /// <summary>Number of calculations: every combination of the variables' values.</summary>
    public long PointCount() => Variables.Count == 0 ? 0 : Variables.Aggregate(1L, (n, v) => n * Math.Max(1, v.Steps));

    /// <summary>All combinations, the first variable varying slowest.</summary>
    public IReadOnlyList<double[]> Grid()
    {
        List<double[]> points = [[]];
        foreach (StudyVariable variable in Variables)
        {
            IReadOnlyList<double> values = variable.Values();
            points = points.SelectMany(prefix => values.Select(value => (double[])[.. prefix, value])).ToList();
        }

        return Variables.Count == 0 ? [] : points;
    }

    /// <summary>Problems that prevent the study from running, as messages for the user.</summary>
    public IReadOnlyList<string> Validate(SystemDocument document, ComponentCatalog catalog)
    {
        List<string> problems = [];
        if (Variables.Count == 0)
            problems.Add("Add at least one parameter to vary.");
        foreach (StudyVariable variable in Variables)
        {
            string name = Describe(document, catalog, variable);
            if (document.FindNode(variable.NodeId) is not { } node ||
                catalog.Find(node.TypeId)?.FindParameter(variable.ParameterKey) is not { Kind: ParameterKind.Number } parameter)
            {
                problems.Add($"{name}: the parameter no longer exists.");
                continue;
            }

            if (!parameter.ActiveForKey(node, variable.ParameterKey))
                problems.Add($"{name}: the parameter is not used in the component's current mode.");
            if (variable.Steps < 1)
                problems.Add($"{name}: needs at least one step.");
            if (!double.IsFinite(variable.Start) || !double.IsFinite(variable.End))
                problems.Add($"{name}: start and end must be numbers.");
            if (variable.Spacing == StudySpacing.Logarithmic && (variable.Start <= 0 || variable.End <= 0))
                problems.Add($"{name}: logarithmic steps need a positive start and end.");
        }

        if (PointCount() > MaxPoints)
            problems.Add($"The study has {PointCount():N0} combinations; at most {MaxPoints:N0} are allowed. Use fewer steps.");
        if (Variables.GroupBy(v => (v.NodeId, v.ParameterKey)).Any(g => g.Count() > 1))
            problems.Add("The same parameter is varied twice.");
        return problems;
    }

    /// <summary>"Comp1 · Discharge pressure" for display.</summary>
    public static string Describe(SystemDocument document, ComponentCatalog catalog, StudyVariable variable)
    {
        NodeModel? node = document.FindNode(variable.NodeId);
        string? label = node is null ? null : catalog.Find(node.TypeId)?.FindParameter(variable.ParameterKey)?.Label;
        return $"{node?.Name ?? "?"} · {label ?? variable.ParameterKey}";
    }

    public static string Unit(SystemDocument document, ComponentCatalog catalog, StudyVariable variable) =>
        document.FindNode(variable.NodeId) is { } node
            ? catalog.Find(node.TypeId)?.FindParameter(variable.ParameterKey)?.Unit ?? ""
            : "";

    public static string Describe(SystemDocument document, ComponentCatalog catalog, StudyOutput output)
    {
        NodeModel? node = document.FindNode(output.NodeId);
        string name = node?.Name ?? "?";
        if (output.Kind == StudyOutputKind.Component)
            return $"{name} · {output.Label}";
        string? port = node is null ? null : catalog.Find(node.TypeId)?.FindPort(output.PortKey ?? "")?.Label;
        return $"{name} · {port ?? output.PortKey} · {StudyOutput.QuantitySymbol(output.Quantity)}";
    }
}
