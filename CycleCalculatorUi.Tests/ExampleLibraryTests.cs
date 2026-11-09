using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Examples;
using CycleCalculatorUi.Core.Persistence;
using NUnit.Framework;

namespace CycleCalculatorUi.Tests;

[TestFixture]
public class ExampleLibraryTests
{
    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "examples-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void ReadsFilesByFileName_AndReportsBrokenOnes()
    {
        Write("2 second.cycle.json", new SystemDocument { Name = "Second" });
        Write("1 first.cycle.json", new SystemDocument { Name = "First" });
        File.WriteAllText(Path.Combine(_directory, "broken.cycle.json"), "{ not json");
        File.WriteAllText(Path.Combine(_directory, "notes.json"), "{}");

        using ExampleLibrary library = new(_directory);

        Assert.That(library.Examples.Select(e => e.Title), Is.EqualTo(new[] { "First", "Second" }));
        Assert.That(library.Problems.Single(), Does.StartWith("broken.cycle.json:"));
    }

    [Test]
    public void Create_ReturnsAFreshCopy()
    {
        Write("a.cycle.json", new SystemDocument { Name = "A" });
        using ExampleLibrary library = new(_directory);

        SystemDocument first = library.Examples[0].Create();
        first.Name = "changed";

        Assert.That(library.Examples[0].Create().Name, Is.EqualTo("A"));
    }

    [Test]
    public void Reload_PicksUpNewFiles()
    {
        using ExampleLibrary library = new(_directory);
        Assert.That(library.Examples, Is.Empty);

        Write("a.cycle.json", new SystemDocument { Name = "A" });
        library.Reload();

        Assert.That(library.Examples.Single().Title, Is.EqualTo("A"));
    }

    [Test]
    public void MissingFolder_IsAProblem()
    {
        using ExampleLibrary library = new(Path.Combine(_directory, "missing"));
        Assert.That(library.Examples, Is.Empty);
        Assert.That(library.Problems, Has.Count.EqualTo(1));
    }

    private void Write(string file, SystemDocument document) =>
        File.WriteAllText(Path.Combine(_directory, file), DocumentSerializer.Serialize(document));
}
