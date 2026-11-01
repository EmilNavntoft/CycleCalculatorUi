namespace CycleCalculatorUi.Shared.Services;

public sealed record FileType(string Name, string Extension);

public sealed record OpenedFile(string Name, string Content);

/// <summary>
/// Opening and saving files, implemented per host: browser downloads and file pickers on the web,
/// native dialogs on the desktop.
/// </summary>
public interface IFileService
{
    /// <summary>
    /// True when <see cref="OpenAsync"/> shows a native dialog. Otherwise the editor offers a browser
    /// file picker (&lt;InputFile&gt;) instead.
    /// </summary>
    bool HasOpenDialog { get; }

    Task<OpenedFile?> OpenAsync(FileType type);

    /// <summary>Saves text to a file the user chooses (or downloads it in the browser).</summary>
    Task SaveAsync(string suggestedName, string content, FileType type);
}

/// <summary>Keeps an unsaved copy of the open document so it survives a reload or restart.</summary>
public interface IDraftStore
{
    Task<string?> LoadAsync();

    Task SaveAsync(string content);
}
