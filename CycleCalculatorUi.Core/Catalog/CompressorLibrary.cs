using System.Globalization;
using System.Text;
using System.Text.Json;
using CycleCalculator.CycleModel.Model;
using CycleCalculator.CycleModel.Model.En12900;

namespace CycleCalculatorUi.Core.Catalog;

public enum CompressorSpeed
{
    Fixed,
    Variable
}

/// <summary>The EN 12900 polynomials of a compressor measured at one frequency, in the model's units.</summary>
public sealed record CompressorPoint(double Frequency, double[] MassFlow, double[] Power, double[]? Current);

/// <summary>
/// The polynomials of a compressor for one speed type and net frequency, e.g. "variable speed, 50 Hz net".
/// </summary>
public sealed record CompressorVariant
{
    public required CompressorSpeed Speed { get; init; }

    /// <summary>Supply frequency in Hz the data is for; null when it applies to any net frequency.</summary>
    public double? NetFrequency { get; init; }

    /// <summary>Voltage in V the current polynomials were measured at; null when not known.</summary>
    public double? NominalVoltage { get; init; }

    /// <summary>How a variable-speed compressor gets its performance between and beyond the measured frequencies.</summary>
    public required En12900FrequencyScaling Scaling { get; init; }

    /// <summary>The polynomial sets by ascending frequency; one for fixed speed and linear scaling.</summary>
    public required IReadOnlyList<CompressorPoint> Points { get; init; }

    public bool HasCurrent => Points[0].Current is not null;

    public bool Matches(CompressorSpeed speed, double netFrequency) =>
        Speed == speed && (NetFrequency is null || NetFrequency == netFrequency);

    /// <summary>E.g. "variable speed, 50 Hz (30–70 Hz, interpolated)".</summary>
    public string Label
    {
        get
        {
            string text = (Speed == CompressorSpeed.Fixed ? "fixed speed" : "variable speed") +
                          (NetFrequency is { } net ? $", {Format(net)} Hz" : "");
            if (Speed == CompressorSpeed.Variable)
            {
                text += Scaling == En12900FrequencyScaling.Linear
                    ? $" (linear from {Format(Points[0].Frequency)} Hz)"
                    : $" ({Format(Points[0].Frequency)}–{Format(Points[^1].Frequency)} Hz, interpolated)";
            }

            return text;
        }
    }

    internal static string Format(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}

/// <summary>One EN 12900 compressor model from the compressor library folder.</summary>
public sealed record CompressorModel
{
    /// <summary>Unique name shown in the editor: "Manufacturer Model" when a manufacturer is given.</summary>
    public required string Name { get; init; }

    /// <summary>File the model was read from, relative to the library folder.</summary>
    public required string File { get; init; }

    /// <summary>CoolProp fluid name the polynomials were measured with, if given.</summary>
    public string? Refrigerant { get; init; }

    public required En12900DischargeVariable DischargeVariable { get; init; }

    public required double NominalSuperheat { get; init; }

    public En12900MassFlowUnit MassFlowUnit { get; init; } = En12900MassFlowUnit.KilogramsPerHour;

    public En12900PowerUnit PowerUnit { get; init; } = En12900PowerUnit.Watts;

    /// <summary>The data per speed type and net frequency; at least one.</summary>
    public required IReadOnlyList<CompressorVariant> Variants { get; init; }

    /// <summary>The variant for a speed type and net frequency: one for exactly that frequency, else one for any.</summary>
    public CompressorVariant? Variant(CompressorSpeed speed, double netFrequency) =>
        Variants.FirstOrDefault(v => v.Speed == speed && v.NetFrequency == netFrequency) ??
        Variants.FirstOrDefault(v => v.Speed == speed && v.NetFrequency is null);

    /// <summary>The net frequencies the model has data for (data for any net frequency is not listed).</summary>
    public IEnumerable<double> NetFrequencies =>
        Variants.Where(v => v.NetFrequency.HasValue).Select(v => v.NetFrequency!.Value).Distinct().Order();

    public bool HasCurrent => Variants.Any(v => v.HasCurrent);

