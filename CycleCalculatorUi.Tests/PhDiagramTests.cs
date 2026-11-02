using CycleCalculator.CycleModel.Fluids;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Charts;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Examples;
using CycleCalculatorUi.Core.Solving;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

[TestFixture]
public class PhDiagramTests
{
    [Test]
    public void WithoutResult_DrawsDomeAndIsothermsButNoStates()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        IReadOnlyList<PhChart> charts = PhDiagramBuilder.Build(document, ComponentCatalog.Default, null);

        PhChart chart = charts.Single();
        Assert.That(chart.Error, Is.Null);
        Assert.That(chart.Fluid, Is.EqualTo(CoolPropFluids.CarbonDioxide));
        Assert.That(chart.Saturation, Has.Count.GreaterThan(50));
        // CO2's critical point is at about 73.8 bar.
        Assert.That(chart.Saturation.Max(p => p.Pressure), Is.EqualTo(73.8).Within(0.5));
        Assert.That(chart.Isotherms, Has.Count.GreaterThanOrEqualTo(4));
        Assert.That(chart.States, Is.Empty);
    }

    [Test]
    public async Task WithResult_PlotsEveryPortStateInsideTheAxes()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        SolveResult result = await new SolveService(ComponentCatalog.Default).SolveAsync(document);
        Assert.That(result.Success, Is.True, result.Message);

        PhChart chart = PhDiagramBuilder.Build(document, ComponentCatalog.Default, result).Single();
        Assert.That(chart.States, Is.Not.Empty);
        Assert.That(chart.Processes, Is.Not.Empty);
        Assert.That(chart.States.All(s => s.Pressure >= chart.MinPressure && s.Pressure <= chart.MaxPressure), Is.True);
        Assert.That(chart.States.All(s => s.Enthalpy >= chart.MinEnthalpy && s.Enthalpy <= chart.MaxEnthalpy), Is.True);
    }

    [TestCase(CoolPropFluids.Water)]
    [TestCase("R290")]
    [TestCase(CoolPropFluids.R134a)]
    public void OtherFluids_DrawDomeAndIsotherms(string fluid)
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        foreach (NodeModel node in document.Nodes)
            for (int i = 0; i < node.Fluids.Count; i++)
                node.Fluids[i] = new FluidSelection(fluid, CoolPropBackends.HEOS);

        PhChart chart = PhDiagramBuilder.Build(document, ComponentCatalog.Default, null).Single();
        Assert.That(chart.Error, Is.Null);
        Assert.That(chart.Saturation, Has.Count.GreaterThan(50));
        Assert.That(chart.Isotherms, Has.Count.GreaterThanOrEqualTo(4));
    }

    [Test]
    public void UnknownFluid_ReportsErrorInsteadOfThrowing()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        foreach (NodeModel node in document.Nodes)
            for (int i = 0; i < node.Fluids.Count; i++)
                node.Fluids[i] = new FluidSelection("NotAFluid", CoolPropBackends.HEOS);

        PhChart chart = PhDiagramBuilder.Build(document, ComponentCatalog.Default, null).Single();
        Assert.That(chart.Error, Is.Not.Null);
    }
}
