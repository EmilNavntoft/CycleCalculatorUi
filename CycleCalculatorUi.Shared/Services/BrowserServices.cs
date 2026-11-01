using Microsoft.JSInterop;

namespace CycleCalculatorUi.Shared.Services;

/// <summary>Saves by downloading; opening goes through the editor's browser file picker.</summary>
public sealed class BrowserFileService(IJSRuntime js) : IFileService
{
    public bool HasOpenDialog => false;

    public Task<OpenedFile?> OpenAsync(FileType type) => Task.FromResult<OpenedFile?>(null);

    public async Task SaveAsync(string suggestedName, string content, FileType type)
    {
        await using IJSObjectReference module = await Host(js);
        await module.InvokeVoidAsync("downloadText", suggestedName, content);
    }

    internal static ValueTask<IJSObjectReference> Host(IJSRuntime js) =>
        js.InvokeAsync<IJSObjectReference>("import", "./_content/CycleCalculatorUi.Shared/host.js");
}

/// <summary>Keeps the draft in the browser's local storage, so it is private to each browser.</summary>
public sealed class BrowserDraftStore(IJSRuntime js) : IDraftStore
{
    private const string Key = "cyclecalculator.draft";

    public async Task<string?> LoadAsync()
    {
        await using IJSObjectReference module = await BrowserFileService.Host(js);
        return await module.InvokeAsync<string?>("loadDraft", Key);
    }

    public async Task SaveAsync(string content)
    {
        await using IJSObjectReference module = await BrowserFileService.Host(js);
        await module.InvokeVoidAsync("saveDraft", Key, content);
    }
}
