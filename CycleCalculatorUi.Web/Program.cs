using CycleCalculatorUi.Web.Components;
using CycleCalculatorUi.Shared;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Editor services shared with the desktop host.
// The EN 12900 compressor library folder can be set with "CompressorLibrary:Directory" (appsettings or environment).
builder.Services.AddCycleEditor(compressorLibraryDirectory: builder.Configuration["CompressorLibrary:Directory"]);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(
        typeof(CycleCalculatorUi.Shared._Imports).Assembly);

app.Run();