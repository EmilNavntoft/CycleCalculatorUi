using CycleCalculatorUi.Core.Catalog;

namespace CycleCalculatorUi.Core.Document;

/// <summary>Builds documents in code, e.g. for examples and tests.</summary>
public sealed class DocumentBuilder(ComponentCatalog catalog, string name, FluidSelection fluid)
{
    private readonly SystemDocument _document = new() { Name = name, DefaultFluid = fluid };

    public NodeModel Add(string typeId, string nodeName, double x, double y, params (string Key, double? Value)[] numbers)
    {
        NodeModel node = catalog.Get(typeId).CreateNode(nodeName, x, y, fluid);
        foreach ((string key, double? value) in numbers)
            node.Numbers[key] = value;
        _document.Nodes.Add(node);
        return node;
    }

    public DocumentBuilder Choice(NodeModel node, string key, string value)
    {
        node.Choices[key] = value;
        return this;
    }

    public DocumentBuilder Connect(NodeModel from, string fromPort, NodeModel to, string toPort,
        ConnectionKind kind = ConnectionKind.Fluid)
    {
        _document.Connections.Add(new ConnectionModel
        {
            Kind = kind,
            From = new PortRef(from.Id, fromPort),
            To = new PortRef(to.Id, toPort)
        });
        return this;
    }

    public SystemDocument Build() => _document;
}
