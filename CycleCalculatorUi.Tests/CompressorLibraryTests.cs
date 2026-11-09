using CycleCalculator.CycleModel.Model;
using CycleCalculator.CycleModel.Model.En12900;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Editing;
using CycleCalculatorUi.Core.Examples;
using CycleCalculatorUi.Core.Persistence;
using CycleCalculatorUi.Core.Solving;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

/// <summary>EN 12900 compressor models read from the library folder and chosen in the editor.</summary>
[TestFixture]
public class CompressorLibraryTests
{
    /// <summary>The older format: one polynomial set per model.</summary>
    private const string TwoLegacyModels =
        """
        {
          // comments and trailing commas are fine
          "manufacturer": "Acme",
          "refrigerant": "R744",
          "dischargeVariable": "pressure",
          "nominalSuperheat": 10,
          "models": [
            { "name": "A-1", "massFlow": [3600, 36, 0, 0, 0, 0, 0, 0, 0, 0], "power": [5000, 10, 20, 0, 0, 0, 0, 0, 0, 0] },
            { "name": "A-2", "nominalFrequency": 60, "massFlowUnit": "kg/s", "powerUnit": "kW",
              "massFlow": [1, 0, 0, 0, 0, 0, 0, 0, 0, 0], "power": [5, 0, 0, 0, 0, 0, 0, 0, 0, 0],
              "current": [8, 0, 0, 0, 0, 0, 0, 0, 0, 0], },
          ]
        }
        """;

    /// <summary>
    /// A model with fixed speed at 50 and 60 Hz, variable speed at 50 Hz from five frequencies (mass flow 0.02·f kg/s,
    /// power 0.1·f kW, current 10 A up to 50 Hz then rising) and variable speed at 60 Hz from one set (linear).
    /// </summary>
    private const string VariantModel =
        """
        {
          "manufacturer": "Acme", "refrigerant": "R744", "dischargeVariable": "pressure", "nominalSuperheat": 10,
          "massFlowUnit": "kg/s", "powerUnit": "kW", "nominalVoltage": 400,
          "models": [ { "name": "V-1", "variants": [
            { "speed": "fixed", "netFrequency": 50,
              "massFlow": [1, 0,0,0,0,0,0,0,0,0], "power": [5, 0,0,0,0,0,0,0,0,0], "current": [10, 0,0,0,0,0,0,0,0,0] },
            { "speed": "fixed", "netFrequency": 60, "nominalVoltage": 460,
              "massFlow": [1.2, 0,0,0,0,0,0,0,0,0], "power": [6, 0,0,0,0,0,0,0,0,0], "current": [11, 0,0,0,0,0,0,0,0,0] },
            { "speed": "variable", "netFrequency": 50, "scaling": "interpolate", "frequencies": [
              { "frequency": 30, "massFlow": [0.6, 0,0,0,0,0,0,0,0,0], "power": [3, 0,0,0,0,0,0,0,0,0], "current": [10, 0,0,0,0,0,0,0,0,0] },
              { "frequency": 70, "massFlow": [1.4, 0,0,0,0,0,0,0,0,0], "power": [7, 0,0,0,0,0,0,0,0,0], "current": [16, 0,0,0,0,0,0,0,0,0] },
              { "frequency": 40, "massFlow": [0.8, 0,0,0,0,0,0,0,0,0], "power": [4, 0,0,0,0,0,0,0,0,0], "current": [10, 0,0,0,0,0,0,0,0,0] },
              { "frequency": 50, "massFlow": [1.0, 0,0,0,0,0,0,0,0,0], "power": [5, 0,0,0,0,0,0,0,0,0], "current": [10, 0,0,0,0,0,0,0,0,0] },
              { "frequency": 60, "massFlow": [1.2, 0,0,0,0,0,0,0,0,0], "power": [6, 0,0,0,0,0,0,0,0,0], "current": [12, 0,0,0,0,0,0,0,0,0] }
            ] },
            { "speed": "variable", "netFrequency": 60, "scaling": "linear",
              "massFlow": [1.2, 0,0,0,0,0,0,0,0,0], "power": [6, 0,0,0,0,0,0,0,0,0], "current": [11, 0,0,0,0,0,0,0,0,0] }
          ] } ]
        }
        """;

