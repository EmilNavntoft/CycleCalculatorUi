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

    [Test]
    public void ZoomedView_IsUsedAndShowsFinerIsotherms()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        PhChart full = PhDiagramBuilder.Build(document, ComponentCatalog.Default, null).Single();
        Assert.That(full.IsZoomed, Is.False);

        PhView zoom = new(400, 480, 20, 40);
        PhChart zoomed = PhDiagramBuilder.Build(document, ComponentCatalog.Default, null,
            new Dictionary<string, PhView> { [CoolPropFluids.CarbonDioxide] = zoom }).Single();

        Assert.That(zoomed.View, Is.EqualTo(zoom));
        Assert.That(zoomed.IsZoomed, Is.True);
        Assert.That(zoomed.Isotherms, Has.Count.GreaterThanOrEqualTo(4));
        double fullStep = full.Isotherms[1].Temperature - full.Isotherms[0].Temperature;
        double zoomedStep = zoomed.Isotherms[1].Temperature - zoomed.Isotherms[0].Temperature;
        Assert.That(zoomedStep, Is.LessThan(fullStep));
        // The dome gets extra points inside the zoomed pressure range.
        Assert.That(zoomed.Saturation.Count(p => p.Pressure >= 20 && p.Pressure <= 40),
            Is.GreaterThan(full.Saturation.Count(p => p.Pressure >= 20 && p.Pressure <= 40)));
    }

    [Test]
    public void ZoomedView_IsKeptAboveTheTriplePointAndNotFarBeyondTheDiagram()
    {
        SystemDocument document = ExampleSystems.TwoStageWithParallelCompressor();
        PhChart chart = PhDiagramBuilder.Build(document, ComponentCatalog.Default, null,
            new Dictionary<string, PhView> { [CoolPropFluids.CarbonDioxide] = new(-1e6, 1e6, 1e-6, 1e6) }).Single();

        // CO2's triple point is at about 5.2 bar.
        Assert.That(chart.MinPressure, Is.GreaterThan(5.1));
        Assert.That(chart.MaxEnthalpy - chart.MinEnthalpy,
            Is.LessThanOrEqualTo((chart.DefaultView.MaxEnthalpy - chart.DefaultView.MinEnthalpy) * 4 + 1e-6));
        Assert.That(chart.Error, Is.Null);
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
