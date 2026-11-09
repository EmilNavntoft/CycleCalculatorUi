using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Examples;
using CycleCalculatorUi.Core.Persistence;
using CycleCalculatorUi.Core.Solving;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

// Temporary probe: solves saved cascade files and variations, prints convergence and UA exchanger water flows.
[TestFixture, Explicit]
public class SecondaryFlowProbe
{
    private readonly SolveService _service = new(ComponentCatalog.Default);

    private static IEnumerable<(string Label, SystemDocument Document)> Cases()
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        foreach (string file in Directory.GetFiles(downloads, "Cascade.cycle*.json").Order())
        {
            SystemDocument baseDoc = DocumentSerializer.Deserialize(File.ReadAllText(file));
            yield return (Path.GetFileName(file), baseDoc);
            foreach (double sinkOut in new[] { 50.0, 60.0, 70.0, 90.0 })
            foreach (double ua in new[] { 30.0, 80.0, 150.0 })
            {
                SystemDocument doc = DocumentSerializer.Clone(baseDoc);
                doc.Nodes.First(n => n.Name == "BC2").Numbers["temperature"] = sinkOut;
                doc.Nodes.First(n => n.Name == "HX2").Numbers["ua"] = ua;
                yield return ($"{Path.GetFileName(file)} sinkOut={sinkOut} uaHX2={ua}", doc);
            }
        }

        SystemDocument example = ShippedExamples.EjectorHeatPump();
        yield return ("EjectorHeatPump", example);
        foreach (double sinkOut in new[] { 50.0, 60.0, 70.0, 90.0 })
        foreach (double ua in new[] { 10.0, 30.0, 80.0 })
        {
            SystemDocument doc = DocumentSerializer.Clone(example);
            doc.Nodes.First(n => n.Name == "Water out (sink)").Numbers["temperature"] = sinkOut;
            doc.Nodes.First(n => n.Name == "Gas cooler").Numbers["ua"] = ua;
            yield return ($"EjectorHeatPump sinkOut={sinkOut} uaGC={ua}", doc);
        }
    }

    [Test]
    public async Task Probe()
    {
        int failures = 0, total = 0;
        string? only = Environment.GetEnvironmentVariable("PROBE_ONLY");
        foreach ((string label, SystemDocument document) in Cases())
        {
            if (only is not null && !only.Split('|').Contains(label)) continue;
            total++;
            SolveResult result = await _service.SolveAsync(document);
            string flows = string.Join(", ", document.Nodes
                .Where(n => n.TypeId == "FixedUaHeatExchanger")
                .Select(n => $"{n.Name} m2={result.Port(n.Id, "Inlet2")?.MassFlow:G4}"));
            if (!result.Success) failures++;
            TestContext.Out.WriteLine(
                $"PROBE {(result.Success ? "OK  " : "FAIL")} {label} [{result.Duration.TotalMilliseconds:F0} ms] {flows} {(result.Success ? "" : result.Message)}");
        }

        TestContext.Out.WriteLine($"PROBE SUMMARY {total - failures}/{total} converged");
    }
}
