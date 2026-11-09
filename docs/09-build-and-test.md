# 9 · Build and Test

> **Summary.** Everything builds with the .NET 10 SDK from `CycleCalculatorUi.sln`. Run the web host or the
> desktop host with `dotnet run`. The UI tests (NUnit, `dotnet test`) exercise Core only; the backend tests
> use the Microsoft Testing Platform runner (`dotnet run`). `publish-linux.sh` packages the desktop app as a
> self-contained tarball with a per-user installer.

**Contents**
- [Prerequisites](#prerequisites)
- [Build and run](#build-and-run)
- [Tests](#tests)
- [UI test suite](#ui-test-suite)
- [Desktop packaging (Linux)](#desktop-packaging-linux)
- [Troubleshooting](#troubleshooting)

---

## Prerequisites

- .NET 10 SDK
- Submodule checked out: `git submodule update --init`
- Linux desktop host: `webkit2gtk-4.1`
- CoolProp native library: shipped in the submodule for Windows x64 and Linux x64 only

## Build and run

```bash
dotnet build CycleCalculatorUi.sln

# Web host (Blazor Server) — http://localhost:5098 / https://localhost:7078
dotnet run --project CycleCalculatorUi.Web

# Desktop host (Photino window)
dotnet run --project CycleCalculatorUi.Desktop
```

Web configuration (appsettings or environment variables):

| Key | Default | Purpose |
|-----|---------|---------|
| `CompressorLibrary:Directory` | shipped `Compressors/` | EN 12900 model folder |
| `Examples:Directory` | shipped `Examples/` | Example system folder |

`appsettings.Development.json` enables `DetailedErrors`.

## Tests

```bash
# UI tests (NUnit, VSTest adapter)
dotnet test CycleCalculatorUi.Tests
dotnet test CycleCalculatorUi.Tests --filter "FullyQualifiedName~StudyTests"

# Backend tests (Microsoft Testing Platform — use dotnet run, not dotnet test)
dotnet run --project CycleCalculator/CycleCalculator.Tests
```

Backend test layout: [backend: testing and extending](../CycleCalculator/docs/06-testing-and-extending.md).

## UI test suite

`CycleCalculatorUi.Tests` references **Core only** — there are no Razor or JavaScript tests, which is one
reason all logic lives in Core.

| File | Covers |
|------|--------|
| `ShippedExamples.cs` | Helper: loads the shipped examples by title |
| `DocumentTests.cs` | Examples validate; empty document; unconnected ports; out-of-range parameters; controlled valve needs a signal; connection rules and direction normalising; fluid circuits and conflicts; serializer round trip, rejection of garbage and newer versions; fingerprint ignores layout; shaped routes; flow-direction sign |
| `SolveTests.cs` | Example matches backend reference values and the same system built directly in the backend; start-value estimate; temperature profiles; volume-flow compressor; residual history and progress; start guesses; cancellation; receiver and ejector examples; invalid / under-specified systems; unconverged last iterate; reversed flow and pressure-rise failures |
| `StudyTests.cs` | Energy balance closes for every example; classification and role override; role/study excluded from fingerprint; grid order; log spacing; warm start = cold start; one bad point fails alone; cancel keeps finished points; invalid setup; file round trip |
| `PhDiagramTests.cs` | Dome and isotherms without a result; states inside the axes; zoomed view; several fluids; unknown fluid reports an error |
| `CompressorLibraryTests.cs` | File parsing (legacy, variants, invalid/duplicate); write and read back; folder watching; shipped copy; first-model default; embedding + undo; variant selection; drift warnings and reload; format-2 conversion |
| `CompressorCapacityControlTests.cs` | Compressors holding suction pressure, pump position, evaporator capacity, heat rejection; related validation |
| `ParallelCompressorTests.cs` | Repeat-group add/remove with undo; per-compressor validation; old documents; identical compressors share flow; operating frequency only for variable speed |
| `PumpTests.cs` | Default pump on max curve; unreachable setpoint; crossed curves; pump into a pipe |
| `SignalControlTests.cs` | Format-1 migration; quantity options follow the source; all controllers offer the same quantities; pump holds a mass flow; three-way valve pressure control |
| `SignalOperatorTests.cs` | Min of inputs; validation; output feeding several inputs; input count follows the operation; chaining; loops are errors |
| `ExampleLibraryTests.cs` | Reads files by name; broken files reported; `Create` returns a copy; reload; missing folder |
| `SecondaryFlowProbe.cs` | `[Explicit]` exploratory probe reading files from `~/Downloads`; not part of the normal run |

## Desktop packaging (Linux)

```bash
./publish-linux.sh            # default runtime linux-x64
```

1. `dotnet publish CycleCalculatorUi.Desktop -c Release -r <rid> --self-contained` into `publish/<rid>/`.
2. Warns if `libCoolPropCsharp.so` is missing (only x64 is shipped).
3. Copies `packaging/linux/install.sh` next to the program.
4. Packs `publish/CycleCalculator-<rid>.tar.gz` with top folder `CycleCalculator/`.

End users unpack and run `./install.sh` — a per-user install without root:

| What | Where |
|------|-------|
| Program | `~/.local/share/cyclecalculator` |
| Command | `~/.local/bin/cyclecalculator` (symlink) |
| Menu entry | `~/.local/share/applications/cyclecalculator.desktop` |

It warns when webkit2gtk-4.1 is missing (with apt/dnf/pacman hints). `./install.sh --uninstall` removes it.

The shipped `Compressors/` and `Examples/` folders are copied into the output by
`CycleCalculatorUi.Core.csproj` on both build and publish.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| Web build fails on missing prune package data | Add `-p:AllowMissingPrunePackageData=true` (the ASP.NET targeting pack is not installed on every machine) |
| Desktop shows an old `diagram.js` / `app.css` | Rebuild — the Desktop csproj copies Shared's `wwwroot` after build |
| `DllNotFoundException: CoolPropCsharp` | Unsupported platform (only win-x64 / linux-x64), or the native library was not copied to the output |
| New executable project picks up test-platform settings | Set `IsTestProject=false` and `GenerateTestingPlatformEntryPoint=false` (the backend references NUnit) |
| Desktop window does not open on Linux | Install `webkit2gtk-4.1` |
