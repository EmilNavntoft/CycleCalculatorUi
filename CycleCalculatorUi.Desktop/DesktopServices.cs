using CycleCalculatorUi.Shared.Services;
using Photino.NET;

namespace CycleCalculatorUi.Desktop;

/// <summary>Native open and save dialogs of the main window.</summary>
public sealed class DesktopFileService : IFileService
{
    /// <summary>Set once the main window exists (the services are created before it).</summary>
    public static PhotinoWindow? Window { get; set; }

    private string? _lastDirectory;

    public bool HasOpenDialog => true;

    public async Task<OpenedFile?> OpenAsync(FileType type)
    {
        if (Window is null)
            return null;
        string[] paths = await Window.ShowOpenFileAsync("Open", _lastDirectory, false, Filters(type));
        if (paths is not [{ Length: > 0 } path])
            return null;
        _lastDirectory = Path.GetDirectoryName(path);
        return new OpenedFile(Path.GetFileName(path), await File.ReadAllTextAsync(path));
    }

    public async Task SaveAsync(string suggestedName, string content, FileType type)
    {
        if (Window is null)
            return;
        string defaultPath = Path.Combine(_lastDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), suggestedName);
        string? path = await Window.ShowSaveFileAsync("Save", defaultPath, Filters(type));
        if (string.IsNullOrEmpty(path))
            return;
        if (!path.EndsWith(type.Extension, StringComparison.OrdinalIgnoreCase))
            path += type.Extension;
        _lastDirectory = Path.GetDirectoryName(path);
        await File.WriteAllTextAsync(path, content);
    }

    private static (string Name, string[] Extensions)[] Filters(FileType type) =>
        [(type.Name, ["*" + type.Extension]), ("All files", ["*.*"])];
}

/// <summary>Keeps the draft in the user's application data folder.</summary>
public sealed class DesktopDraftStore : IDraftStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CycleCalculator", "draft.cycle.json");

    public async Task<string?> LoadAsync() =>
        File.Exists(FilePath) ? await File.ReadAllTextAsync(FilePath) : null;

    public async Task SaveAsync(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // Write to a temporary file first so a crash never leaves a half-written draft.
        string temporary = FilePath + ".tmp";
        await File.WriteAllTextAsync(temporary, content);
        File.Move(temporary, FilePath, overwrite: true);
    }
}
