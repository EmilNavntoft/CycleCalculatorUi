using CycleCalculatorUi.Core.Document;
using CycleCalculatorUi.Core.Persistence;

namespace CycleCalculatorUi.Core.Examples;

/// <summary>An example system read from a <c>.cycle.json</c> file of the <see cref="ExampleLibrary"/>.</summary>
public sealed class ExampleSystem(string title, string file, SystemDocument document)
{
    /// <summary>The document's name.</summary>
    public string Title { get; } = title;

    /// <summary>Path relative to the library folder.</summary>
    public string File { get; } = file;

    /// <summary>A fresh copy of the system, safe to edit.</summary>
    public SystemDocument Create() => DocumentSerializer.Clone(document);
}

/// <summary>
/// Example systems read from the <c>.cycle.json</c> files in a folder, listed by file name. The folder is watched, so
/// files added, changed or removed show up without a restart. Thread-safe: one instance is shared by all editor
/// sessions. The program ships its examples in <see cref="ShippedDirectory"/>.
/// </summary>
public sealed class ExampleLibrary : IDisposable
{
    /// <summary>The folder of examples that ships with the program, next to its assemblies.</summary>
    public static string ShippedDirectory => Path.Combine(AppContext.BaseDirectory, "Examples");

    private const string Pattern = "*" + DocumentSerializer.FileExtension;

    private readonly Lock _lock = new();
    private readonly FileSystemWatcher? _watcher;
    private IReadOnlyList<ExampleSystem> _examples = [];
    private IReadOnlyList<string> _problems = [];
    private bool _dirty = true;

    /// <param name="directory">Folder with the example files, e.g. <see cref="ShippedDirectory"/>.</param>
    public ExampleLibrary(string directory)
    {
        Directory = Path.GetFullPath(directory);
        try
        {
            _watcher = new FileSystemWatcher(Directory, Pattern)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName
            };
            _watcher.Changed += (_, _) => Reload();
            _watcher.Created += (_, _) => Reload();
            _watcher.Deleted += (_, _) => Reload();
            _watcher.Renamed += (_, _) => Reload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Without a watcher the folder is still read; changes then need Reload().
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public string Directory { get; }

    /// <summary>The folder's examples ordered by file name; re-read when the folder changed.</summary>
    public IReadOnlyList<ExampleSystem> Examples
    {
        get
        {
            EnsureLoaded();
            return _examples;
        }
    }

    /// <summary>Files that could not be read, e.g. "broken.cycle.json: The file is not a valid cycle document."</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            EnsureLoaded();
            return _problems;
        }
    }

    /// <summary>The example whose document is named <paramref name="title"/>, or null.</summary>
    public ExampleSystem? Find(string title) => Examples.FirstOrDefault(e => e.Title == title);

    /// <summary>Re-reads the folder on the next access.</summary>
    public void Reload()
    {
        lock (_lock)
            _dirty = true;
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (!_dirty)
                return;
            _dirty = false;
            (_examples, _problems) = Load(Directory);
        }
    }

    private static (IReadOnlyList<ExampleSystem>, IReadOnlyList<string>) Load(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
            return ([], [$"The example folder {directory} does not exist."]);
        string[] files;
        try
        {
            files = System.IO.Directory.GetFiles(directory, Pattern, SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], [$"The example folder cannot be read: {ex.Message}"]);
        }

        List<ExampleSystem> examples = [];
        List<string> problems = [];
        foreach (string path in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            string relative = Path.GetRelativePath(directory, path);
            try
            {
                SystemDocument document = DocumentSerializer.Deserialize(File.ReadAllText(path));
                examples.Add(new ExampleSystem(document.Name, relative, document));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                problems.Add($"{relative}: {ex.Message}");
            }
        }

        return (examples, problems);
    }

    public void Dispose() => _watcher?.Dispose();
}