    /// <summary>Mass flow coefficients converted to <paramref name="unit"/> (the polynomial is linear in its coefficients).</summary>
    public double[] MassFlowIn(double[] coefficients, En12900MassFlowUnit unit) =>
        Scale(coefficients, PerSecond(MassFlowUnit) / PerSecond(unit));

    /// <summary>Power coefficients converted to <paramref name="unit"/>.</summary>
    public double[] PowerIn(double[] coefficients, En12900PowerUnit unit) =>
        Scale(coefficients, Kilowatts(PowerUnit) / Kilowatts(unit));

    private static double PerSecond(En12900MassFlowUnit unit) => unit == En12900MassFlowUnit.KilogramsPerHour ? 1 / 3600.0 : 1;

    private static double Kilowatts(En12900PowerUnit unit) => unit == En12900PowerUnit.Watts ? 1 / 1000.0 : 1;

    private static double[] Scale(double[] values, double factor) =>
        factor == 1 ? values.ToArray() : values.Select(v => v * factor).ToArray();
}

/// <summary>
/// EN 12900 compressor models read from the <c>.json</c> files in a folder. The folder is watched, so files added,
/// changed or removed show up without a restart. Thread-safe: one instance is shared by all editor sessions.
/// The <see cref="ExampleModel"/> is always listed first.
/// </summary>
public sealed class CompressorLibrary : IDisposable
{
    public const string ReadmeFileName = "README.txt";

    public const string ExampleModelName = "Example CO₂ compressor";

    /// <summary>
    /// Built-in model used by new compressors and the examples, so they solve without a library folder: one polynomial
    /// set measured at 50 Hz that scales linearly, for any net frequency.
    /// </summary>
    public static CompressorModel ExampleModel { get; } = new()
    {
        Name = ExampleModelName,
        File = "(built in)",
        DischargeVariable = En12900DischargeVariable.Pressure,
        NominalSuperheat = 10,
        MassFlowUnit = En12900MassFlowUnit.KilogramsPerHour,
        PowerUnit = En12900PowerUnit.Watts,
        Variants = [ExampleVariant(CompressorSpeed.Fixed), ExampleVariant(CompressorSpeed.Variable)]
    };

    private static CompressorVariant ExampleVariant(CompressorSpeed speed) => new()
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

    private readonly Lock _lock = new();
    private readonly FileSystemWatcher? _watcher;
    private IReadOnlyList<CompressorModel> _models = [ExampleModel];
    private IReadOnlyList<string> _problems = [];
    private bool _dirty = true;
    private int _version;

    /// <summary>A library without a folder (only the example model), e.g. for tests.</summary>
    public static CompressorLibrary Empty { get; } = new();

    private CompressorLibrary()
    {
        _dirty = false;
    }

    /// <param name="directory">Folder with the model files; created with a README when missing.</param>
    public CompressorLibrary(string directory)
    {
        Directory = Path.GetFullPath(directory);
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(Path.Combine(Directory, ReadmeFileName), Readme);
            }

            _watcher = new FileSystemWatcher(Directory, "*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName
            };
            _watcher.Changed += (_, _) => Invalidate();
            _watcher.Created += (_, _) => Invalidate();
            _watcher.Deleted += (_, _) => Invalidate();
            _watcher.Renamed += (_, _) => Invalidate();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Without a watcher the folder is still read; changes then need Reload().
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>The library folder; null for <see cref="Empty"/>.</summary>
    public string? Directory { get; }

    /// <summary>The example model, then the folder's models sorted by name; re-read when the folder changed.</summary>
    public IReadOnlyList<CompressorModel> Models
    {
        get
        {
            EnsureLoaded();
            return _models;
        }
    }

