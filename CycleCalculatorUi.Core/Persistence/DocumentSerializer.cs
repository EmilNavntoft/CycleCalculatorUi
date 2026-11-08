using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CycleCalculator.CycleModel.Model;
using CycleCalculator.CycleModel.Model.En12900;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Persistence;

/// <summary>Reads and writes <see cref="SystemDocument"/>s as JSON (".cycle.json" files).</summary>
public static class DocumentSerializer
{
    public const string FileExtension = ".cycle.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions CompactOptions = new(Options) { WriteIndented = false };

    public static string Serialize(SystemDocument document) => JsonSerializer.Serialize(document, Options);

    /// <summary>Parses a document. Throws <see cref="InvalidDataException"/> for unreadable or newer files.</summary>
    public static SystemDocument Deserialize(string json)
    {
        SystemDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SystemDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file is not a valid cycle document.", ex);
        }

        if (document is null)
            throw new InvalidDataException("The file is empty.");
        if (document.Version > SystemDocument.CurrentVersion)
            throw new InvalidDataException(
                $"The file was saved by a newer version of the editor (format {document.Version}).");

        Upgrade(document);
        if (document.Version < 3)
            EmbedCompressorModels(document);
        document.Version = SystemDocument.CurrentVersion;
        return document;
    }

    /// <summary>Fills in settings that older files lack, so they solve as before.</summary>
    private static void Upgrade(SystemDocument document)
    {
        // Safe to repeat: no current document uses the old modes or keys.
        UpgradeControlModes(document);

        // EN 12900 compressors gained a per-compressor speed setting; before, any operating frequency applied.
        foreach (NodeModel node in document.Nodes.Where(n => n.TypeId == nameof(CycleCalculator.CycleModel.Model.En12900Compressor)))
        {
            int count = Math.Max(1, node.GroupCounts.GetValueOrDefault("compressors", 1));
            for (int index = 0; index < count; index++)
            {
                string speedKey = RepeatGroupDescriptor.ItemKey(BuiltInComponents.SpeedControlKey, index);
                if (node.Choices.ContainsKey(speedKey))
                    continue;
                bool hasFrequency = node.Numbers.TryGetValue(
                    RepeatGroupDescriptor.ItemKey("operatingFrequency", index), out double? frequency) && frequency.HasValue;
                node.Choices[speedKey] = hasFrequency ? "variable" : "fixed";
            }
        }
    }

    /// <summary>
    /// Up to format 2, EN 12900 compressors held their polynomials as editable parameters (per compressor, with node-wide
    /// units, discharge variable and superheat). They are now always a model embedded in the node; the stored polynomials
    /// become a model for any net frequency that scales linearly from its nominal frequency, so results stay the same.
    /// </summary>
    private static void EmbedCompressorModels(SystemDocument document)
    {
        string[] nodeKeys = ["massFlowUnit", "powerUnit", "dischargeVariable", "nominalSuperheat"];
        string[] itemKeys = ["massFlowPolynomial", "powerPolynomial", "currentPolynomial", "nominalFrequency"];
        // Polynomials left out were the editor's defaults, the example compressor's.
        CompressorPoint example = Examples.ExampleSystems.EjectorCompressor.Variants[0].Points[0];
        foreach (NodeModel node in document.Nodes.Where(n => n.TypeId == nameof(CycleCalculator.CycleModel.Model.En12900Compressor)))
        {
            En12900MassFlowUnit massFlowUnit = Enum.TryParse(node.Choices.GetValueOrDefault("massFlowUnit"), out En12900MassFlowUnit m)
                ? m
                : En12900MassFlowUnit.KilogramsPerHour;
            En12900PowerUnit powerUnit = Enum.TryParse(node.Choices.GetValueOrDefault("powerUnit"), out En12900PowerUnit p)
                ? p
                : En12900PowerUnit.Watts;
            En12900DischargeVariable discharge = Enum.TryParse(node.Choices.GetValueOrDefault("dischargeVariable"), out En12900DischargeVariable d)
                ? d
                : En12900DischargeVariable.Pressure;
            double superheat = node.Numbers.GetValueOrDefault("nominalSuperheat") ?? 10;

            int count = Math.Max(1, node.GroupCounts.GetValueOrDefault(En12900Library.Group, 1));
            for (int index = 0; index < count; index++)
            {
                string Key(string key) => RepeatGroupDescriptor.ItemKey(key, index);
                string modelKey = Key(En12900Library.ModelKey);
                if (node.Texts.ContainsKey(modelKey))
                    continue;

                double frequency = node.Numbers.GetValueOrDefault(Key("nominalFrequency")) ?? 50;
                if (index == 0)
                    node.Choices[En12900Library.NetFrequencyKey] = frequency == 60 ? "60" : "50";
                CompressorPoint point = new(frequency,
                    node.Arrays.GetValueOrDefault(Key("massFlowPolynomial")) ?? example.MassFlow,
                    node.Arrays.GetValueOrDefault(Key("powerPolynomial")) ?? example.Power,
                    node.Arrays.GetValueOrDefault(Key("currentPolynomial")));
                string name = node.Choices.GetValueOrDefault(modelKey) is { Length: > 0 } stored
                    ? stored
                    : count > 1 ? $"Custom compressor {index + 1} (from older file)" : "Custom compressor (from older file)";
                CompressorModel model = new()
                {
                    Name = name,
                    File = "",
                    DischargeVariable = discharge,
                    NominalSuperheat = superheat,
                    MassFlowUnit = massFlowUnit,
                    PowerUnit = powerUnit,
                    Variants =
                    [
                        new CompressorVariant { Speed = CompressorSpeed.Fixed, Scaling = En12900FrequencyScaling.Linear, Points = [point] },
                        new CompressorVariant { Speed = CompressorSpeed.Variable, Scaling = En12900FrequencyScaling.Linear, Points = [point] }
                    ]
                };
                node.Choices[modelKey] = name;
                node.Texts[modelKey] = CompressorLibrary.Write(model);
                foreach (string key in itemKeys)
                {
                    node.Arrays.Remove(Key(key));
                    node.Numbers.Remove(Key(key));
                }
            }

            foreach (string key in nodeKeys)
            {
                node.Choices.Remove(key);
                node.Numbers.Remove(key);
            }

            node.Numbers.TryAdd(En12900Library.NetVoltageKey, 400);
            node.Choices.TryAdd(En12900Library.NetFrequencyKey, "50");
            document.Study?.Variables.RemoveAll(v => v.NodeId == node.Id &&
                                                    (nodeKeys.Contains(v.ParameterKey) ||
                                                     itemKeys.Contains(RepeatGroupDescriptor.Parse(v.ParameterKey).ParameterKey)));
        }
    }

    /// <summary>
    /// Format 1 had one control mode per measured quantity (e.g. an expansion valve's "superheat" mode). Controllers
    /// now have one "signal" mode and choose the quantity with <see cref="BuiltInComponents.ControlSignalKey"/>.
    /// </summary>
    private static void UpgradeControlModes(SystemDocument document)
    {
        // The per-quantity error outputs of the setpoint controllers became one "Control error".
        string[] controllers =
        [
            nameof(CycleCalculator.CycleModel.Model.ExpansionValve),
            nameof(CycleCalculator.CycleModel.Model.ThreeWayValve),
            nameof(CycleCalculator.CycleModel.Model.FixedEfficiencyHeatExchanger)
        ];
        foreach (Studies.StudyOutput output in document.Study?.Outputs ?? [])
        {
            if (output.Kind == Studies.StudyOutputKind.Component &&
                output.Label is "Temperature error" or "Superheat error" or "Mass flow error" &&
                document.FindNode(output.NodeId) is { } owner && controllers.Contains(owner.TypeId))
                output.Label = "Control error";
        }

        foreach (NodeModel node in document.Nodes)
        {
            switch (node.TypeId)
            {
                case nameof(CycleCalculator.CycleModel.Model.ExpansionValve):
                    MoveModeToSignal(node, "temperature", "superheat", "massFlow");
                    break;
                case nameof(CycleCalculator.CycleModel.Model.ThreeWayValve):
                    MoveModeToSignal(node, "temperature", "massFlow");
                    break;
                case nameof(CycleCalculator.CycleModel.Model.FixedEfficiencyHeatExchanger):
                    MoveModeToSignal(node, "temperature");
                    RenameNumber(document, node, "targetTemperatureSetpoint", Measurements.Temperature.SetpointKey);
                    break;
                case nameof(CycleCalculator.CycleModel.Model.SimpleCompressor):
                case nameof(CycleCalculator.CycleModel.Model.En12900Compressor):
                    if (node.Choices.GetValueOrDefault(BuiltInComponents.ControlSignalKey) == "pumpEnvelope")
                        node.Choices[BuiltInComponents.ControlSignalKey] = Measurements.PumpEnvelopePosition.Key;
                    break;
            }
        }
    }

    private static void MoveModeToSignal(NodeModel node, params string[] quantities)
    {
        if (node.Choices.GetValueOrDefault("mode") is not { } mode || !quantities.Contains(mode))
            return;
        node.Choices["mode"] = "signal";
        node.Choices[BuiltInComponents.ControlSignalKey] = mode;
    }

    /// <summary>Renames a stored number and the study variables that vary it.</summary>
    private static void RenameNumber(SystemDocument document, NodeModel node, string oldKey, string newKey)
    {
        if (node.Numbers.Remove(oldKey, out double? value) && !node.Numbers.ContainsKey(newKey))
            node.Numbers[newKey] = value;
        foreach (Studies.StudyVariable variable in document.Study?.Variables ?? [])
        {
            if (variable.NodeId == node.Id && variable.ParameterKey == oldKey)
                variable.ParameterKey = newKey;
        }
    }

    public static SystemDocument Clone(SystemDocument document) =>
        JsonSerializer.Deserialize<SystemDocument>(JsonSerializer.Serialize(document, CompactOptions), CompactOptions)!;

    /// <summary>
    /// A hash of everything that affects the calculation. Layout (position, rotation, flip) is left out,
    /// so moving components does not make results stale. The energy roles and the study setup only change how
    /// results are summarised, so they are left out too.
    /// </summary>
    public static string Fingerprint(SystemDocument document)
    {
        SystemDocument copy = Clone(document);
        copy.Name = "";
        copy.Study = null;
        foreach (NodeModel node in copy.Nodes)
        {
            node.X = 0;
            node.Y = 0;
            node.Rotation = 0;
            node.FlipHorizontal = false;
            node.EnergyRole = default;
        }

        foreach (ConnectionModel connection in copy.Connections)
            connection.Points = null;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(copy, CompactOptions)));
        return Convert.ToHexString(hash, 0, 8);
    }
}
