using System.Globalization;
using System.Text;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Studies;

namespace CycleCalculatorUi.Shared;

/// <summary>A result column of a study: an energy total, a COP or one of the picked outputs.</summary>
public sealed record StudyMetric(string Key, string Label, string Unit, Func<StudyPoint, double?> Read);

public static class StudyMetrics
{
    /// <summary>The totals every study shows, followed by the study's own outputs.</summary>
    public static IReadOnlyList<StudyMetric> For(StudyResult result, ComponentCatalog catalog)
    {
        List<StudyMetric> metrics =
        [
            new("heatIn", "Heat input", "kW", p => p.Energy?.HeatIn),
            new("heatOut", "Heat output", "kW", p => p.Energy?.HeatOut),
            new("powerIn", "Power input", "kW", p => p.Energy?.PowerIn),
            new("copHeating", "COP heating", "-", p => p.Energy?.CopHeating),
            new("copCooling", "COP cooling", "-", p => p.Energy?.CopCooling)
        ];
        for (int i = 0; i < result.Definition.Outputs.Count; i++)
        {
            int index = i;
            StudyOutput output = result.Definition.Outputs[i];
            metrics.Add(new StudyMetric($"output{i}", StudyDefinition.Describe(result.Document, catalog, output),
                OutputUnit(result, output), p => index < p.Outputs.Count ? p.Outputs[index] : null));
        }

        return metrics;
    }

    private static string OutputUnit(StudyResult result, StudyOutput output) =>
        output.Kind == StudyOutputKind.Port ? StudyOutput.QuantityUnit(output.Quantity) : output.Unit ?? "";
}

public static class StudyExport
{
    /// <summary>One row per study point with '.' decimals: parameter values, totals, outputs and status.</summary>
    public static string ToCsv(StudyResult result, ComponentCatalog catalog)
    {
        IReadOnlyList<StudyMetric> metrics = StudyMetrics.For(result, catalog);
        StringBuilder csv = new();
        IEnumerable<string> header = result.Definition.Variables
            .Select(v => $"{StudyDefinition.Describe(result.Document, catalog, v)} [{StudyDefinition.Unit(result.Document, catalog, v)}]")
            .Concat(metrics.Select(m => $"{m.Label} [{m.Unit}]"))
            .Append("Status");
        csv.AppendLine(string.Join(',', header.Select(Quote)));
        foreach (StudyPoint point in result.Points)
        {
            IEnumerable<string> cells = point.Values.Select(v => Number(v))
                .Concat(metrics.Select(m => Number(m.Read(point))))
                .Append(Quote(point.Success ? "ok" : point.Message));
            csv.AppendLine(string.Join(',', cells));
        }

        return csv.ToString();
    }

    private static string Number(double? value) => value?.ToString("G10", CultureInfo.InvariantCulture) ?? "";

    private static string Quote(string text) => '"' + text.Replace("\"", "\"\"") + '"';
}
