using System.Collections.Concurrent;
using System.Globalization;
using CycleCalculator.CycleModel.Model;
using CycleCalculator.CycleModel.Model.En12900;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Catalog;

/// <summary>
/// Choosing EN 12900 compressor models from the <see cref="CompressorLibrary"/>. A chosen model is embedded whole in
/// the node (<see cref="NodeModel.Texts"/> under the model key, in the library file format), so documents stay
/// self-contained; the model name is kept to show where the data came from and to warn when it no longer matches the
/// library. The node's net frequency and the compressor's speed pick the variant that is used.
/// </summary>
public static class En12900Library
{
    /// <summary>Per-compressor choice of the model; the embedded model data is stored in Texts under the same key.</summary>
    public const string ModelKey = "model";

    public const string NetFrequencyKey = "netFrequency";

    public const string NetVoltageKey = "netVoltage";

    public const string Group = "compressors";

    public static readonly string[] StandardNetFrequencies = ["50", "60"];

    /// <summary>
    /// The CO₂ compressor of the backend's end-to-end tests: one polynomial set measured at 50 Hz that scales linearly,
    /// for any net frequency. Older files that left out polynomials used these (the editor's former defaults); the
    /// shipped ejector heat pump example embeds it.
    /// </summary>
    public static CompressorModel ExampleCompressor { get; } = new()
    {
        Name = "CO₂ compressor (example data)",
        File = "",
        DischargeVariable = En12900DischargeVariable.Pressure,
        NominalSuperheat = 10,
        MassFlowUnit = En12900MassFlowUnit.KilogramsPerHour,
        PowerUnit = En12900PowerUnit.Watts,
        Variants = [ExampleCompressorVariant(CompressorSpeed.Fixed), ExampleCompressorVariant(CompressorSpeed.Variable)]
    };

    private static CompressorVariant ExampleCompressorVariant(CompressorSpeed speed) => new()
    {
        Speed = speed,
        Scaling = En12900FrequencyScaling.Linear,
        Points =
        [
            new CompressorPoint(50,
            [
                2396.20919901144, 69.4448899794199, -5.00691806645477, 0.899339507043113, -0.00410373098918049,
                -0.00457899885315599, 0.00873555952226035, 0.00163967376135643, -0.000277241052561197,
                -0.00000000165694956689
            ],
            [
                -15179.3465346534, -464.76089108911, 754.703465346531, -4.01655742574256, 1.45824059405942,
                -3.04439702970294, 0.0102332673267327, -0.0566240792079209, 0.0384600297029703, 0.00535540990099
            ], null)
        ]
    };

    private static readonly ConcurrentDictionary<string, CompressorModel?> ParseCache = new();

    public static ParameterDescriptor ModelParameter(CompressorLibrary library) =>
        new(ModelKey, "Model", ParameterKind.Choice)
        {
            Group = Group,
            DefaultChoice = "",
            OptionsFor = (node, key, _, _) =>
            {
                List<ChoiceOption> options = library.Models.Select(model => new ChoiceOption(model.Name, model.Name)).ToList();
                if (node.Choices.GetValueOrDefault(key) is { Length: > 0 } stored)
                {
                    if (library.Find(stored) is null)
                        options.Add(new ChoiceOption(stored, $"{stored} (not in library)"));
                }
                else
                    options.Insert(0, new ChoiceOption("", library.Models.Count == 0 ? "No models in the library" : "Choose a model…"));
                return options;
            },
            OnChosen = (node, key, value) =>
            {
                if (library.Find(value) is { } model)
                    Apply(node, RepeatGroupDescriptor.Parse(key).Index, model);
            },
            Action = new ParameterAction("Reload from library",
                (node, key) => node.Choices.GetValueOrDefault(key) is { } name && library.Find(name) is { } model &&
                               node.Texts.GetValueOrDefault(key) != CompressorLibrary.Write(model),
                (node, key) =>
                {
                    if (node.Choices.GetValueOrDefault(key) is { } name && library.Find(name) is { } model)
                        Apply(node, RepeatGroupDescriptor.Parse(key).Index, model);
                }),
        };

