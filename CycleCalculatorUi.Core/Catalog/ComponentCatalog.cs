using CycleCalculator.CycleModel.Model;
﻿using CycleCalculator.CycleModel.Model.BaseComponents;
using CycleCalculator.CycleModel.Model.Connectors;

namespace CycleCalculatorUi.Core.Catalog;

/// <summary>The component types available in the editor's library.</summary>
public sealed class ComponentCatalog
{
    private readonly Dictionary<string, ComponentDescriptor> _descriptors;

    public ComponentCatalog(IEnumerable<ComponentDescriptor> descriptors)
    {
        _descriptors = descriptors.ToDictionary(descriptor => descriptor.TypeId);
    }

    /// <summary>The catalog with all built-in component types.</summary>
    public static ComponentCatalog Default { get; } = new(BuiltInComponents.All());

    public IReadOnlyCollection<ComponentDescriptor> All => _descriptors.Values;

    public ComponentDescriptor? Find(string typeId) => _descriptors.GetValueOrDefault(typeId);

    public ComponentDescriptor Get(string typeId) =>
        Find(typeId) ?? throw new KeyNotFoundException($"Unknown component type '{typeId}'.");
}

/// <summary>Helpers that keep descriptor definitions short and type-safe.</summary>
internal static class Typed
{
    public static Func<CycleComponent, string, Port> Ports<T>(params (string Key, Func<T, Port> Get)[] ports)
        where T : CycleComponent
    {
        Dictionary<string, Func<T, Port>> map = ports.ToDictionary(p => p.Key, p => p.Get);
        return (component, key) => map.TryGetValue(key, out Func<T, Port>? get)
            ? get((T)component)
            : throw new KeyNotFoundException($"Component '{component.Name}' has no port '{key}'.");
    }

    public static Func<CycleComponent, string, SignalPort> SignalPorts<T>(
        params (string Key, Func<T, SignalPort> Get)[] ports) where T : CycleComponent
    {
        Dictionary<string, Func<T, SignalPort>> map = ports.ToDictionary(p => p.Key, p => p.Get);
        return (component, key) => map.TryGetValue(key, out Func<T, SignalPort>? get)
            ? get((T)component)
            : throw new KeyNotFoundException($"Component '{component.Name}' has no signal port '{key}'.");
    }

    public static Func<CycleComponent, IEnumerable<OutputValue>> Outputs<T>(Func<T, IEnumerable<OutputValue>> read)
        where T : CycleComponent => component => read((T)component);

    public static Func<CycleComponent, TemperatureProfile> Profile<T>(Func<T, TemperatureProfile> read)
        where T : CycleComponent => component => read((T)component);
}
