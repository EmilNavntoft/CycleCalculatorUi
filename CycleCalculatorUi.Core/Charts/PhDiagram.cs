using System.Collections.Concurrent;
using CycleCalculator.CycleModel.Conversion;
using CycleCalculator.CycleModel.CoolProp;
using CycleCalculator.CycleModel.Fluids;
using CycleCalculator.CycleModel.Model.Connectors;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Solving;

namespace CycleCalculatorUi.Core.Charts;

/// <summary>A point on a p-h diagram: enthalpy in kJ/kg, pressure in bar.</summary>
public readonly record struct PhPoint(double Enthalpy, double Pressure);

/// <summary>A solved port state plotted on the diagram.</summary>
public sealed record PhState(string NodeId, string NodeName, string PortLabel, double Enthalpy, double Pressure, double Temperature);

/// <summary>A line of constant temperature (°C).</summary>
public sealed record PhIsotherm(double Temperature, IReadOnlyList<PhPoint> Points);

/// <summary>The visible axis ranges of a diagram: enthalpy in kJ/kg, pressure in bar.</summary>
public sealed record PhView(double MinEnthalpy, double MaxEnthalpy, double MinPressure, double MaxPressure);

/// <summary>Everything needed to draw the log p-h diagram of one fluid.</summary>
public sealed class PhChart
{
    public required string Fluid { get; init; }

    public required string Label { get; init; }

    /// <summary>Set when the fluid's properties could not be computed; the other lists are then empty.</summary>
    public string? Error { get; init; }

    /// <summary>The visible range; the curves are computed for it.</summary>
    public PhView View { get; init; } = new(0, 1, 1, 10);

    /// <summary>The range that shows the whole dome and all states.</summary>
    public PhView DefaultView { get; init; } = new(0, 1, 1, 10);

    public bool IsZoomed => View != DefaultView;

    public double MinPressure => View.MinPressure;

    public double MaxPressure => View.MaxPressure;

    public double MinEnthalpy => View.MinEnthalpy;

    public double MaxEnthalpy => View.MaxEnthalpy;

    /// <summary>Saturation curve: liquid line up to the critical point, then the vapour line back down.</summary>
    public IReadOnlyList<PhPoint> Saturation { get; init; } = [];

    public IReadOnlyList<PhIsotherm> Isotherms { get; init; } = [];

    public IReadOnlyList<PhState> States { get; init; } = [];

    /// <summary>Lines from each inlet to each outlet of the same stream of a component, i.e. the processes.</summary>
    public IReadOnlyList<(PhState From, PhState To)> Processes { get; init; } = [];
}

/// <summary>Builds log p-h diagrams for the fluids used in a document, with the solved states drawn on top.</summary>
public static class PhDiagramBuilder
{
    // Fluid curves only depend on the fluid, so they are computed once per process.
    private static readonly ConcurrentDictionary<string, FluidCurves> Curves = new();

    private sealed record FluidCurves(
        IReadOnlyList<PhPoint> Liquid,
        IReadOnlyList<PhPoint> Vapor,
        double CriticalPressure,
        double CriticalTemperature,
        double TriplePressure,
        double TripleTemperature);

    /// <summary>
    /// One chart per distinct fluid in the document. States are added when <paramref name="result"/> is a
    /// successful solve; pass null to get the bare diagrams. <paramref name="views"/> gives a zoomed range per fluid;
    /// fluids without one show their default range.
    /// </summary>
    public static IReadOnlyList<PhChart> Build(SystemDocument document, ComponentCatalog catalog, SolveResult? result,
        IReadOnlyDictionary<string, PhView>? views = null)
    {
        IReadOnlyList<FluidCircuit> circuits = FluidCircuitResolver.Resolve(document, catalog);
        List<PhChart> charts = [];
        foreach (IGrouping<string, FluidCircuit> fluid in circuits.GroupBy(c => c.Fluid.Fluid))
        {
            HashSet<StreamRef> streams = fluid.SelectMany(c => c.Members).ToHashSet();
            List<PhState> states = [];
            List<(PhState, PhState)> processes = [];
            if (result is { HasResults: true })
                CollectStates(document, catalog, result, streams, states, processes);
            charts.Add(BuildChart(fluid.Key, states, processes, views?.GetValueOrDefault(fluid.Key)));
        }

        return charts;
    }

