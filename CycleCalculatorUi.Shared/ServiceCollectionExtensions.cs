using CycleCalculatorUi.Core.Catalog;
using CycleCalculatorUi.Core.Solving;
using CycleCalculatorUi.Core.Editing;
using CycleCalculatorUi.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CycleCalculatorUi.Shared;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the services the cycle editor needs. Used by both the web and the desktop host.</summary>
    /// <param name="compressorLibraryDirectory">
    /// Folder of EN 12900 compressor model files; defaults to the collection shipped with the program
    /// (<see cref="CompressorLibrary.ShippedDirectory"/>).
    /// </param>
    public static IServiceCollection AddCycleEditor(this IServiceCollection services, SolveOptions? solveOptions = null,
        string? compressorLibraryDirectory = null)
    {
        services.AddSingleton(_ => new CompressorLibrary(compressorLibraryDirectory ?? CompressorLibrary.ShippedDirectory));
        services.AddSingleton(sp => ComponentCatalog.WithLibrary(sp.GetRequiredService<CompressorLibrary>()));
        services.AddSingleton(sp => new SolveService(sp.GetRequiredService<ComponentCatalog>(), solveOptions));
        // One editor per user session (per circuit on the web, one per window on desktop).
        services.AddScoped<EditorState>();

        // Browser implementations by default; the desktop host registers native ones first.
        services.TryAddScoped<IFileService, BrowserFileService>();
        services.TryAddScoped<IDraftStore, BrowserDraftStore>();
        return services;
    }
}
