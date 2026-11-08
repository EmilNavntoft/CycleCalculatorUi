# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A drag-and-drop editor for thermodynamic cycles (refrigeration/heat-pump systems) on top of the CycleCalculator solver. The solver lives in the `CycleCalculator/` git submodule (its own repo, see `CycleCalculator/README.md` for components, units and solver overview). All projects target .NET 10.

## Commands

```bash
dotnet build CycleCalculatorUi.sln

# Web host (Blazor Server)
dotnet run --project CycleCalculatorUi.Web
# Desktop host (Photino, needs webkit2gtk-4.1 on Linux)
dotnet run --project CycleCalculatorUi.Desktop

# UI tests (NUnit, VSTest adapter)
dotnet test CycleCalculatorUi.Tests
dotnet test CycleCalculatorUi.Tests --filter "FullyQualifiedName~StudyTests"

# Backend tests use Microsoft.Testing.Platform (EnableNUnitRunner): use dotnet run, not dotnet test
dotnet run --project CycleCalculator/CycleCalculator.Tests
```

Local quirk: if the web build fails on missing prune package data, add `-p:AllowMissingPrunePackageData=true` (the `aspnet-targeting-pack` isn't installed on this machine).

## Architecture

Project layers (each references the one below):

- **CycleCalculatorUi.Web** / **CycleCalculatorUi.Desktop** — thin hosts. Both call `AddCycleEditor()` (`Shared/ServiceCollectionExtensions.cs`). Desktop registers native `IFileService`/`IDraftStore` before that call; otherwise browser versions are used (`TryAdd`). Web is Blazor Server, not WASM, because CoolProp is native P/Invoke.
- **CycleCalculatorUi.Shared** — Razor UI (`Pages/Editor.razor` is the main page) plus JS modules in `wwwroot/` (`diagram.js` canvas interaction, `phchart.js` p-h diagram, `host.js` file/draft helpers), loaded via `import("./_content/CycleCalculatorUi.Shared/...")`. Photino only serves the output `wwwroot`, so the Desktop csproj copies Shared's static assets into `_content/CycleCalculatorUi.Shared/` after build.
- **CycleCalculatorUi.Core** — no UI dependencies; all logic lives here and is what the tests exercise.
- **CycleCalculator** (submodule) — backend components and solver (`Orchestrator.Solve`).

Key flow in Core:

- `Document/SystemDocument` is the editor's own model (nodes, connections, parameter values as strings). Backend component parameters are constructor args, so the UI never mutates backend objects; `Building/ModelBuilder` rebuilds fresh backend components from the document on every solve, and `Validate` runs cheaply on every edit.
- `Catalog/` describes component types (`ComponentDescriptor`, `ParameterDescriptor`, ports, `EnergyKind`). **Adding a backend component** = one descriptor in `Catalog/BuiltInComponents.cs` + one glyph in `Shared/Components/ComponentGlyph.razor`. Set `EnergyKind` or it won't appear in heat/power totals (`Solving/EnergyBalance.cs`).
- Setpoint controllers (valves, effectiveness HX, compressors, pump) share one parameter set from `SignalControlParameters` in `BuiltInComponents.cs`: a `controlSignal` choice plus one setpoint per quantity in `Catalog/Measurements.cs`. A value a backend component exposes (`CycleComponent.ExposedValues`, e.g. the pump's envelope position) becomes measurable with one `MeasuredQuantity` entry there. The inspector only lists quantities the connected source provides (`ParameterDescriptor.OptionsFor`).
- Signal operators (`SignalOperator`, backend `Model/SignalOperator.cs`) have no fluid ports (`Streams = []`): numbered signal inputs and a `PortKind.SignalOutput`. A controller wired to an output measures `Measurements.OperatorOutput` (backend `Measurement.Signal`); `EditorState.Connect` selects it automatically.
- Repeatable parameter groups (e.g. parallel EN 12900 compressors): item 1 uses plain keys, item n>1 is stored as `key#n`, count in `NodeModel.GroupCounts`. Iterate `ComponentDescriptor.ParameterInstances(node)`, not `Parameters`, when touching stored values. Ports can repeat with a group too (`PortDescriptor.Group`, keys `In`, `In#2`…): use `PortsFor(node)` / `FindPort(node, key)` where the node is known. `RepeatGroupDescriptor.FixedCount` pins the count (e.g. one input for constant operations); `EditorState` drops connections to ports a node no longer has.
- EN 12900 compressor library: `Catalog/CompressorLibrary` reads model `.json` files from a watched folder (default `~/.config/CycleCalculator/compressors`, web: `CompressorLibrary:Directory`; format in its `Readme`) plus the built-in `ExampleModel`. A model has variants per speed (fixed/variable) and net frequency, each with one or more polynomial sets and a `scaling` (linear or interpolated). There is no manual polynomial input: choosing a model (`Catalog/En12900Library`) embeds it whole as JSON (`CompressorLibrary.Write`) in `NodeModel.Texts` under the model key, so documents never depend on the library; the node's `netFrequency` and each compressor's speed pick the variant, and `netVoltage` scales the current. `Check` warns when the embedded data drifts from the library ("Reload from library" is a `ParameterAction`). Format-2 documents with polynomial parameters are converted in `DocumentSerializer.EmbedCompressorModels`. `ComponentCatalog.Default` has an empty library; the hosts use `ComponentCatalog.WithLibrary`.
- `Editing/EditorState` (scoped: one per web circuit / desktop window) is the single entry point for edits — go through `Edit(...)` so undo/redo and `Changed` notifications work. It also owns selection, validation issues and the latest `SolveResult`.
- `Solving/SolveService` runs solves on a background thread with timeout, a concurrency limit (shared web server) and throttled residual-progress callbacks. Results carry a `DocumentSerializer.Fingerprint` to detect staleness; `Study` and `EnergyRole` are deliberately excluded from the fingerprint.
- `Studies/` — parametric studies over a full grid of parameter combinations; `StudyRunner` warm-starts each point from the previous point's port states.
- `Persistence/DocumentSerializer` — `.cycle.json` files. Keep old files loadable.
- `Examples/ExampleSystems` — built-in example systems.

## Gotchas

- The backend csproj references NUnit packages, so the app projects set `IsTestProject=false` / `GenerateTestingPlatformEntryPoint=false`; keep those when adding new executables.
