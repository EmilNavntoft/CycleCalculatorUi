using System.Globalization;
using CycleCalculator.CycleModel.Model;
using System.Text.Json;
using CycleCalculator.CycleModel.Model.En12900;

namespace CycleCalculatorUi.Core.Catalog;

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

    public double NominalFrequency { get; init; } = En12900Polynomial.DefaultNominalFrequency;

    public En12900MassFlowUnit MassFlowUnit { get; init; } = En12900MassFlowUnit.KilogramsPerHour;

    public En12900PowerUnit PowerUnit { get; init; } = En12900PowerUnit.Watts;

    public required double[] MassFlow { get; init; }

    public required double[] Power { get; init; }

    public double[]? Current { get; init; }

    /// <summary>Mass flow coefficients in <paramref name="unit"/> (the polynomial is linear in its coefficients).</summary>
    public double[] MassFlowIn(En12900MassFlowUnit unit) => Scale(MassFlow, PerSecond(MassFlowUnit) / PerSecond(unit));

    /// <summary>Power coefficients in <paramref name="unit"/>.</summary>
    public double[] PowerIn(En12900PowerUnit unit) => Scale(Power, Kilowatts(PowerUnit) / Kilowatts(unit));

    private static double PerSecond(En12900MassFlowUnit unit) => unit == En12900MassFlowUnit.KilogramsPerHour ? 1 / 3600.0 : 1;

    private static double Kilowatts(En12900PowerUnit unit) => unit == En12900PowerUnit.Watts ? 1 / 1000.0 : 1;

    private static double[] Scale(double[] values, double factor) =>
        factor == 1 ? values.ToArray() : values.Select(v => v * factor).ToArray();
}

/// <summary>
/// EN 12900 compressor models read from the <c>.json</c> files in a folder. The folder is watched, so files added,
/// changed or removed show up without a restart. Thread-safe: one instance is shared by all editor sessions.
/// </summary>
public sealed class CompressorLibrary : IDisposable
{
    public const string ReadmeFileName = "README.txt";

    private readonly Lock _lock = new();
    private readonly FileSystemWatcher? _watcher;
    private IReadOnlyList<CompressorModel> _models = [];
    private IReadOnlyList<string> _problems = [];
    private bool _dirty = true;
    private int _version;

    /// <summary>A library without a folder, e.g. for tests.</summary>
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

    /// <summary>The models, sorted by name; re-read when the folder changed.</summary>
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
            return ([], [$"The compressor library folder cannot be read: {ex.Message}"]);
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
                if (models.Any(m => m.Name == model.Name))
                    problems.Add($"{relative}: the model name '{model.Name}' is already used in {models.First(m => m.Name == model.Name).File}; skipped.");
                else
                    models.Add(model);
            }
        }

        return (models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList(), problems);
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
    public static IReadOnlyList<CompressorModel> Parse(string json, string file, List<string> problems)
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
                return Read(root, default, file, null, problems) is { } single ? [single] : [];

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
                else if (Read(entry, root, file, number, problems) is { } model)
                    models.Add(model);
            }

            return models;
        }
    }

    /// <summary>Reads one model; settings missing on <paramref name="entry"/> come from <paramref name="defaults"/>.</summary>
    private static CompressorModel? Read(JsonElement entry, JsonElement defaults, string file, int? number,
        List<string> problems)
    {
        JsonElement? Get(string name) =>
            Property(entry, name) ?? (defaults.ValueKind == JsonValueKind.Object ? Property(defaults, name) : null);

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

        double frequency = Number(Get("nominalFrequency"), "nominalFrequency", Problem) ?? En12900Polynomial.DefaultNominalFrequency;
        if (frequency <= 0)
            Problem("\"nominalFrequency\" must be above 0 Hz.");

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

        double[]? massFlow = Coefficients(Property(entry, "massFlow"), "massFlow", true, Problem);
        double[]? power = Coefficients(Property(entry, "power"), "power", true, Problem);
        double[]? current = Coefficients(Property(entry, "current"), "current", false, Problem);

        if (problems.Count > before)
            return null;

        return new CompressorModel
        {
            Name = manufacturer is null ? modelName! : $"{manufacturer} {modelName}",
            File = file,
            Refrigerant = refrigerant,
            DischargeVariable = discharge,
            NominalSuperheat = superheat!.Value,
            NominalFrequency = frequency,
            MassFlowUnit = massFlowUnit,
            PowerUnit = powerUnit,
            MassFlow = massFlow!,
            Power = power!,
            Current = current
        };
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
            case { ValueKind: JsonValueKind.Number } e when e.TryGetDouble(out double value):
                return value;
            case { ValueKind: JsonValueKind.String } e when double.TryParse(e.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value):
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
            if (Number(item, name, _ => { }) is not { } value || !double.IsFinite(value))
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

    public void Dispose() => _watcher?.Dispose();

    /// <summary>Written into a new library folder to explain the file format.</summary>
    public const string Readme =
        """
        EN 12900 compressor library
        ===========================

        Every .json file in this folder (and its subfolders) adds compressor models to the
        "Model" list of the "Compressor (EN 12900)" component. Changes are picked up
        automatically. Choosing a model copies its polynomials into the system, so saved
        .cycle.json files keep working without the library.

        A file holds one model, or several in a "models" array. Settings next to "models"
        apply to every model in the file unless a model sets them itself.

        {
          "manufacturer": "Acme",                  // optional, shown in front of the name
          "refrigerant": "CarbonDioxide",          // optional, CoolProp name (R744, R134a, ...)
          "dischargeVariable": "pressure",         // "pressure" (bar abs.) or "saturationTemperature" (°C)
          "nominalSuperheat": 10,                  // K, suction superheat of the polynomials
          "nominalFrequency": 50,                  // Hz, optional (default 50)
          "massFlowUnit": "kg/h",                  // "kg/h" (default) or "kg/s"
          "powerUnit": "W",                        // "W" (default) or "kW"
          "models": [
            {
              "name": "AC-100",
              "massFlow": [c1, c2, c3, c4, c5, c6, c7, c8, c9, c10],
              "power":    [c1, c2, c3, c4, c5, c6, c7, c8, c9, c10],
              "current":  [c1, c2, c3, c4, c5, c6, c7, c8, c9, c10]   // optional, A
            }
          ]
        }

        The ten coefficients follow EN 12900:
          c1 + c2·S + c3·D + c4·S² + c5·S·D + c6·D² + c7·S³ + c8·S²·D + c9·S·D² + c10·D³
        with S the suction saturation temperature (°C) and D the discharge variable.
        Comments (// ...) and trailing commas are allowed. Model names must be unique.
        """;
}
