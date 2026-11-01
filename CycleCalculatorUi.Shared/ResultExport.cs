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
        csv.AppendLine("Component,Port,Mass flow [kg/s],Pressure [bar],Temperature [°C],Enthalpy [kJ/kg]");
        foreach (NodeModel node in document.Nodes)
        {
            if (!result.Ports.TryGetValue(node.Id, out var ports) || catalog.Find(node.TypeId) is not { } descriptor)
                continue;
            foreach (PortDescriptor port in descriptor.Ports.Where(p => ports.ContainsKey(p.Key)))
            {
                PortResult value = ports[port.Key];
                csv.AppendLine(string.Join(',',
                    Quote(node.Name), Quote(port.Label),
                    Number(value.MassFlow), Number(value.Pressure), Number(value.Temperature), Number(value.Enthalpy)));
            }
        }

        return csv.ToString();
    }

    private static string Number(double value) => value.ToString("G10", CultureInfo.InvariantCulture);

    private static string Quote(string text) => '"' + text.Replace("\"", "\"\"") + '"';
}
