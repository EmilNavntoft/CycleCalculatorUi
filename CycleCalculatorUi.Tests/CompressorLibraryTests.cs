using CycleCalculator.CycleModel.Model;
using CycleCalculatorUi.Core.Building;
using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Editing;
using CycleCalculatorUi.Core.Examples;
using CycleCalculatorUi.Core.Solving;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

/// <summary>EN 12900 compressor models read from the library folder and chosen in the editor.</summary>
[TestFixture]
public class CompressorLibraryTests
{
    private static readonly double[] MassFlow = [3600, 36, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly double[] Power = [5000, 10, 20, 0, 0, 0, 0, 0, 0, 0];

    private const string TwoModels =
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

    [Test]
    public void Parse_ModelsInheritTheFileSettings()
    {
        List<string> problems = [];
        IReadOnlyList<CompressorModel> models = CompressorLibrary.Parse(TwoModels, "acme.json", problems);

        Assert.That(problems, Is.Empty);
        Assert.That(models.Select(m => m.Name), Is.EqualTo(new[] { "Acme A-1", "Acme A-2" }));
        Assert.That(models[0].Refrigerant, Is.EqualTo("R744"));
        Assert.That(models[0].MassFlowUnit, Is.EqualTo(En12900MassFlowUnit.KilogramsPerHour));
        Assert.That(models[0].NominalFrequency, Is.EqualTo(50));
        Assert.That(models[0].Current, Is.Null);
        Assert.That(models[1].MassFlowUnit, Is.EqualTo(En12900MassFlowUnit.KilogramsPerSecond));
        Assert.That(models[1].PowerUnit, Is.EqualTo(En12900PowerUnit.Kilowatts));
        Assert.That(models[1].NominalFrequency, Is.EqualTo(60));
        Assert.That(models[1].Current![0], Is.EqualTo(8));
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
    public void Library_CreatesTheFolderWithAReadmeAndReadsNewFiles()
    {
        using CompressorLibrary library = new(_directory);
        Assert.That(File.Exists(Path.Combine(_directory, CompressorLibrary.ReadmeFileName)));
        Assert.That(library.Models, Is.Empty);

        File.WriteAllText(Path.Combine(_directory, "acme.json"), TwoModels);
        Directory.CreateDirectory(Path.Combine(_directory, "other"));
        File.WriteAllText(Path.Combine(_directory, "other", "dup.json"), TwoModels);
        library.Reload();

        Assert.That(library.Models.Select(m => m.Name), Is.EqualTo(new[] { "Acme A-1", "Acme A-2" }));
        Assert.That(library.Problems, Has.Count.EqualTo(2).And.All.Contains("is already used in acme.json"));
    }

    [Test]
    public void ChoosingAModel_CopiesItsValuesAndUndoRestoresTheOldOnes()
    {
        (EditorState state, NodeModel node, _) = EditorWithLibrary();
        double[] before = node.Arrays["massFlowPolynomial"]!;

        state.SetChoice(node.Id, En12900Library.ModelKey, "Acme A-2");
        node = state.Document.FindNode(node.Id)!;

        // The only compressor takes over the model's units, discharge variable and superheat.
        Assert.That(node.Choices["massFlowUnit"], Is.EqualTo(nameof(En12900MassFlowUnit.KilogramsPerSecond)));
        Assert.That(node.Choices["powerUnit"], Is.EqualTo(nameof(En12900PowerUnit.Kilowatts)));
        Assert.That(node.Arrays["massFlowPolynomial"]![0], Is.EqualTo(1));
        Assert.That(node.Arrays["currentPolynomial"]![0], Is.EqualTo(8));
        Assert.That(node.Numbers["nominalFrequency"], Is.EqualTo(60));
        Assert.That(state.IssuesFor(node.Id).Where(i => i.ParameterKey == En12900Library.ModelKey), Is.Empty);

        state.Undo();
        Assert.That(state.Document.FindNode(node.Id)!.Arrays["massFlowPolynomial"], Is.EqualTo(before));
    }

    [Test]
    public void ParallelCompressor_ConvertsTheModelToTheSharedUnits()
    {
        (EditorState state, NodeModel node, _) = EditorWithLibrary();
        state.SetChoice(node.Id, En12900Library.ModelKey, "Acme A-1"); // kg/h and W
        state.AddGroupItem(node.Id, "compressors");
        state.SetChoice(node.Id, "model#2", "Acme A-2"); // kg/s and kW
        node = state.Document.FindNode(node.Id)!;

        Assert.That(node.Choices["massFlowUnit"], Is.EqualTo(nameof(En12900MassFlowUnit.KilogramsPerHour)));
        Assert.That(node.Arrays["massFlowPolynomial#2"]![0], Is.EqualTo(3600).Within(1e-9));
        Assert.That(node.Arrays["powerPolynomial#2"]![0], Is.EqualTo(5000).Within(1e-9));
        Assert.That(node.Arrays["massFlowPolynomial"], Is.EqualTo(MassFlow));
        Assert.That(state.IssuesFor(node.Id).Where(i => i.Severity == IssueSeverity.Error), Is.Empty);
    }

    [Test]
    public void EditedOrMissingModels_GiveWarnings()
    {
        (EditorState state, NodeModel node, CompressorLibrary library) = EditorWithLibrary();
        state.SetChoice(node.Id, En12900Library.ModelKey, "Acme A-1");
        state.SetCoefficients(node.Id, "powerPolynomial", [1, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        Assert.That(state.IssuesFor(node.Id).Single(i => i.ParameterKey == En12900Library.ModelKey).Message,
            Does.Contain("differ from library model Acme A-1"));

        File.Delete(Path.Combine(_directory, "acme.json"));
        library.Reload();
        ValidationIssue missing = state.IssuesFor(node.Id).Single(i => i.ParameterKey == En12900Library.ModelKey);
        Assert.That(missing.Severity, Is.EqualTo(IssueSeverity.Warning));
        Assert.That(missing.Message, Does.Contain("not in the compressor library"));
    }

    private (EditorState, NodeModel, CompressorLibrary) EditorWithLibrary()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "acme.json"), TwoModels);
        CompressorLibrary library = new(_directory);
        ComponentCatalog catalog = ComponentCatalog.WithLibrary(library);
        EditorState state = new(catalog, new SolveService(catalog));
        state.Load(ExampleSystems.EjectorHeatPump());
        return (state, state.Document.Nodes.Single(n => n.Name == "Comp1"), library);
    }
}
