# AGENTS.md — AI Coding Agent Instructions for DEM Cleaner

This file is the primary operational reference for AI coding agents working in the **DEM Cleaner** repository. Read this file first before investigating or modifying code. For deep technical details on data structures, algorithms, and pipelines, consult [ARCHITECTURE.md](file:///C:/Users/redla/repositories/DEM%20Cleaner/ARCHITECTURE.md). For build, run, and debugging procedures, consult [DEVELOPMENT.md](file:///C:/Users/redla/repositories/DEM%20Cleaner/DEVELOPMENT.md). For model tier selection and escalation rules, consult [AI_WORKFLOW.md](file:///C:/Users/redla/repositories/DEM%20Cleaner/AI_WORKFLOW.md).

---

## 1. Project Overview

**DEM Cleaner** (by Sipp Studio) is a Windows desktop application (`.NET 8.0` / `WPF`) for inspecting, cleaning, stitching, previewing (2D & GPU 3D), cropping, resampling, and exporting Digital Elevation Model (DEM) GeoTIFF datasets.

Core capabilities already implemented:
- Importing single or multi-tile `.tif`/`.tiff` DEM datasets (scanline or tiled; 8/16/32/64-bit integer or IEEE float).
- Automatic 2D mosaic grid arrangement from USGS 1m filenames (`x##y###`), generic `x/y` or `r/c` filenames, or GeoTIFF `ModelTiepointTag` metadata.
- Detecting NoData/void pixels via `GDAL_NODATA` (tag `42113`), `NaN`/`Infinity`, extreme float sentinels (`<= -1e30f`), and configurable custom thresholds (`<=`, `==`, `>=`).
- Filling void pixels per-tile or globally using Minimum, Maximum, Mean, or multi-pass wavefront + Gauss-Seidel SOR Laplace interpolation, with optional per-tile fill overrides.
- Smoothing elevation seams across adjacent tiles (C0 smoothstep jump feathering + C1 cross-boundary Laplacian relaxation).
- Interactive 2D mosaic viewport (zoom, pan, custom square export area selection, center translation gizmo, scale bar) and Before/After tile inspector.
- Hardware-accelerated 3D heightmap visualizer (`Viewport3D`, 1K–16K resolution tiers, 4 color ramps, flat-water shader, quad wireframe, sun azimuth, Z-scale).
- Exporting single merged images, source grid layouts, or custom $N \times N$ grids with bilinear resampling and game-engine resolution presets (Power-of-Two, Unreal Engine Landscape, Unity Terrain) to 16-bit normalized PNG, 32-bit float GeoTIFF, or 16-bit normalized GeoTIFF.

---

## 2. High-Level Architecture & Key Files

The codebase uses a **WPF Code-Behind + Static Services + Observable Model** pattern in a single project ([DemCleaner.csproj](file:///C:/Users/redla/repositories/DEM%20Cleaner/DemCleaner.csproj)):

- **[App.xaml](file:///C:/Users/redla/repositories/DEM%20Cleaner/App.xaml) / [App.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/App.xaml.cs)**: Application entry point and `--self-test` CLI runner (`RunSelfTest`).
- **[MainWindow.xaml](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml) / [MainWindow.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs)**: Main window layout, dynamic theme resource keys, UI state, viewport interactions (2D mosaic overlay canvas, 3D camera orbit/pan/zoom), and async workflow orchestration.
- **[Models/DemTile.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/Models/DemTile.cs)**: Data models (`DemTile`, `DemProcessingOptions`, `ExpectedOutputFileItem`) and enums (`FillMethod`, `TileFillModeOverride`, `NoDataComparison`).
- **[Services/TiffDemReader.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/TiffDemReader.cs)**: LibTiff.NET custom tag registration, scanline/tiled TIFF decoding, filename coordinate parsing, grid arrangement (`ArrangeTilesInGrid`), and WGS84 center calculation (`TryGetGridCenterLatLon`).
- **[Services/DemProcessor.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs)**: Pure compute and asset generation engine (void analysis, void filling, SOR Laplace interpolation, inter-tile seam smoothing, `MosaicDescriptor` stitching, bilinear resampling, PNG/GeoTIFF export, 2D preview bitmaps, and 3D mesh/texture construction).
- **[Services/UtmCoordinateConverter.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/UtmCoordinateConverter.cs)**: UTM Easting/Northing to WGS84 Latitude/Longitude conversion and EPSG PCS code parsing.
- **[Services/ThemeManager.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/ThemeManager.cs)**: Defines `AppThemePalette` and 6 earthy/dark UI themes applied at runtime via `ResourceDictionary`.

---

## 3. Coding Standards & Conventions

- **Language & Runtime**: C# 12 on `net8.0-windows` with `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`. Do not introduce nullable compiler warnings.
- **Namespaces**: File-scoped namespaces (`namespace DemCleaner;`, `namespace DemCleaner.Models;`, `namespace DemCleaner.Services;`).
- **Naming**:
  - Private fields in `MainWindow`: `_camelCase` (e.g., `_tiles`, `_cts`, `_isBusy`).
  - XAML control names: Prefixed PascalCase (`Btn*`, `Txt*`, `Chk*`, `Rb*`, `Cmb*`, `Sld*`, `Dg*`, `Bdr*`, `Grd*`, `Pnl*`, `Sv*`).
- **Immutability for DTOs**: Use `record` or `readonly record struct` for value carriers (`AppThemePalette`, `Heightmap3DAssets`, `ExportAreaStats`, `ExpectedOutputFileItem`).
- **Locale-Independent Parsing/Formatting**: Always use `CultureInfo.InvariantCulture` when parsing or formatting numeric strings in UI inputs and TIFF tags (e.g., `GDAL_NODATA`, elevation thresholds, Z-scale ratios, lat/lon URLs).
- **Comments**: Preserve existing XML docstrings and section banners.

---

## 4. Important Architectural Constraints & Guardrails

1. **CRITICAL — Debug PostBuild Auto-Launches the GUI**:
   - [DemCleaner.csproj](file:///C:/Users/redla/repositories/DEM%20Cleaner/DemCleaner.csproj#L23-L25) defines a `PostBuild` target that executes `if "$(ConfigurationName)" == "Debug" start "" "$(TargetPath)"`.
   - Running `dotnet build` with default (`Debug`) configuration will launch a live `DemCleaner.exe` window on the user's desktop and may lock the binary on subsequent builds.
   - **Rule for Agents**: Always verify compilation using Release configuration (`dotnet build -c Release`) or disable post-build execution unless explicitly testing the Debug launch behavior.
2. **WPF Cross-Thread Freezable Rule**:
   - Any `BitmapSource`, `WriteableBitmap`, `MeshGeometry3D`, `Brush`, or `Material` created inside `Task.Run(...)` or `Parallel.For(...)` **must** call `.Freeze()` before returning to the UI thread.
3. **UI Guard Flags**:
   - Check `_isInitializing` and `_isBusy` in UI event handlers before triggering processing pipelines.
   - Use `_isSyncingSelectedTileCombo` and `_isSyncingExportResolutionBoxes` when programmatically updating linked UI controls to prevent recursive event loops.
4. **Preserve `BaseFilledData` vs. `FilledData` Separation**:
   - `DemTile.RawData` is immutable after load.
   - `DemTile.BaseFilledData` stores void-filled elevations *before* inter-tile seam smoothing.
   - `DemTile.FilledData` stores the final elevations *after* `ApplyTileSeamSmoothing`. Always restore `FilledData` from `BaseFilledData` at the start of seam smoothing so smoothing steps are non-destructive and repeatable.
5. **LibTiff Tag Registration**:
   - Always call `TiffDemReader.EnsureTagsRegistered()` before opening a `Tiff` handle for reading or writing custom GeoTIFF/GDAL tags (`42113`, `33550`, `33922`, `34735`, `34737`).
6. **Proprietary License**:
   - Respect [LICENSE.txt](file:///C:/Users/redla/repositories/DEM%20Cleaner/LICENSE.txt). Do not remove Sipp Studio branding or license tooltip loading in [MainWindow.xaml](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml) / [MainWindow.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs).

---

## 5. Preferred Implementation Patterns

- **Async Background Pipelines with Cancellation**:
  - Long-running operations (TIFF loading, void filling, seam smoothing, 3D mesh/texture building, file export) must run on background threads via `Task.Run(..., token)` and check `cancellationToken.ThrowIfCancellationRequested()` inside inner loops.
  - Use `_cts` for primary data processing/export tasks (toggled with `SetBusy(true/false, ...)`), and `_cts3D` independently for 3D viewport rebuilds so rapid 3D option changes cancel superseded 3D builds without interrupting data state.
- **CPU Throttling in Parallel Loops**:
  - When using `Parallel.For` in `DemProcessor` (such as 3D texture and heightmap generation), constrain `MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)` so the Windows Desktop Window Manager (DWM) and UI thread remain responsive.
- **Dynamic Theme Resources**:
  - Never hardcode panel or text colors in XAML where a theme key applies. Bind to `{DynamicResource Theme*}` keys defined in [MainWindow.xaml](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml#L20-L34) and updated by [`ThemeManager.ApplyTheme`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/ThemeManager.cs#L144-L161).

---

## 6. Dependency Management Guidelines

- The project intentionally maintains a minimal dependency footprint: only `BitMiracle.LibTiff.NET` (`2.4.660`) on top of the `.NET 8.0` / `WPF` base class libraries.
- **Do not add new NuGet packages** (such as MVVM frameworks, third-party UI toolkits, or native GDAL wrappers) without explicit user approval. Native GIS dependencies significantly complicate Windows distribution.

---

## 7. Error Handling & Performance Expectations

- **Error Handling**:
  - Catch `OperationCanceledException` separately in all async UI workflows, restore consistent UI state, and call `SetBusy(false, "... stopped by user.")` without showing an error modal.
  - Catch unexpected exceptions at the top-level UI workflow boundary, display a clear `MessageBox` with `MessageBoxImage.Error`, and reset busy state in `catch`/`finally`.
  - Malformed optional GeoTIFF metadata tags in `TiffDemReader` should fail gracefully (returning `null`) rather than aborting tile import.
- **Performance & Memory**:
  - DEM tiles can be very large (e.g., $10{,}000 \times 10{,}000$ float pixels $\approx 400\text{ MB}$ per `float[]` buffer). Avoid unnecessary full-grid allocations.
  - Query multi-tile regions virtually through `MosaicDescriptor` and `SampleMosaicPixel` rather than allocating a single giant stitched `float[]` for the entire mosaic.
  - Debounce high-frequency UI sliders (e.g., `_waterSliderDebounceTimer` at 140 ms) and throttle 3D camera mouse-move updates (`RequestThrottled3DCameraUpdate` at ~30 FPS / 33 ms).

---

## 8. Testing, Verification & Git Expectations

- **Build Verification**:
  - Run `dotnet build -c Release` to verify zero errors and zero warnings without triggering the Debug `PostBuild` GUI launch.
- **Self-Test Execution**:
  - The app includes a built-in CLI self-test mode (`--self-test` in [App.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/App.xaml.cs#L16-L44)). Run `./bin/Release/net8.0-windows/DemCleaner.exe --self-test` and inspect `bin/Release/net8.0-windows/selftest_result.txt`.
- **Rules for Modifying Existing Systems**:
  - Read the relevant section of [MainWindow.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs) or [Services/DemProcessor.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs) before editing; do not guess control names or method signatures.
  - When changing export options, resolution presets, or naming rules, ensure both `UpdateExpectedOutputFilesList()` (preview table) and `ExportCustomAreaSetAsync()` (actual file export) stay synchronized.
- **Git Usage**:
  - Do not automatically create commits, amend commits, or push unless requested by the user.
  - Preserve uncommitted user changes in the working tree.
  - Do not commit build outputs (`bin/`, `obj/`), `.vs/` state, `selftest_result.txt`, or exported DEM/PNG datasets.
- **Pre-Completion Review Checklist**:
  1. `dotnet build -c Release` succeeds cleanly.
  2. `git diff` contains only minimal, targeted changes with no unintended formatting churn.
  3. If architecture, build steps, or workflows changed, update [ARCHITECTURE.md](file:///C:/Users/redla/repositories/DEM%20Cleaner/ARCHITECTURE.md) or [DEVELOPMENT.md](file:///C:/Users/redla/repositories/DEM%20Cleaner/DEVELOPMENT.md).