    public static ParameterDescriptor NetFrequencyParameter() =>
        new(NetFrequencyKey, "Net frequency", ParameterKind.Choice)
        {
            DefaultChoice = "50",
            Options = [new ChoiceOption("50", "50 Hz"), new ChoiceOption("60", "60 Hz")],
            OptionsFor = (node, key, _, _) =>
            {
                IEnumerable<string> offered = Models(node).SelectMany(model => model.NetFrequencies)
                    .Select(f => f.ToString(CultureInfo.InvariantCulture));
                string stored = node.Choices.GetValueOrDefault(key) ?? "50";
                return StandardNetFrequencies.Concat(offered).Append(stored).Distinct()
                    .OrderBy(f => double.Parse(f, CultureInfo.InvariantCulture))
                    .Select(f => new ChoiceOption(f, $"{f} Hz")).ToList();
            },
        };

    public static ParameterDescriptor NetVoltageParameter() =>
        new(NetVoltageKey, "Net voltage", ParameterKind.Number)
        {
            Unit = "V", DefaultNumber = 400, Min = 0, MinExclusive = true,
        };

    /// <summary>The speeds compressor <paramref name="index"/>'s model offers at the node's net frequency (all when none).</summary>
    public static IReadOnlyList<CompressorSpeed> OfferedSpeeds(NodeModel node, int index)
    {
        if (Embedded(node, index) is not { } model)
            return [CompressorSpeed.Fixed, CompressorSpeed.Variable];
        double net = NetFrequency(node);
        return [.. Enum.GetValues<CompressorSpeed>().Where(speed => model.Variant(speed, net) is not null)];
    }

    /// <summary>The supply frequency chosen for the node, in Hz.</summary>
    public static double NetFrequency(NodeModel node) =>
        double.TryParse(node.Choices.GetValueOrDefault(NetFrequencyKey), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double frequency) && frequency > 0
            ? frequency
            : 50;

    /// <summary>The model embedded for compressor <paramref name="index"/>, or null when missing or unreadable.</summary>
    public static CompressorModel? Embedded(NodeModel node, int index) =>
        Embedded(node, RepeatGroupDescriptor.ItemKey(ModelKey, index));

    private static CompressorModel? Embedded(NodeModel node, string key) =>
        node.Texts.GetValueOrDefault(key) is { Length: > 0 } text ? Read(text) : null;

    /// <summary>Reads embedded model data; cached, since it is read on every validation.</summary>
    public static CompressorModel? Read(string text)
    {
        if (ParseCache.TryGetValue(text, out CompressorModel? cached))
            return cached;
        if (ParseCache.Count > 256)
            ParseCache.Clear();
        CompressorModel? model = CompressorLibrary.Parse(text, "", [], requireVoltage: false) is [{ } single] ? single : null;
        ParseCache[text] = model;
        return model;
    }

    private static IEnumerable<CompressorModel> Models(NodeModel node) =>
        Enumerable.Range(0, Count(node)).Select(index => Embedded(node, index)).OfType<CompressorModel>();

    public static CompressorSpeed Speed(NodeModel node, int index) =>
        node.Choices.GetValueOrDefault(RepeatGroupDescriptor.ItemKey(BuiltInComponents.SpeedControlKey, index)) == "variable"
            ? CompressorSpeed.Variable
            : CompressorSpeed.Fixed;