    /// <summary>Files or models that could not be read, e.g. "bitzer.json: model 2: …".</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            EnsureLoaded();
            return _problems;
        }
    }

    /// <summary>Increases whenever the models were re-read, so cached validation can be redone.</summary>
    public int Version
    {
        get
        {
            EnsureLoaded();
            return _version;
        }
    }

    public CompressorModel? Find(string name) => Models.FirstOrDefault(m => m.Name == name);

    /// <summary>Re-reads the folder on the next access.</summary>
    public void Reload() => Invalidate();

    private void Invalidate()
    {
        lock (_lock)
            _dirty = Directory is not null;
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (!_dirty)
                return;
            _dirty = false;
            (_models, _problems) = Load(Directory!);
            _version++;
        }
    }

    private static (IReadOnlyList<CompressorModel>, IReadOnlyList<string>) Load(string directory)
    {
        List<CompressorModel> models = [];
        List<string> problems = [];
        string[] files;
        try
        {
            files = System.IO.Directory.Exists(directory)
                ? System.IO.Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([ExampleModel], [$"The compressor library folder cannot be read: {ex.Message}"]);
        }

        foreach (string path in files.Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(directory, path);
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{relative}: {ex.Message}");
                continue;
            }

            foreach (CompressorModel model in Parse(text, relative, problems))
            {
                if (model.Name == ExampleModelName)
                    problems.Add($"{relative}: the model name '{model.Name}' is reserved for the built-in example; skipped.");
                else if (models.Any(m => m.Name == model.Name))
                    problems.Add($"{relative}: the model name '{model.Name}' is already used in {models.First(m => m.Name == model.Name).File}; skipped.");
                else
                    models.Add(model);
            }
        }

        return ([ExampleModel, .. models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)], problems);
    }

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Reads the models of one file: a single model object, or an object with a "models" array whose entries inherit
    /// the file's other settings (manufacturer, units, discharge variable…). Problems are added to
    /// <paramref name="problems"/> prefixed with <paramref name="file"/>.
    /// </summary>
    /// <param name="requireVoltage">
    /// Whether variants with current polynomials need a nominal voltage. Off for data stored in documents, which may
    /// come from older files without one.
    /// </param>
    public static IReadOnlyList<CompressorModel> Parse(string json, string file, List<string> problems,
        bool requireVoltage = true)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            problems.Add($"{file}: not valid JSON ({ex.Message})");
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{file}: expected a JSON object.");
                return [];
            }

            JsonElement root = document.RootElement;
            if (Property(root, "models") is not { } list)
                return Read(root, root, file, null, requireVoltage, problems) is { } single ? [single] : [];

            if (list.ValueKind != JsonValueKind.Array)
            {
                problems.Add($"{file}: \"models\" must be an array.");
                return [];
            }

            List<CompressorModel> models = [];
            int number = 0;
            foreach (JsonElement entry in list.EnumerateArray())
            {
                number++;
                if (entry.ValueKind != JsonValueKind.Object)
                    problems.Add($"{file}: model {number}: expected a JSON object.");
                else if (Read(entry, root, file, number, requireVoltage, problems) is { } model)
                    models.Add(model);
            }

            return models;
        }
    }

    /// <summary>Reads one model; settings missing on <paramref name="entry"/> come from <paramref name="root"/>.</summary>
    private static CompressorModel? Read(JsonElement entry, JsonElement root, string file, int? number,
        bool requireVoltage, List<string> problems)
    {
        JsonElement[] scopes = [entry, root];
        JsonElement? Get(string name) => Lookup(scopes, name);

        string? modelName = String(Property(entry, "name"));
        string where = modelName is not null ? $"{file}: {modelName}" : number is { } n ? $"{file}: model {n}" : file;
        int before = problems.Count;
        void Problem(string message) => problems.Add($"{where}: {message}");

        if (modelName is null)
            Problem("\"name\" is missing.");

        string? manufacturer = String(Get("manufacturer"));
        string? refrigerant = String(Get("refrigerant"));

        En12900DischargeVariable discharge = default;
        switch (String(Get("dischargeVariable"))?.ToLowerInvariant().Replace(" ", ""))
        {
            case "pressure":
                discharge = En12900DischargeVariable.Pressure;
                break;
            case "saturationtemperature" or "temperature":
                discharge = En12900DischargeVariable.SaturationTemperature;
                break;
            case null:
                Problem("\"dischargeVariable\" is missing (\"pressure\" or \"saturationTemperature\").");
                break;
            default:
                Problem("\"dischargeVariable\" must be \"pressure\" or \"saturationTemperature\".");
                break;
        }

        double? superheat = Number(Get("nominalSuperheat"), "nominalSuperheat", Problem);
        if (superheat is null && Get("nominalSuperheat") is null)
            Problem("\"nominalSuperheat\" is missing (K).");
        else if (superheat < 0)
            Problem("\"nominalSuperheat\" must be at least 0 K.");

        En12900MassFlowUnit massFlowUnit = En12900MassFlowUnit.KilogramsPerHour;
        switch (String(Get("massFlowUnit"))?.ToLowerInvariant())
        {
            case null or "kg/h":
                break;
            case "kg/s":
                massFlowUnit = En12900MassFlowUnit.KilogramsPerSecond;
                break;
            default:
                Problem("\"massFlowUnit\" must be \"kg/h\" or \"kg/s\".");
                break;
        }

        En12900PowerUnit powerUnit = En12900PowerUnit.Watts;
        switch (String(Get("powerUnit"))?.ToLowerInvariant())
        {
            case null or "w":
                break;
            case "kw":
                powerUnit = En12900PowerUnit.Kilowatts;
                break;
            default:
                Problem("\"powerUnit\" must be \"W\" or \"kW\".");
                break;
        }

        List<CompressorVariant> variants = [];
        if (Property(entry, "variants") is { } list)
        {
            if (list.ValueKind != JsonValueKind.Array)
                Problem("\"variants\" must be an array.");
            else
            {
                int index = 0;
                foreach (JsonElement item in list.EnumerateArray())
                {
                    index++;
                    void VariantProblem(string message) => Problem($"variant {index}: {message}");
                    if (item.ValueKind != JsonValueKind.Object)
                        VariantProblem("expected a JSON object.");
                    else if (ReadVariant(item, [item, .. scopes], requireVoltage, VariantProblem) is { } variant)
                    {
                        if (variants.Any(v => v.Speed == variant.Speed && v.NetFrequency == variant.NetFrequency))
                            VariantProblem($"there is already a {variant.Label} variant.");
                        else
                            variants.Add(variant);
                    }
                }

                if (index == 0)
                    Problem("\"variants\" is empty.");
            }
        }
        else if (ReadLegacy(entry, scopes, Problem) is { } legacy)
            variants.AddRange(legacy);

        if (problems.Count > before)
            return null;

        return new CompressorModel
        {
            Name = manufacturer is null ? modelName! : $"{manufacturer} {modelName}",
            File = file,
            Refrigerant = refrigerant,
            DischargeVariable = discharge,
            NominalSuperheat = superheat!.Value,
            MassFlowUnit = massFlowUnit,
            PowerUnit = powerUnit,
            Variants = variants
        };
    }

    /// <summary>
    /// The first file format: one polynomial set on the model, measured at "nominalFrequency". It becomes a fixed- and
    /// a variable-speed variant for that net frequency with linear scaling.
    /// </summary>
    private static CompressorVariant[]? ReadLegacy(JsonElement entry, JsonElement[] scopes, Action<string> problem)
    {
        double frequency = Number(Lookup(scopes, "nominalFrequency"), "nominalFrequency", problem)
                           ?? En12900Polynomial.DefaultNominalFrequency;
        if (frequency <= 0)
        {
            problem("\"nominalFrequency\" must be above 0 Hz.");
            return null;
        }

        double? voltage = Voltage(Lookup(scopes, "nominalVoltage"), problem);
        if (Point(entry, frequency, problem, "give \"variants\", or \"massFlow\" and \"power\" on the model") is not { } point)
            return null;

        return
        [
            new CompressorVariant
            {
                Speed = CompressorSpeed.Fixed, NetFrequency = frequency, NominalVoltage = voltage,
                Scaling = En12900FrequencyScaling.Linear, Points = [point]
            },
            new CompressorVariant
            {
                Speed = CompressorSpeed.Variable, NetFrequency = frequency, NominalVoltage = voltage,
                Scaling = En12900FrequencyScaling.Linear, Points = [point]
            }
        ];
    }

    private static CompressorVariant? ReadVariant(JsonElement variant, JsonElement[] scopes, bool requireVoltage,
        Action<string> problem)
    {
        int problems = 0;
        void Problem(string message)
        {
            problems++;
            problem(message);
        }

        CompressorSpeed speed = default;
        switch (String(Property(variant, "speed"))?.ToLowerInvariant().Replace(" ", ""))
        {
            case "fixed" or "fixedspeed":
                speed = CompressorSpeed.Fixed;
                break;
            case "variable" or "variablespeed":
                speed = CompressorSpeed.Variable;
                break;
            case null:
                Problem("\"speed\" is missing (\"fixed\" or \"variable\").");
                break;
            default:
                Problem("\"speed\" must be \"fixed\" or \"variable\".");
                break;
        }

        double? netFrequency = Number(Lookup(scopes, "netFrequency"), "netFrequency", Problem);
        if (netFrequency <= 0)
            Problem("\"netFrequency\" must be above 0 Hz.");
        double? voltage = Voltage(Lookup(scopes, "nominalVoltage"), Problem);

        En12900FrequencyScaling? scaling = null;
        switch (String(Lookup(scopes, "scaling"))?.ToLowerInvariant())
        {
            case null:
                break;
            case "linear":
                scaling = En12900FrequencyScaling.Linear;
                break;
            case "interpolate" or "interpolated":
                scaling = En12900FrequencyScaling.Interpolated;
                break;
            default:
                Problem("\"scaling\" must be \"linear\" or \"interpolate\".");
                break;
        }

        List<CompressorPoint> points = [];
        if (Property(variant, "frequencies") is { } list)
        {
            if (list.ValueKind != JsonValueKind.Array)
                Problem("\"frequencies\" must be an array.");
            else
            {
                int index = 0;
                foreach (JsonElement item in list.EnumerateArray())
                {
                    index++;
                    void PointProblem(string message) => Problem($"frequency {index}: {message}");
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        PointProblem("expected a JSON object.");
                        continue;
                    }

                    double? frequency = Number(Property(item, "frequency"), "frequency", PointProblem);
                    if (frequency is null)
                    {
                        if (Property(item, "frequency") is null)
                            PointProblem("\"frequency\" is missing (Hz).");
                        continue;
                    }

                    if (Point(item, frequency.Value, PointProblem) is { } point)
                        points.Add(point);
                }
            }
        }
        else
        {
            double? frequency = Number(Property(variant, "frequency"), "frequency", Problem) ?? netFrequency;
            if (frequency is null)
                Problem("\"frequency\" is missing: give \"netFrequency\", or \"frequency\" for data valid at any net frequency.");
            else if (Point(variant, frequency.Value, Problem) is { } point)
                points.Add(point);
        }

        if (problems > 0)
            return null;

        points.Sort((a, b) => a.Frequency.CompareTo(b.Frequency));
        if (points.Count == 0)
            Problem("\"frequencies\" is empty.");
        else if (points.Any(p => p.Frequency <= 0))
            Problem("frequencies must be above 0 Hz.");
        else if (points.Zip(points.Skip(1)).FirstOrDefault(p => p.First.Frequency == p.Second.Frequency) is ({ } a, _))
            Problem($"two polynomial sets are given for {CompressorVariant.Format(a.Frequency)} Hz.");
        else if (points.Any(p => p.Current is null) && points.Any(p => p.Current is not null))
            Problem("give \"current\" for every frequency or for none.");
        else if (speed == CompressorSpeed.Fixed && points.Count > 1)
            Problem("a fixed-speed variant has one polynomial set (at its net frequency).");
        else if (scaling == En12900FrequencyScaling.Linear && points.Count > 1)
            Problem("\"linear\" scaling uses one polynomial set; use \"interpolate\" for several frequencies.");
        else if (requireVoltage && points[0].Current is not null && voltage is null)
            Problem("\"nominalVoltage\" is missing (V); it is needed to scale the current to the net voltage.");

        if (problems > 0)
            return null;

        return new CompressorVariant
        {
            Speed = speed,
            NetFrequency = netFrequency,
            NominalVoltage = voltage,
            Scaling = speed == CompressorSpeed.Fixed
                ? En12900FrequencyScaling.Linear
                : scaling ?? (points.Count > 1 ? En12900FrequencyScaling.Interpolated : En12900FrequencyScaling.Linear),
            Points = points
        };
    }

    private static CompressorPoint? Point(JsonElement element, double frequency, Action<string> problem,
        string? missingHint = null)
    {
        if (missingHint is not null && Property(element, "massFlow") is null && Property(element, "power") is null)
        {
            problem($"no polynomials: {missingHint}.");
            return null;
        }

        double[]? massFlow = Coefficients(Property(element, "massFlow"), "massFlow", true, problem);
        double[]? power = Coefficients(Property(element, "power"), "power", true, problem);
        double[]? current = Coefficients(Property(element, "current"), "current", false, problem);
        return massFlow is null || power is null || (Property(element, "current") is not null && current is null)
            ? null
            : new CompressorPoint(frequency, massFlow, power, current);
    }

    private static double? Voltage(JsonElement? element, Action<string> problem)
    {
        double? voltage = Number(element, "nominalVoltage", problem);
        if (voltage <= 0)
        {
            problem("\"nominalVoltage\" must be above 0 V.");
            return null;
        }

        return voltage;
    }

    /// <summary>The first scope that has the property.</summary>
    private static JsonElement? Lookup(JsonElement[] scopes, string name)
    {
        foreach (JsonElement scope in scopes)
        {
            if (Property(scope, name) is { } value)
                return value;
        }

        return null;
    }

    /// <summary>A property by name, ignoring case; null values count as missing.</summary>
    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
        }

        return null;
    }

    private static string? String(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } e && e.GetString()?.Trim() is { Length: > 0 } text ? text : null;

    private static double? Number(JsonElement? element, string name, Action<string> problem)
    {
        switch (element)
        {
            case null:
                return null;
            case { ValueKind: JsonValueKind.Number } e when e.TryGetDouble(out double value) && double.IsFinite(value):
                return value;
            case { ValueKind: JsonValueKind.String } e when double.TryParse(e.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) && double.IsFinite(value):
                return value;
            default:
                problem($"\"{name}\" must be a number.");
                return null;
        }
    }

    private static double[]? Coefficients(JsonElement? element, string name, bool required, Action<string> problem)
    {
        if (element is null)
        {
            if (required)
                problem($"\"{name}\" is missing (10 coefficients).");
            return null;
        }

        if (element.Value.ValueKind != JsonValueKind.Array)
        {
            problem($"\"{name}\" must be an array of 10 numbers.");
            return null;
        }

        List<double> values = [];
        foreach (JsonElement item in element.Value.EnumerateArray())
        {
            if (Number(item, name, _ => { }) is not { } value)
            {
                problem($"\"{name}\" must contain only numbers.");
                return null;
            }

            values.Add(value);
        }

        if (values.Count != 10)
        {
            problem($"\"{name}\" needs 10 coefficients, got {values.Count}.");
            return null;
        }

        return values.ToArray();
    }

    /// <summary>
    /// Writes one model as a single-model file in the current format (with its full name and without the
    /// manufacturer), e.g. to embed it in a document. <see cref="Parse"/> reads it back to an equal model.
    /// Equal models give equal text.
    /// </summary>
    public static string Write(CompressorModel model)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", model.Name);
            if (model.Refrigerant is not null)
                writer.WriteString("refrigerant", model.Refrigerant);
            writer.WriteString("dischargeVariable",
                model.DischargeVariable == En12900DischargeVariable.Pressure ? "pressure" : "saturationTemperature");
            writer.WriteNumber("nominalSuperheat", model.NominalSuperheat);
            writer.WriteString("massFlowUnit", model.MassFlowUnit == En12900MassFlowUnit.KilogramsPerHour ? "kg/h" : "kg/s");
            writer.WriteString("powerUnit", model.PowerUnit == En12900PowerUnit.Watts ? "W" : "kW");
            writer.WriteStartArray("variants");
            foreach (CompressorVariant variant in model.Variants)
            {
                writer.WriteStartObject();
                writer.WriteString("speed", variant.Speed == CompressorSpeed.Fixed ? "fixed" : "variable");
                if (variant.NetFrequency is { } net)
                    writer.WriteNumber("netFrequency", net);
                if (variant.NominalVoltage is { } voltage)
                    writer.WriteNumber("nominalVoltage", voltage);
                writer.WriteString("scaling", variant.Scaling == En12900FrequencyScaling.Linear ? "linear" : "interpolate");
                writer.WriteStartArray("frequencies");
                foreach (CompressorPoint point in variant.Points)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("frequency", point.Frequency);
                    WriteArray(writer, "massFlow", point.MassFlow);
                    WriteArray(writer, "power", point.Power);
                    if (point.Current is not null)
                        WriteArray(writer, "current", point.Current);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteArray(Utf8JsonWriter writer, string name, double[] values)
    {
        writer.WriteStartArray(name);
        foreach (double value in values)
            writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }

    public void Dispose() => _watcher?.Dispose();

    /// <summary>Written into a new library folder to explain the file format.</summary>
    public const string Readme =
        """
        EN 12900 compressor library
        ===========================

        Every .json file in this folder (and its subfolders) adds compressor models to the
        "Model" list of the "Compressor (EN 12900)" component. Changes are picked up
        automatically. Choosing a model copies all of its data into the system, so saved
        .cycle.json files keep working without the library.

        A file holds one model, or several in a "models" array. Settings next to "models"
        apply to every model in the file unless a model sets them itself; settings of a
        model apply to its variants unless a variant sets them itself.

        {
          "manufacturer": "Acme",                  // optional, shown in front of the name
          "refrigerant": "CarbonDioxide",          // optional, CoolProp name (R744, R134a, ...)
          "dischargeVariable": "pressure",         // "pressure" (bar abs.) or "saturationTemperature" (°C)
          "nominalSuperheat": 10,                  // K, suction superheat of the polynomials
          "massFlowUnit": "kg/h",                  // "kg/h" (default) or "kg/s"
          "powerUnit": "W",                        // "W" (default) or "kW"
          "nominalVoltage": 400,                   // V the current polynomials were measured at
          "models": [
            {
              "name": "AC-100",
              "variants": [
                // Fixed speed: one polynomial set, measured at the net frequency.
                { "speed": "fixed", "netFrequency": 50,
                  "massFlow": [c1, ..., c10], "power": [c1, ..., c10], "current": [c1, ..., c10] },
                { "speed": "fixed", "netFrequency": 60, "nominalVoltage": 460,
                  "massFlow": [...], "power": [...], "current": [...] },

                // Variable speed, one set per measured frequency, interpolated between them
                // (quadratic with three or more, linear with two; extrapolated beyond them).
                { "speed": "variable", "netFrequency": 50, "scaling": "interpolate",
                  "frequencies": [
                    { "frequency": 30, "massFlow": [...], "power": [...], "current": [...] },
                    { "frequency": 50, "massFlow": [...], "power": [...], "current": [...] },
                    { "frequency": 70, "massFlow": [...], "power": [...], "current": [...] }
                  ] },

                // Variable speed from one set: mass flow and power scale linearly with
                // frequency. The set is measured at netFrequency unless "frequency" is given.
                { "speed": "variable", "netFrequency": 60, "scaling": "linear",
                  "massFlow": [...], "power": [...], "current": [...] }
              ]
            }
          ]
        }

        "scaling" is "linear" (one set) or "interpolate" (several sets); it defaults to
        linear for one set and interpolate for several. "current" is optional (A); when
        given, "nominalVoltage" is required and the current is scaled by nominal / net
        voltage. Below the net frequency the drive lowers the voltage, so the current is
        interpolated only from sets at or below the net frequency (with linear scaling it
        stays constant there); above it only from sets at or above it.

        A variant without "netFrequency" applies to any net frequency (give "frequency").
        The older format with "massFlow", "power", "current" and "nominalFrequency" directly
        on the model is still read as fixed and variable speed at that net frequency.

        The ten coefficients follow EN 12900:
          c1 + c2·S + c3·D + c4·S² + c5·S·D + c6·D² + c7·S³ + c8·S²·D + c9·S·D² + c10·D³
        with S the suction saturation temperature (°C) and D the discharge variable.
        Comments (// ...) and trailing commas are allowed. Model names must be unique.
        """;
}