    private string _directory = null!;

    [SetUp]
    public void SetUp() =>
        _directory = Path.Combine(Path.GetTempPath(), "compressor-library-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    private static IReadOnlyList<CompressorModel> ParseClean(string json)
    {
        List<string> problems = [];
        IReadOnlyList<CompressorModel> models = CompressorLibrary.Parse(json, "acme.json", problems);
        Assert.That(problems, Is.Empty);
        return models;
    }

    [Test]
    public void Parse_LegacyModelsInheritTheFileSettingsAndBecomeFixedAndVariableSpeed()
    {
        IReadOnlyList<CompressorModel> models = ParseClean(TwoLegacyModels);

        Assert.That(models.Select(m => m.Name), Is.EqualTo(new[] { "Acme A-1", "Acme A-2" }));
        Assert.That(models[0].Refrigerant, Is.EqualTo("R744"));
        Assert.That(models[0].MassFlowUnit, Is.EqualTo(En12900MassFlowUnit.KilogramsPerHour));
        Assert.That(models[0].Variants.Select(v => (v.Speed, v.NetFrequency, v.Scaling)), Is.EqualTo(new[]
        {
            (CompressorSpeed.Fixed, (double?)50, En12900FrequencyScaling.Linear),
            (CompressorSpeed.Variable, (double?)50, En12900FrequencyScaling.Linear)
        }));
        Assert.That(models[0].HasCurrent, Is.False);
        Assert.That(models[1].MassFlowUnit, Is.EqualTo(En12900MassFlowUnit.KilogramsPerSecond));
        Assert.That(models[1].PowerUnit, Is.EqualTo(En12900PowerUnit.Kilowatts));
        Assert.That(models[1].NetFrequencies, Is.EqualTo(new[] { 60.0 }));
        Assert.That(models[1].Variants[0].Points.Single().Current![0], Is.EqualTo(8));
    }

    [Test]
    public void Parse_VariantsPerSpeedAndNetFrequency()
    {
        CompressorModel model = ParseClean(VariantModel).Single();

        Assert.That(model.NetFrequencies, Is.EqualTo(new[] { 50.0, 60.0 }));
        CompressorVariant interpolated = model.Variant(CompressorSpeed.Variable, 50)!;
        Assert.That(interpolated.Scaling, Is.EqualTo(En12900FrequencyScaling.Interpolated));
        Assert.That(interpolated.Points.Select(p => p.Frequency), Is.EqualTo(new[] { 30.0, 40, 50, 60, 70 }));
        Assert.That(interpolated.NominalVoltage, Is.EqualTo(400), "inherited from the file");
        Assert.That(model.Variant(CompressorSpeed.Fixed, 60)!.NominalVoltage, Is.EqualTo(460));
        CompressorVariant linear = model.Variant(CompressorSpeed.Variable, 60)!;
        Assert.That(linear.Scaling, Is.EqualTo(En12900FrequencyScaling.Linear));
        Assert.That(linear.Points.Single().Frequency, Is.EqualTo(60), "measured at the net frequency");
        Assert.That(model.Variant(CompressorSpeed.Fixed, 55), Is.Null);
    }

    [TestCase("""{ "speed": "variable", "netFrequency": 50, "scaling": "linear", "frequencies": [ {"frequency": 30, "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0]}, {"frequency": 50, "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0]} ] }""",
        "\"linear\" scaling uses one polynomial set")]
    [TestCase("""{ "speed": "variable", "netFrequency": 50, "scaling": "cubic", "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0] }""",
        "\"scaling\" must be \"linear\" or \"interpolate\"")]
    [TestCase("""{ "speed": "fixed", "netFrequency": 50, "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0], "current": [1,0,0,0,0,0,0,0,0,0] }""",
        "\"nominalVoltage\" is missing")]
    [TestCase("""{ "speed": "sometimes", "netFrequency": 50, "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0] }""",
        "\"speed\" must be \"fixed\" or \"variable\"")]
    [TestCase("""{ "speed": "fixed", "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0] }""",
        "\"frequency\" is missing")]
    [TestCase("""{ "speed": "variable", "netFrequency": 50, "frequencies": [ {"frequency": 50, "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0]}, {"frequency": 50, "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0]} ] }""",
        "two polynomial sets are given for 50 Hz")]
    public void Parse_ReportsInvalidVariants(string variant, string message)
    {
        string json = $$"""{ "name": "X", "dischargeVariable": "pressure", "nominalSuperheat": 10, "variants": [ {{variant}} ] }""";
        List<string> problems = [];

        Assert.That(CompressorLibrary.Parse(json, "f.json", problems), Is.Empty);
        Assert.That(problems, Has.Some.Contains("f.json: X: variant 1: " + message));
    }

    [Test]
    public void Parse_RejectsDuplicateVariants()
    {
        const string json =
            """
            { "name": "X", "dischargeVariable": "pressure", "nominalSuperheat": 10, "netFrequency": 50, "variants": [
              { "speed": "fixed", "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0] },
              { "speed": "fixed", "massFlow": [2,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0] } ] }
            """;
        List<string> problems = [];

        Assert.That(CompressorLibrary.Parse(json, "f.json", problems), Is.Empty);
        Assert.That(problems, Has.Some.Contains("variant 2: there is already a fixed speed, 50 Hz variant"));
    }

    [Test]
    public void Parse_ReportsIncompleteModelsAndKeepsTheOthers()
    {
        const string json =
            """
            { "dischargeVariable": "saturationTemperature", "nominalSuperheat": 10, "models": [
              { "name": "Good", "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0] },
              { "name": "Short", "massFlow": [1,2,3], "power": [1,0,0,0,0,0,0,0,0,0] },
              { "massFlow": [1,0,0,0,0,0,0,0,0,0] }
            ] }
            """;
        List<string> problems = [];
        IReadOnlyList<CompressorModel> models = CompressorLibrary.Parse(json, "f.json", problems);

        Assert.That(models.Select(m => m.Name), Is.EqualTo(new[] { "Good" }));
        Assert.That(models[0].DischargeVariable, Is.EqualTo(En12900DischargeVariable.SaturationTemperature));
        Assert.That(problems, Has.Some.Contains("f.json: Short: \"massFlow\" needs 10 coefficients, got 3."));
        Assert.That(problems, Has.Some.Contains("f.json: model 3: \"name\" is missing."));
        Assert.That(problems, Has.Some.Contains("f.json: model 3: \"power\" is missing"));

        problems.Clear();
        Assert.That(CompressorLibrary.Parse("{ not json", "bad.json", problems), Is.Empty);
        Assert.That(problems.Single(), Does.StartWith("bad.json: not valid JSON"));
    }

    [Test]
    public void Write_ReadsBackToTheSameModel()
    {
        foreach (CompressorModel model in ParseClean(VariantModel).Concat(ParseClean(TwoLegacyModels)).Append(En12900Library.ExampleCompressor))
        {
            string text = CompressorLibrary.Write(model);
            List<string> problems = [];
            CompressorModel read = CompressorLibrary.Parse(text, "", problems, requireVoltage: false).Single();
            Assert.That(problems, Is.Empty);
            Assert.That(read.Name, Is.EqualTo(model.Name));
            Assert.That(CompressorLibrary.Write(read), Is.EqualTo(text), model.Name);
            Assert.That(read.Variants.SelectMany(v => v.Points).SelectMany(p => p.MassFlow),
                Is.EqualTo(model.Variants.SelectMany(v => v.Points).SelectMany(p => p.MassFlow)));
        }
    }

    [Test]
    public void Library_ReadsNewFilesAndReportsAMissingFolder()
    {
        using CompressorLibrary library = new(_directory);
        Assert.That(library.Models, Is.Empty);
        Assert.That(library.Problems.Single(), Does.Contain("does not exist"));

        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "acme.json"), TwoLegacyModels);
        Directory.CreateDirectory(Path.Combine(_directory, "other"));
        File.WriteAllText(Path.Combine(_directory, "other", "dup.json"), TwoLegacyModels);
        library.Reload();