    /// <summary>
    /// Embeds <paramref name="model"/> as compressor <paramref name="index"/> of an EN 12900 node. When the node's net
    /// frequency or the compressor's speed has no data in the model, switches to ones that have; when it is the only
    /// compressor, also takes over the model's nominal voltage as net voltage.
    /// </summary>
    public static void Apply(NodeModel node, int index, CompressorModel model)
    {
        string key = RepeatGroupDescriptor.ItemKey(ModelKey, index);
        node.Choices[key] = model.Name;
        node.Texts[key] = CompressorLibrary.Write(model);

        bool single = Count(node) == 1;
        double net = NetFrequency(node);
        if (single && !model.Variants.Any(v => v.NetFrequency is null || v.NetFrequency == net) &&
            model.NetFrequencies.FirstOrDefault() is var offered and > 0)
        {
            net = offered;
            node.Choices[NetFrequencyKey] = offered.ToString(CultureInfo.InvariantCulture);
        }

        string speedKey = RepeatGroupDescriptor.ItemKey(BuiltInComponents.SpeedControlKey, index);
        CompressorSpeed speed = Speed(node, index);
        if (model.Variant(speed, net) is null)
        {
            CompressorSpeed other = speed == CompressorSpeed.Fixed ? CompressorSpeed.Variable : CompressorSpeed.Fixed;
            if (model.Variant(other, net) is not null)
            {
                speed = other;
                node.Choices[speedKey] = other == CompressorSpeed.Variable ? "variable" : "fixed";
            }
        }

        if (single && model.Variant(speed, net)?.NominalVoltage is { } voltage)
            node.Numbers[NetVoltageKey] = voltage;
    }

    /// <summary>
    /// The backend map of compressor <paramref name="index"/>: the variant for its speed and the net frequency, with
    /// coefficients converted to kg/s and kW.
    /// </summary>
    public static En12900CompressorMap BuildMap(NodeModel node, int index, double netVoltage, double? operatingFrequency)
    {
        string modelKey = RepeatGroupDescriptor.ItemKey(ModelKey, index);
        CompressorModel model = Embedded(node, index)
                                ?? throw new ParameterException(modelKey, "Choose a compressor model.");
        CompressorSpeed speed = Speed(node, index);
        double net = NetFrequency(node);
        CompressorVariant variant = model.Variant(speed, net)
                                    ?? throw new ParameterException(modelKey, MissingVariant(model, speed, net));

        En12900Polynomial Polynomial(double[] coefficients, double frequency) =>
            new(coefficients, model.NominalSuperheat, frequency);

        En12900FrequencyPoint[] points = variant.Points.Select(point => new En12900FrequencyPoint(point.Frequency,
                Polynomial(model.MassFlowIn(point.MassFlow, En12900MassFlowUnit.KilogramsPerSecond), point.Frequency),
                Polynomial(model.PowerIn(point.Power, En12900PowerUnit.Kilowatts), point.Frequency),
                point.Current is null ? null : Polynomial(point.Current, point.Frequency)))
            .ToArray();

        // Data for any net frequency with one set (e.g. from older files) runs at the set's own frequency, as before.
        double mapNet = variant.NetFrequency ?? (points.Length == 1 ? points[0].Frequency : net);
        return new En12900CompressorMap(points, variant.Scaling, mapNet, variant.NominalVoltage, netVoltage,
            speed == CompressorSpeed.Variable ? operatingFrequency : null, speed == CompressorSpeed.Variable);
    }

    /// <summary>The discharge variable shared by the node's models (the first one's when they differ).</summary>
    public static En12900DischargeVariable DischargeVariable(NodeModel node) =>
        Models(node).FirstOrDefault()?.DischargeVariable ?? En12900DischargeVariable.Pressure;

    private static string MissingVariant(CompressorModel model, CompressorSpeed speed, double net) =>
        $"{model.Name} has no {(speed == CompressorSpeed.Fixed ? "fixed" : "variable")}-speed data for " +
        $"{CompressorVariant.Format(net)} Hz net frequency. It has: {string.Join("; ", model.Variants.Select(v => v.Label))}.";

    /// <summary>One line about a model for the inspector.</summary>
    public static string Summary(CompressorModel model)
    {
        List<string> parts = [];
        if (model.Refrigerant is not null)
            parts.Add(model.Refrigerant);
        parts.Add(model.DischargeVariable == En12900DischargeVariable.Pressure ? "D = discharge pressure" : "D = discharge sat. temperature");
        parts.Add($"{CompressorVariant.Format(model.NominalSuperheat)} K superheat");
        if (!model.HasCurrent)
            parts.Add("no current data");
        return string.Join(" · ", parts) + ". Data: " + string.Join("; ", model.Variants.Select(v =>
            v.Label + (v.NominalVoltage is { } voltage ? $", {CompressorVariant.Format(voltage)} V" : ""))) + ".";
    }

