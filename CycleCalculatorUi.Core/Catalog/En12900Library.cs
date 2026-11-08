using CycleCalculator.CycleModel.Model.En12900;
using CycleCalculator.CycleModel.Model;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Catalog;

/// <summary>
/// Choosing EN 12900 compressor models from the <see cref="CompressorLibrary"/>. A chosen model is copied into the
/// node (polynomials, units, nominal values), so documents stay self-contained; the model name is kept to show
/// where the values came from and to warn when they no longer match the library.
/// </summary>
public static class En12900Library
{
    /// <summary>Per-compressor choice of the library model; empty means custom polynomials.</summary>
    public const string ModelKey = "model";

    public const string Custom = "";

    private const string Group = "compressors";

    public static ParameterDescriptor ModelParameter(CompressorLibrary library, string group) =>
        new(ModelKey, "Model", ParameterKind.Choice)
        {
            Group = group,
            DefaultChoice = Custom,
            Options = [new ChoiceOption(Custom, "Custom polynomials")],
            OptionsFor = (node, key, _, _) =>
            {
                List<ChoiceOption> options = [new(Custom, "Custom polynomials")];
                options.AddRange(library.Models.Select(model => new ChoiceOption(model.Name, model.Name)));
                if (node.Choices.GetValueOrDefault(key) is { Length: > 0 } stored && library.Find(stored) is null)
                    options.Add(new ChoiceOption(stored, $"{stored} (not in library)"));
                return options;
            },
            OnChosen = (node, key, value) =>
            {
                if (library.Find(value) is { } model)
                    Apply(node, RepeatGroupDescriptor.Parse(key).Index, model);
            },
            Help = library.Directory is null
                ? "No compressor library folder is set up."
                : $"Models from the .json files in {library.Directory} (format in {CompressorLibrary.ReadmeFileName} " +
                  "there). Choosing one fills in its polynomials, units and nominal values."
        };

    /// <summary>Copies <paramref name="model"/> into compressor <paramref name="index"/> of an EN 12900 node.</summary>
    public static void Apply(NodeModel node, int index, CompressorModel model)
    {
        node.Choices[RepeatGroupDescriptor.ItemKey(ModelKey, index)] = model.Name;

        // The units, discharge variable and superheat are shared by all compressors of the node, so they only follow
        // the model when it is the only compressor; otherwise the polynomials are converted to the node's units.
        if (Count(node) == 1)
        {
            node.Choices["massFlowUnit"] = model.MassFlowUnit.ToString();
            node.Choices["powerUnit"] = model.PowerUnit.ToString();
            node.Choices["dischargeVariable"] = model.DischargeVariable.ToString();
            node.Numbers["nominalSuperheat"] = model.NominalSuperheat;
        }

        node.Arrays[RepeatGroupDescriptor.ItemKey("massFlowPolynomial", index)] = model.MassFlowIn(MassFlowUnit(node));
        node.Arrays[RepeatGroupDescriptor.ItemKey("powerPolynomial", index)] = model.PowerIn(PowerUnit(node));
        node.Arrays[RepeatGroupDescriptor.ItemKey("currentPolynomial", index)] = model.Current?.ToArray();
        node.Numbers[RepeatGroupDescriptor.ItemKey("nominalFrequency", index)] = model.NominalFrequency;
    }

    /// <summary>Warnings for compressors whose library model is missing, changed or does not fit the node.</summary>
    public static IEnumerable<ValidationIssue> Check(NodeModel node, CompressorLibrary library)
    {
        List<ValidationIssue> issues = [];
        bool anyModel = false;
        for (int index = 0; index < Count(node); index++)
        {
            string key = RepeatGroupDescriptor.ItemKey(ModelKey, index);
            if (node.Choices.GetValueOrDefault(key) is not { Length: > 0 } name)
                continue;
            anyModel = true;

            string prefix = $"{node.Name}: Compressor {index + 1}: ";
            void Warn(string message) => issues.Add(new ValidationIssue(IssueSeverity.Warning, prefix + message, node.Id, key));

            if (library.Find(name) is not { } model)
            {
                Warn($"the model '{name}' is not in the compressor library; its stored polynomials are used.");
                continue;
            }

            if (model.DischargeVariable != DischargeVariable(node))
                Warn($"{model.Name} uses the discharge {(model.DischargeVariable == En12900DischargeVariable.Pressure ? "pressure" : "saturation temperature")} " +
                     "as polynomial variable D, but the component is set to the other one.");
            if (Math.Abs(model.NominalSuperheat - (node.Numbers.GetValueOrDefault("nominalSuperheat") ?? 10)) > 1e-9)
                Warn($"{model.Name} was measured at {model.NominalSuperheat:G6} K superheat, unlike the component's nominal superheat.");
            if (model.Refrigerant is { } refrigerant && node.Fluids.FirstOrDefault()?.Fluid is { } fluid &&
                !SameFluid(refrigerant, fluid))
                Warn($"{model.Name} is for {refrigerant}, but the component uses {FluidOptions.FluidLabel(fluid)}.");

            bool changed =
                !Same(node.Arrays.GetValueOrDefault(RepeatGroupDescriptor.ItemKey("massFlowPolynomial", index)), model.MassFlowIn(MassFlowUnit(node))) ||
                !Same(node.Arrays.GetValueOrDefault(RepeatGroupDescriptor.ItemKey("powerPolynomial", index)), model.PowerIn(PowerUnit(node))) ||
                !Same(node.Arrays.GetValueOrDefault(RepeatGroupDescriptor.ItemKey("currentPolynomial", index)), model.Current) ||
                !Same(node.Numbers.GetValueOrDefault(RepeatGroupDescriptor.ItemKey("nominalFrequency", index)), model.NominalFrequency);
            if (changed)
                Warn($"the polynomials or nominal frequency differ from library model {model.Name} (edited here, or the " +
                     "library file changed). Choose Custom and then the model again to reload them.");
        }

        if (anyModel)
        {
            foreach (string problem in library.Problems)
                issues.Add(new ValidationIssue(IssueSeverity.Warning, $"{node.Name}: compressor library: {problem}", node.Id));
        }

        return issues;
    }

    private static int Count(NodeModel node) => Math.Max(1, node.GroupCounts.GetValueOrDefault(Group, 1));

    private static En12900MassFlowUnit MassFlowUnit(NodeModel node) =>
        Enum.TryParse(node.Choices.GetValueOrDefault("massFlowUnit"), out En12900MassFlowUnit unit)
            ? unit
            : En12900MassFlowUnit.KilogramsPerHour;

    private static En12900PowerUnit PowerUnit(NodeModel node) =>
        Enum.TryParse(node.Choices.GetValueOrDefault("powerUnit"), out En12900PowerUnit unit) ? unit : En12900PowerUnit.Watts;

    private static En12900DischargeVariable DischargeVariable(NodeModel node) =>
        Enum.TryParse(node.Choices.GetValueOrDefault("dischargeVariable"), out En12900DischargeVariable variable)
            ? variable
            : En12900DischargeVariable.Pressure;

    private static bool Same(double[]? stored, double[]? expected) =>
        stored is null || expected is null
            ? stored is null && expected is null
            : stored.Length == expected.Length && stored.Zip(expected).All(p => Same(p.First, p.Second));

    private static bool Same(double? stored, double expected) =>
        stored is { } value && Math.Abs(value - expected) <= 1e-9 * Math.Max(1, Math.Abs(expected));

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