    private static void CollectStates(SystemDocument document, ComponentCatalog catalog, SolveResult result,
        HashSet<StreamRef> streams, List<PhState> states, List<(PhState, PhState)> processes)
    {
        foreach (NodeModel node in document.Nodes)
        {
            if (catalog.Find(node.TypeId) is not { } descriptor || !result.Ports.TryGetValue(node.Id, out var ports))
                continue;
            foreach (IGrouping<int, PortDescriptor> stream in descriptor.Ports
                         .Where(p => p.Kind == PortKind.Fluid && ports.ContainsKey(p.Key))
                         .GroupBy(p => p.Stream))
            {
                if (!streams.Contains(new StreamRef(node.Id, stream.Key)))
                    continue;
                List<(PortDescriptor Port, PhState State)> portStates = stream
                    .Select(p => (p, State(node, p, ports[p.Key])))
                    .Where(s => double.IsFinite(s.Item2.Enthalpy) && s.Item2.Pressure > 0)
                    .ToList();
                states.AddRange(portStates.Select(s => s.State));
                foreach (var inlet in portStates.Where(s => s.Port.Direction == PortDirection.Inlet))
                foreach (var outlet in portStates.Where(s => s.Port.Direction == PortDirection.Outlet))
                    processes.Add((inlet.State, outlet.State));
            }
        }

        static PhState State(NodeModel node, PortDescriptor port, PortResult value) =>
            new(node.Id, node.Name, port.Label, value.Enthalpy, value.Pressure, value.Temperature);
    }

    private static PhChart BuildChart(string fluid, List<PhState> states, List<(PhState, PhState)> processes, PhView? requested)
    {
        string label = FluidOptions.FluidLabel(fluid);
        FluidCurves curves;
        try
        {
            curves = Curves.GetOrAdd(fluid, ComputeCurves);
        }
        catch (Exception ex)
        {
            return new PhChart { Fluid = fluid, Label = label, Error = $"Could not compute properties of {fluid}: {ex.Message}" };
        }

        FluidProperties properties = FluidProperties.For(CoolPropBackends.HEOS, fluid);
        double pc = curves.CriticalPressure;

        // Pressure range: the whole dome plus the states, on a log axis.
        double minP = Math.Max(curves.TriplePressure * 1.01, pc / 100);
        double maxP = pc * 1.4;
        if (states.Count > 0)
        {
            minP = Math.Max(curves.TriplePressure * 1.01, Math.Min(minP, states.Min(s => s.Pressure) / 2));
            maxP = Math.Max(maxP, states.Max(s => s.Pressure) * 1.4);
        }

        // Enthalpy range: the visible part of the dome plus the states, with room for superheated vapour.
        List<double> enthalpies = curves.Liquid.Concat(curves.Vapor)
            .Where(p => p.Pressure >= minP)
            .Select(p => p.Enthalpy)
            .Concat(states.Select(s => s.Enthalpy))
            .ToList();
        double minH = enthalpies.Min(), maxH = enthalpies.Max();
        double span = Math.Max(maxH - minH, 1);
        PhView defaultView = new(minH - span * 0.08, maxH + span * 0.3, minP, maxP);
        PhView view = requested is null ? defaultView : Clamp(requested, defaultView, curves);

        return new PhChart
        {
            Fluid = fluid,
            Label = label,
            View = view,
            DefaultView = defaultView,
            Saturation = Saturation(properties, curves, view, view != defaultView),
            Isotherms = Isotherms(properties, curves, view.MinPressure, view.MaxPressure, view.MinEnthalpy, view.MaxEnthalpy),
            States = states,
            Processes = processes
        };
    }