    /// <summary>Problems with the compressors' models: missing data, mismatches and differences from the library.</summary>
    public static IEnumerable<ValidationIssue> Check(NodeModel node, CompressorLibrary library)
    {
        List<ValidationIssue> issues = [];
        bool showLibraryProblems = false;
        double net = NetFrequency(node);
        En12900DischargeVariable? discharge = null;
        bool? current = null;
        for (int index = 0; index < Count(node); index++)
        {
            string key = RepeatGroupDescriptor.ItemKey(ModelKey, index);
            string prefix = Count(node) > 1 ? $"{node.Name}: Compressor {index + 1}: " : $"{node.Name}: ";
            void Add(IssueSeverity severity, string message) =>
                issues.Add(new ValidationIssue(severity, prefix + message, node.Id, key));

            if (Embedded(node, index) is not { } model)
            {
                showLibraryProblems = true;
                Add(IssueSeverity.Error, node.Texts.ContainsKey(key)
                    ? "the stored compressor data cannot be read; choose the model again."
                    : "choose a compressor model.");
                continue;
            }

            CompressorSpeed speed = Speed(node, index);
            if (model.Variant(speed, net) is not { } variant)
                Add(IssueSeverity.Error, MissingVariant(model, speed, net));
            else if (current is null)
                current = variant.HasCurrent;
            else if (current != variant.HasCurrent)
                Add(IssueSeverity.Error, "give all compressors current data or none: some models have current polynomials, others not.");

            if (discharge is null)
                discharge = model.DischargeVariable;
            else if (discharge != model.DischargeVariable)
                Add(IssueSeverity.Error, $"{model.Name} uses a different discharge variable (D) than compressor 1; " +
                                         "compressors in parallel must share it.");

            if (model.Refrigerant is { } refrigerant && node.Fluids.FirstOrDefault()?.Fluid is { } fluid &&
                !SameFluid(refrigerant, fluid))
                Add(IssueSeverity.Warning, $"{model.Name} is for {refrigerant}, but the component uses {FluidOptions.FluidLabel(fluid)}.");

            // A model that is not in the library (e.g. in an example or an older file) just uses its stored data.
            if (library.Find(model.Name) is not { } libraryModel)
                continue;

            showLibraryProblems = true;
            if (CompressorLibrary.Write(libraryModel) != node.Texts[key])
                Add(IssueSeverity.Warning, $"the stored data differs from library model {model.Name} (the library file " +
                                           "changed). Use \"Reload from library\" to update it.");
        }

        if (showLibraryProblems)
        {
            foreach (string problem in library.Problems)
                issues.Add(new ValidationIssue(IssueSeverity.Warning, $"{node.Name}: compressor library: {problem}", node.Id));
        }

        return issues;
    }

    private static int Count(NodeModel node) => Math.Max(1, node.GroupCounts.GetValueOrDefault(Group, 1));

    private static readonly string[][] FluidAliases =
    [
        ["R744", "CO2", "CARBONDIOXIDE"],
        ["R717", "NH3", "AMMONIA"],
        ["R290", "PROPANE"],
        ["R600A", "ISOBUTANE"],
        ["R1270", "PROPYLENE"],
        ["R718", "WATER", "H2O"]
    ];

    /// <summary>Compares fluid names loosely: case, dashes and common refrigerant numbers (R744 = CarbonDioxide).</summary>
    private static bool SameFluid(string a, string b)
    {
        static string Normalize(string name)
        {
            string plain = new(name.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
            return FluidAliases.FirstOrDefault(aliases => aliases.Contains(plain))?[0] ?? plain;
        }

        return Normalize(a) == Normalize(b);
    }
}
