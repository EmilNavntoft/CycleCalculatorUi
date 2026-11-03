using System.Globalization;
using System.Text;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Solving;

namespace CycleCalculatorUi.Shared;

public static class ResultExport
{
    /// <summary>All port states as CSV with '.' decimals, one row per port.</summary>
    public static string ToCsv(SystemDocument document, ComponentCatalog catalog, SolveResult result)
    {
        StringBuilder csv = new();
        csv.AppendLine("Component,Port,Mass flow in design direction [kg/s],Pressure [bar],Temperature [°C],Enthalpy [kJ/kg]");
        foreach (NodeModel node in document.Nodes)
        {
            if (!result.Ports.TryGetValue(node.Id, out var ports) || catalog.Find(node.TypeId) is not { } descriptor)
                continue;
            foreach (PortDescriptor port in descriptor.Ports.Where(p => ports.ContainsKey(p.Key)))
            {
                PortResult value = ports[port.Key];
                csv.AppendLine(string.Join(',',
                    Quote(node.Name), Quote(port.Label),
                    Number(FlowDirection.Of(document, catalog, new PortRef(node.Id, port.Key), value).MassFlow), Number(value.Pressure), Number(value.Temperature), Number(value.Enthalpy)));
            }
        }

        EnergySummary energy = EnergyBalance.Compute(document, catalog, result);
        csv.AppendLine();
        csv.AppendLine("Total,Value,Unit");
        csv.AppendLine($"Heat input,{Number(energy.HeatIn)},kW");
        csv.AppendLine($"Heat output,{Number(energy.HeatOut)},kW");
        csv.AppendLine($"Power input,{Number(energy.PowerIn)},kW");
        if (energy.CopHeating is { } copHeating)
            csv.AppendLine($"COP heating,{Number(copHeating)},-");
        if (energy.CopCooling is { } copCooling)
            csv.AppendLine($"COP cooling,{Number(copCooling)},-");
        return csv.ToString();
    }

    private static string Number(double value) => value.ToString("G10", CultureInfo.InvariantCulture);

    private static string Quote(string text) => '"' + text.Replace("\"", "\"\"") + '"';
}