    /// <summary>Keeps a zoomed range within sensible limits: not below the triple point, not far beyond the default range.</summary>
    private static PhView Clamp(PhView view, PhView defaultView, FluidCurves curves)
    {
        double defaultSpan = defaultView.MaxEnthalpy - defaultView.MinEnthalpy;
        double hSpan = Math.Clamp(view.MaxEnthalpy - view.MinEnthalpy, defaultSpan * 1e-4, defaultSpan * 4);
        double hMid = (view.MinEnthalpy + view.MaxEnthalpy) / 2;
        hMid = Math.Clamp(hMid, defaultView.MinEnthalpy - defaultSpan, defaultView.MaxEnthalpy + defaultSpan);

        double floor = Math.Log(curves.TriplePressure * 1.01), ceiling = Math.Log(defaultView.MaxPressure * 20);
        double defaultDecades = Math.Log(defaultView.MaxPressure) - Math.Log(defaultView.MinPressure);
        double pSpan = Math.Clamp(Math.Log(view.MaxPressure / view.MinPressure), defaultDecades * 1e-4,
            Math.Min(defaultDecades * 4, ceiling - floor));
        double pLow = Math.Clamp(Math.Log(view.MinPressure), floor, ceiling - pSpan);

        PhView clamped = new(hMid - hSpan / 2, hMid + hSpan / 2, Math.Exp(pLow), Math.Exp(pLow + pSpan));

        // Return the request itself when nothing had to change, so it compares equal.
        static bool Close(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(a));
        return Close(clamped.MinEnthalpy, view.MinEnthalpy) && Close(clamped.MaxEnthalpy, view.MaxEnthalpy) &&
               Close(clamped.MinPressure, view.MinPressure) && Close(clamped.MaxPressure, view.MaxPressure)
            ? view
            : clamped;
    }

    /// <summary>The dome from the cached curves, refined with extra points inside a zoomed range so it stays smooth.</summary>
    private static List<PhPoint> Saturation(FluidProperties properties, FluidCurves curves, PhView view, bool refine)
    {
        List<PhPoint> liquid = [..curves.Liquid], vapor = [..curves.Vapor];
        double top = Math.Min(view.MaxPressure, curves.CriticalPressure * 0.9999);
        double bottom = Math.Max(view.MinPressure, curves.Liquid[0].Pressure);
        if (refine && bottom < top)
        {
            foreach (double p in LogSpace(bottom, top, 60))
            {
                try
                {
                    liquid.Add(new PhPoint(properties.Get(UnitType.Enthalpy, UnitType.Pressure, p, UnitType.Quality, 0), p));
                    vapor.Add(new PhPoint(properties.Get(UnitType.Enthalpy, UnitType.Pressure, p, UnitType.Quality, 1), p));
                }
                catch (Exception)
                {
                    // Same as the cached curve: skip pressures CoolProp cannot handle.
                }
            }

            liquid.Sort((a, b) => a.Pressure.CompareTo(b.Pressure));
            vapor.Sort((a, b) => a.Pressure.CompareTo(b.Pressure));
        }

        return [..liquid, ..Enumerable.Reverse(vapor)];
    }

    private static FluidCurves ComputeCurves(string fluid)
    {
        // HEOS is exact and fast enough for a few hundred points; tabular backends would build tables first.
        FluidProperties properties = FluidProperties.For(CoolPropBackends.HEOS, fluid);
        double pc = properties.CriticalPressure;
        double pLow = Math.Max(properties.TriplePointPressure * 1.001, pc / 1e4);

        // Log spacing over the whole range, plus extra points close to the critical point where the dome bends.
        IEnumerable<double> pressures = LogSpace(pLow, pc * 0.9, 50)
            .Concat(Enumerable.Range(0, 25).Select(i => pc * (0.9 + 0.0999 * (1 - Math.Pow(1 - i / 24.0, 2)))));

        List<PhPoint> liquid = [], vapor = [];
        foreach (double p in pressures)
        {
            try
            {
                double hl = properties.Get(UnitType.Enthalpy, UnitType.Pressure, p, UnitType.Quality, 0);
                double hv = properties.Get(UnitType.Enthalpy, UnitType.Pressure, p, UnitType.Quality, 1);
                liquid.Add(new PhPoint(hl, p));
                vapor.Add(new PhPoint(hv, p));
            }
            catch (Exception)
            {
                // Some pressures fail close to the triple or critical point; the curve simply skips them.
            }
        }

        if (liquid.Count < 2)
            throw new InvalidOperationException("no saturation states could be computed.");

        return new FluidCurves(liquid, vapor, pc, properties.CriticalTemperature,
            properties.TriplePointPressure, properties.TriplePointTemperature);
    }