        Assert.That(library.Models.Select(m => m.Name),
            Is.EqualTo(new[] { "Acme A-1", "Acme A-2" }));
        Assert.That(library.Problems, Has.Count.EqualTo(2).And.All.Contains("is already used in acme.json"));
    }

    [Test]
    public void ShippedCollection_IsCopiedNextToTheProgram()
    {
        Assert.That(File.Exists(Path.Combine(CompressorLibrary.ShippedDirectory, CompressorLibrary.ReadmeFileName)));
        using CompressorLibrary library = new(CompressorLibrary.ShippedDirectory);
        Assert.That(library.Problems, Is.Empty);
    }

    [Test]
    public void NewCompressors_UseTheFirstLibraryModel()
    {
        (EditorState state, _, _) = EditorWithLibrary();
        NodeModel node = state.Catalog.Get("En12900Compressor").CreateNode("C", 0, 0, state.Document.DefaultFluid);

        Assert.That(node.Choices[En12900Library.ModelKey], Is.EqualTo("Acme A-1"));
        Assert.That(En12900Library.Embedded(node, 0)!.Name, Is.EqualTo("Acme A-1"));
    }

    [Test]
    public void WithAnEmptyLibrary_NewCompressorsAskForAModel()
    {
        ComponentCatalog catalog = ComponentCatalog.Default;
        SystemDocument document = new();
        NodeModel node = catalog.Get("En12900Compressor").CreateNode("C", 0, 0, document.DefaultFluid);
        document.Nodes.Add(node);

        ParameterDescriptor model = catalog.Get(node.TypeId).FindParameter(En12900Library.ModelKey)!;
        Assert.That(model.OptionsShown(node, document, catalog).Single().Label, Is.EqualTo("No models in the library"));
        Assert.That(new ModelBuilder(catalog).Validate(document),
            Has.Some.Matches<ValidationIssue>(i => i.ParameterKey == En12900Library.ModelKey && i.Message.Contains("choose a compressor model")));
    }

    [Test]
    public void TheEjectorExample_EmbedsItsCompressorWithoutWarnings()
    {
        SystemDocument document = ShippedExamples.EjectorHeatPump();
        NodeModel node = document.Nodes.Single(n => n.Name == "Comp1");

        Assert.That(En12900Library.Embedded(node, 0)!.Name, Is.EqualTo(En12900Library.ExampleCompressor.Name));
        Assert.That(new ModelBuilder(ComponentCatalog.Default).Validate(document), Is.Empty);
    }

    [Test]
    public void ChoosingAModel_EmbedsItAndUndoRestoresTheOldOne()
    {
        (EditorState state, NodeModel node, _) = EditorWithLibrary();
        string before = node.Texts[En12900Library.ModelKey];

        state.SetChoice(node.Id, En12900Library.ModelKey, "Acme A-2");
        node = state.Document.FindNode(node.Id)!;

        // A-2 only has 60 Hz data: the only compressor switches the net frequency.
        Assert.That(En12900Library.Embedded(node, 0)!.Name, Is.EqualTo("Acme A-2"));
        Assert.That(node.Choices[En12900Library.NetFrequencyKey], Is.EqualTo("60"));
        Assert.That(state.IssuesFor(node.Id).Where(i => i.Severity == IssueSeverity.Error), Is.Empty);

        state.Undo();
        node = state.Document.FindNode(node.Id)!;
        Assert.That(node.Texts[En12900Library.ModelKey], Is.EqualTo(before));
        Assert.That(node.Choices[En12900Library.NetFrequencyKey], Is.EqualTo("50"));
    }

    [Test]
    public void NetFrequencyAndSpeed_ChooseTheVariant()
    {
        (EditorState state, NodeModel node, _) = EditorWithLibrary();
        state.SetChoice(node.Id, En12900Library.ModelKey, "Acme V-1");
        node = state.Document.FindNode(node.Id)!;
        Assert.That(node.Numbers[En12900Library.NetVoltageKey], Is.EqualTo(400), "takes over the model's voltage");

        En12900Compressor Build() => BuildCompressor(state.Document, node);
        En12900CompressorMap map = Build().Maps.Single();
        Assert.That(map.EvaluateMassFlow(0, 100), Is.EqualTo(1));
        Assert.That(map.IsFrequencyControlled, Is.False);

        node.Choices[En12900Library.NetFrequencyKey] = "60";
        Assert.That(Build().Maps.Single().EvaluateMassFlow(0, 100), Is.EqualTo(1.2));
        Assert.That(Build().Maps.Single().EvaluateCurrent(0, 100), Is.EqualTo(11 * 460 / 400.0).Within(1e-12),
            "current scaled from the 460 V nominal to the 400 V net voltage");

        node.Choices[En12900Library.NetFrequencyKey] = "50";
        node.Choices[BuiltInComponents.SpeedControlKey] = "variable";
        node.Numbers["operatingFrequency"] = 54;
        map = Build().Maps.Single();
        Assert.That(map.Scaling, Is.EqualTo(En12900FrequencyScaling.Interpolated));
        Assert.That(map.EvaluateMassFlow(0, 100), Is.EqualTo(0.02 * 54).Within(1e-12));
        Assert.That(map.EvaluatePower(0, 100), Is.EqualTo(0.1 * 54).Within(1e-12));

        node.Numbers["operatingFrequency"] = 45;
        Assert.That(Build().Maps.Single().EvaluateCurrent(0, 100), Is.EqualTo(10).Within(1e-12),
            "below the net frequency only the sets up to 50 Hz count");
    }

    [Test]
    public void MissingVariant_IsAnError()
    {
        (EditorState state, NodeModel node, _) = EditorWithLibrary();
        state.SetChoice(node.Id, En12900Library.ModelKey, "Acme A-1"); // 50 Hz data only
        state.SetChoice(node.Id, En12900Library.NetFrequencyKey, "60");

        ValidationIssue issue = state.IssuesFor(node.Id).Single(i => i.ParameterKey == En12900Library.ModelKey);
        Assert.That(issue.Severity, Is.EqualTo(IssueSeverity.Error));
        Assert.That(issue.Message, Does.Contain("Acme A-1 has no fixed-speed data for 60 Hz net frequency"));
    }

    [Test]
    public void SpeedOptions_FollowTheModel()
    {
        const string fixedOnly =
            """
            { "name": "F", "dischargeVariable": "pressure", "nominalSuperheat": 10, "variants": [
              { "speed": "fixed", "netFrequency": 50, "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [1,0,0,0,0,0,0,0,0,0] } ] }
            """;
        (EditorState state, NodeModel node, _) = EditorWithLibrary(("fixed.json", fixedOnly));
        state.SetChoice(node.Id, En12900Library.ModelKey, "F");
        node = state.Document.FindNode(node.Id)!;

        ParameterDescriptor speed = state.Catalog.Get(node.TypeId).FindParameter(BuiltInComponents.SpeedControlKey)!;
        Assert.That(speed.OptionsShown(node, state.Document, state.Catalog).Select(o => o.Value), Is.EqualTo(new[] { "fixed" }));
    }

    [Test]
    public void ParallelCompressors_KeepTheirOwnModels()
    {
        const string kilowatts =
            """
            { "name": "K", "dischargeVariable": "pressure", "nominalSuperheat": 10, "massFlowUnit": "kg/s", "powerUnit": "kW",
              "massFlow": [1,0,0,0,0,0,0,0,0,0], "power": [5,0,0,0,0,0,0,0,0,0] }
            """;
        (EditorState state, NodeModel node, _) = EditorWithLibrary(("kw.json", kilowatts));
        state.SetChoice(node.Id, En12900Library.ModelKey, "Acme A-1"); // kg/h and W
        state.AddGroupItem(node.Id, "compressors");
        state.SetChoice(node.Id, "model#2", "K");                      // kg/s and kW
        node = state.Document.FindNode(node.Id)!;

        Assert.That(state.IssuesFor(node.Id).Where(i => i.Severity == IssueSeverity.Error), Is.Empty);
        En12900Compressor compressor = BuildCompressor(state.Document, node);
        Assert.That(compressor.Maps[0].EvaluateMassFlow(0, 0), Is.EqualTo(1).Within(1e-12), "3600 kg/h");
        Assert.That(compressor.Maps[0].EvaluatePower(0, 1), Is.EqualTo(5.02).Within(1e-12), "5000 W + 20 W/bar");
        Assert.That(compressor.Maps[1].EvaluatePower(0, 0), Is.EqualTo(5).Within(1e-12));

        state.RemoveGroupItem(node.Id, "compressors", 0);
        Assert.That(En12900Library.Embedded(state.Document.FindNode(node.Id)!, 0)!.Name, Is.EqualTo("K"));
    }

    [Test]
    public void ChangedModels_GiveWarningsAndReloadUpdatesThem()
    {
        (EditorState state, NodeModel node, CompressorLibrary library) = EditorWithLibrary();
        state.SetChoice(node.Id, En12900Library.ModelKey, "Acme A-1");

        File.WriteAllText(Path.Combine(_directory, "acme.json"), TwoLegacyModels.Replace("[5000, 10, 20", "[6000, 10, 20"));
        library.Reload();
        Assert.That(state.IssuesFor(node.Id).Single(i => i.ParameterKey == En12900Library.ModelKey).Message,
            Does.Contain("differs from library model Acme A-1"));

        ParameterDescriptor model = state.Catalog.Get(node.TypeId).FindParameter(En12900Library.ModelKey)!;
        Assert.That(model.Action!.IsAvailable(state.Document.FindNode(node.Id)!, En12900Library.ModelKey), Is.True);
        state.RunParameterAction(node.Id, En12900Library.ModelKey);
        Assert.That(state.IssuesFor(node.Id).Where(i => i.ParameterKey == En12900Library.ModelKey), Is.Empty);
        Assert.That(En12900Library.Embedded(state.Document.FindNode(node.Id)!, 0)!.Variants[0].Points[0].Power[0], Is.EqualTo(6000));

        // Removed from the library: the stored data is used without a warning; the choice is marked.
        File.Delete(Path.Combine(_directory, "acme.json"));
        library.Reload();
        Assert.That(state.IssuesFor(node.Id).Where(i => i.ParameterKey == En12900Library.ModelKey), Is.Empty);
        Assert.That(model.OptionsShown(state.Document.FindNode(node.Id)!, En12900Library.ModelKey, state.Document, state.Catalog)
            .Select(o => o.Label), Has.Some.EqualTo("Acme A-1 (not in library)"));
    }

    /// <summary>Turns a compressor node back into the format-2 shape: polynomials as parameters.</summary>
    private static string ToFormat2(SystemDocument document, NodeModel node, double nominalFrequency)
    {
        CompressorPoint example = En12900Library.ExampleCompressor.Variants[0].Points[0];
        node.Texts.Clear();
        node.Choices.Remove(En12900Library.ModelKey);
        node.Choices.Remove(En12900Library.NetFrequencyKey);
        node.Numbers.Remove(En12900Library.NetVoltageKey);
        node.Choices["massFlowUnit"] = nameof(En12900MassFlowUnit.KilogramsPerHour);
        node.Choices["powerUnit"] = nameof(En12900PowerUnit.Watts);
        node.Choices["dischargeVariable"] = nameof(En12900DischargeVariable.Pressure);
        node.Numbers["nominalSuperheat"] = 10;
        node.Arrays["massFlowPolynomial"] = example.MassFlow;
        node.Arrays["powerPolynomial"] = example.Power;
        node.Numbers["nominalFrequency"] = nominalFrequency;
        document.Version = 2;
        return DocumentSerializer.Serialize(document);
    }

    [Test]
    public async Task Format2Documents_EmbedTheirPolynomialsAndSolveTheSame()
    {
        SystemDocument current = ShippedExamples.EjectorHeatPump();
        SolveResult expected = await new SolveService(ComponentCatalog.Default).SolveAsync(current);
        Assert.That(expected.Success, Is.True, expected.Message);

        SystemDocument document = ShippedExamples.EjectorHeatPump();
        NodeModel node = document.Nodes.Single(n => n.Name == "Comp1");
        document = DocumentSerializer.Deserialize(ToFormat2(document, node, 50));
        node = document.Nodes.Single(n => n.Id == node.Id);

        Assert.That(document.Version, Is.EqualTo(SystemDocument.CurrentVersion));
        Assert.That(node.Arrays, Is.Empty);
        Assert.That(node.Choices.ContainsKey("massFlowUnit"), Is.False);
        Assert.That(En12900Library.Embedded(node, 0)!.Name, Is.EqualTo("Custom compressor (from older file)"));
        Assert.That(new ModelBuilder(ComponentCatalog.Default).Validate(document)
            .Where(i => i.Severity == IssueSeverity.Error), Is.Empty);

        SolveResult result = await new SolveService(ComponentCatalog.Default).SolveAsync(document);
        Assert.That(result.Success, Is.True, result.Message);
        string expectedId = current.Nodes.Single(n => n.Name == "Comp1").Id;
        double Power(SolveResult r, string id) => r.Outputs[id].Single(o => o.Label == "Power").Value!.Value;
        Assert.That(Power(result, node.Id), Is.EqualTo(Power(expected, expectedId)).Within(1e-6 * Power(expected, expectedId)));
    }

    [Test]
    public void Format2VariableSpeedAt60Hz_StillRunsAtItsNominalFrequency()
    {
        SystemDocument document = ShippedExamples.EjectorHeatPump();
        NodeModel node = document.Nodes.Single(n => n.Name == "Comp1");
        node.Choices[BuiltInComponents.SpeedControlKey] = "variable";
        document = DocumentSerializer.Deserialize(ToFormat2(document, node, 60));
        node = document.Nodes.Single(n => n.Id == node.Id);

        Assert.That(node.Choices[En12900Library.NetFrequencyKey], Is.EqualTo("60"));
        En12900CompressorMap map = BuildCompressor(document, node).Maps.Single();
        Assert.That(map.RunningFrequency(), Is.EqualTo(60));
        Assert.That(map.EvaluateMassFlow(-5, 90), Is.EqualTo(map.MassFlowPolynomial.Evaluate(-5, 90)).Within(1e-12));
    }

    private static En12900Compressor BuildCompressor(SystemDocument document, NodeModel node)
    {
        ComponentCatalog catalog = ComponentCatalog.Default;
        BuildResult build = new ModelBuilder(catalog).Build(document);
        Assert.That(build.Success, Is.True, string.Join("; ", build.Issues.Select(i => i.Message)));
        return (En12900Compressor)build.ComponentsByNode[node.Id];
    }

    private (EditorState, NodeModel, CompressorLibrary) EditorWithLibrary(params (string Name, string Json)[] extra)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "acme.json"), TwoLegacyModels);
        File.WriteAllText(Path.Combine(_directory, "variants.json"), VariantModel);
        foreach ((string name, string json) in extra)
            File.WriteAllText(Path.Combine(_directory, name), json);
        CompressorLibrary library = new(_directory);
        ComponentCatalog catalog = ComponentCatalog.WithLibrary(library);
        EditorState state = new(catalog, new SolveService(catalog));
        state.Load(ShippedExamples.EjectorHeatPump());
        return (state, state.Document.Nodes.Single(n => n.Name == "Comp1"), library);
    }
}
