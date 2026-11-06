using CycleCalculatorUi.Shared;
using CycleCalculatorUi.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;

namespace CycleCalculatorUi.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        PhotinoBlazorAppBuilder builder = PhotinoBlazorAppBuilder.CreateDefault(args);
        builder.Services.AddScoped<IFileService, DesktopFileService>();
        builder.Services.AddScoped<IDraftStore, DesktopDraftStore>();
        builder.Services.AddCycleEditor();
        builder.RootComponents.Add<Routes>("#app");

        PhotinoBlazorApp app = builder.Build();
        DesktopFileService.Window = app.MainWindow;
        app.MainWindow
            .SetTitle("CycleCalculator")
            .SetUseOsDefaultSize(false)
            .SetSize(1440, 900)
            .Center()
            .SetIconFile(Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.png"));
#if !DEBUG
        // Photino logs every UI message to stdout; keep that for debug builds only.
        app.MainWindow.SetLogVerbosity(0);
#endif

        AppDomain.CurrentDomain.UnhandledException += (_, error) =>
            app.MainWindow.ShowMessage("Unexpected error", error.ExceptionObject.ToString());

        app.Run();
    }
}