    private static List<PhIsotherm> Isotherms(FluidProperties properties, FluidCurves curves,
        double minP, double maxP, double minH, double maxH)
    {
        // The coldest visible temperature is on the left edge and the hottest on the right edge.
        List<double> left = EdgeTemperatures(properties, minH, minP, maxP);
        List<double> right = EdgeTemperatures(properties, maxH, minP, maxP);
        if (right.Count == 0)
            return [];
        // Left of the coldest liquid the fluid would be solid; start at the triple point then.
        double tMin = Math.Max(curves.TripleTemperature + 1, left.Count > 0 ? left.Min() : double.MinValue);
        double tMax = right.Max();
        if (tMax <= tMin)
            return [];

        double step = NiceStep((tMax - tMin) / 8);
        List<PhIsotherm> isotherms = [];
        for (double t = Math.Ceiling(tMin / step) * step; t <= tMax; t += step)
        {
            List<PhPoint> points = Isotherm(properties, curves, t, minP, maxP);
            if (points.Count >= 2 && points.Any(p => p.Enthalpy >= minH && p.Enthalpy <= maxH))
                isotherms.Add(new PhIsotherm(Math.Round(t, 6), points));
        }

        return isotherms;
    }

    private static List<double> EdgeTemperatures(FluidProperties properties, double h, double minP, double maxP)
    {
        List<double> temperatures = [];
        foreach (double p in LogSpace(minP, maxP, 7))
        {
            try
            {
                temperatures.Add(properties.Get(UnitType.Temperature, UnitType.Enthalpy, h, UnitType.Pressure, p));
            }
            catch (Exception)
            {
                // Outside the valid range, e.g. below the triple point.
            }
        }

        return temperatures;
    }

    /// <summary>Points from high to low pressure. Below the critical temperature the line crosses the dome at the saturation pressure.</summary>
    private static List<PhPoint> Isotherm(FluidProperties properties, FluidCurves curves, double t, double minP, double maxP)
    {
        double? pSat = null;
        double hl = 0, hv = 0;
        if (t < curves.CriticalTemperature - 0.01)
        {
            try
            {
                pSat = properties.Get(UnitType.Pressure, UnitType.Temperature, t, UnitType.Quality, 0);
                hl = properties.Get(UnitType.Enthalpy, UnitType.Temperature, t, UnitType.Quality, 0);
                hv = properties.Get(UnitType.Enthalpy, UnitType.Temperature, t, UnitType.Quality, 1);
            }
            catch (Exception)
            {
                pSat = null;
            }
        }

        List<PhPoint> points = [];
        bool crossed = false;
        foreach (double p in LogSpace(maxP, minP, 60))
        {
            if (pSat is { } ps && !crossed && p < ps)
            {
                // Horizontal through the two-phase region.
                points.Add(new PhPoint(hl, ps));
                points.Add(new PhPoint(hv, ps));
                crossed = true;
            }

            // Too close to saturation, CoolProp may return the wrong phase.
            if (pSat is { } s && Math.Abs(p / s - 1) < 0.002)
                continue;
            try
            {
                points.Add(new PhPoint(properties.Get(UnitType.Enthalpy, UnitType.Pressure, p, UnitType.Temperature, t), p));
            }
            catch (Exception)
            {
                // Outside the fluid's valid range (e.g. solid); skip.
            }
        }

        return points;
    }

    private static IEnumerable<double> LogSpace(double from, double to, int count)
    {
        double a = Math.Log(from), b = Math.Log(to);
        for (int i = 0; i < count; i++)
            yield return Math.Exp(a + (b - a) * i / (count - 1));
    }

    /// <summary>The nearest 1, 2 or 5 times a power of ten.</summary>
    public static double NiceStep(double rough)
    {
        if (!(rough > 0))
            return 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double fraction = rough / magnitude;
        return magnitude * (fraction < 1.5 ? 1 : fraction < 3.5 ? 2 : fraction < 7.5 ? 5 : 10);
    }
}
