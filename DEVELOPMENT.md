# DEVELOPMENT.md — Practical Development Guide for DEM Cleaner

This guide provides project-specific instructions for building, running, testing, debugging, and extending **DEM Cleaner**.

---

## 1. Development Environment & Prerequisites

- **Operating System**: Windows 10 or Windows 11 (required for `net8.0-windows` and WPF `Viewport3D` Direct3D rendering).
- **SDK**: `.NET 8.0 SDK` (with Windows Desktop workload).
- **IDE**: Visual Studio 2022 / 18+ (supports `.slnx` via [DemCleaner.slnx](file:///C:/Users/redla/repositories/DEM%20Cleaner/DemCleaner.slnx)) or VS Code / JetBrains Rider / Antigravity IDE.
- **NuGet Dependencies**:
  - `BitMiracle.LibTiff.NET` (`2.4.660`) — restored automatically via `dotnet restore` or `dotnet build`.

---

## 2. Building the Project

> [!IMPORTANT]
> **Debug PostBuild Auto-Launch Behavior**
> [DemCleaner.csproj](file:///C:/Users/redla/repositories/DEM%20Cleaner/DemCleaner.csproj#L23-L25) contains a `PostBuild` target:
> ```xml
> <Target Name="PostBuild" AfterTargets="PostBuildEvent">
>   <Exec Command="if &quot;$(ConfigurationName)&quot; == &quot;Debug&quot; start &quot;&quot; &quot;$(TargetPath)&quot;" />
> </Target>
> ```
> Building in `Debug` configuration (`dotnet build` with no flags) **automatically launches a new `DemCleaner.exe` GUI window** after compilation finishes. If that window remains open, subsequent `Debug` builds will fail with file-lock errors on `DemCleaner.exe`.

### Recommended Build Command (CLI & AI Agents)
Always build in `Release` configuration when verifying code compilation from the command line so the GUI is not auto-spawned:

```powershell
dotnet build -c Release
```

### Interactive Debug Build (Launches App Automatically)
If you want to compile and immediately launch the GUI in one step:

```powershell
dotnet build -c Debug
```

---

## 3. Running the Application

### Launch from Release Build
```powershell
./bin/Release/net8.0-windows/DemCleaner.exe
```

### Launch without Re-triggering PostBuild
```powershell
dotnet run -c Release --no-build
```

### Runtime Asset Lookup
At startup, [`MainWindow.LoadLicenseTooltipText()`](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs#L116-L150) searches for [LICENSE.txt](file:///C:/Users/redla/repositories/DEM%20Cleaner/LICENSE.txt) in `AppDomain.CurrentDomain.BaseDirectory`, `Environment.CurrentDirectory`, and `..\..\..`. MSBuild copies `LICENSE.txt` to the output folder automatically. Default exports are directed to `<BaseDirectory>\ExportedPngs` unless changed in the UI.

---

## 4. Executing Tests

There is no separate test project in the solution. Instead, [App.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/App.xaml.cs#L16-L44) implements an in-process CLI self-test flag (`--self-test`) that runs headless, verifies core defaults (`DemProcessingOptions.FillMethod == FillMethod.Minimum` and `ThemeManager.Themes.Count`), writes `selftest_result.txt` to the executable's directory, and shuts down with exit code `0`.

### Run the Self-Test
```powershell
dotnet build -c Release
./bin/Release/net8.0-windows/DemCleaner.exe --self-test
Get-Content ./bin/Release/net8.0-windows/selftest_result.txt
```

Expected output:
```text
SELF-TEST PASSED: Default=Minimum, Themes=6.
```

---

## 5. Debugging Guidance

- **Inspecting Void Detection Issues**:
  - Enable **"Highlight Voids in Red"** (`ChkHighlightVoidsInRed`) in the right-side Viewport Options panel or switch to the **Individual Tile Inspector** tab (`RbViewInspector`). Raw void pixels are rendered in coral-red (`R=245, G=60, B=85`) by [`DemProcessor.RenderPreviewBitmap`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs#L1162-L1232).
  - Check [`DemProcessor.IsVoidPixel`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs#L60-L88) if a specific TIFF sentinel value is not being caught.
- **Debugging Tile Grid Ordering**:
  - Set breakpoints in [`TiffDemReader.ParseFilenameMetadata`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/TiffDemReader.cs#L121-L169) and [`TiffDemReader.ArrangeTilesInGrid`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/TiffDemReader.cs#L392-L453).
  - Verify whether the tiles matched Priority 1 (`ParsedFileX`/`ParsedFileY`), Priority 2 (`GeoOriginX`/`GeoOriginY`), or fell back to Priority 3 (alphabetical square grid).
- **Debugging 3D Viewport Stutter or Memory Spikes**:
  - Check [`DemProcessor.Build3DHeightmapAssets`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs#L1238-L1609). Ensure `throttledParallelOptions` (`MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)`) and `.Freeze()` calls on `writeableBmp`, `terrainMesh`, and `skirtMesh` remain intact.
  - Ensure 3D camera mouse moves go through [`RequestThrottled3DCameraUpdate`](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs#L3213-L3231) (throttled to 33 ms).
- **WPF Binding & Freezable Exceptions**:
  - An `InvalidOperationException` stating *"Must create DependencySource on same Thread as the DependencyObject"* means a `BitmapSource`, `Brush`, or `MeshGeometry3D` created on a worker thread was not frozen via `.Freeze()` before being assigned to a UI control or `DemTile` property.

---

## 6. Guidelines for Extending & Modifying the Codebase

### Introducing Dependencies
- Avoid adding NuGet packages unless strictly necessary and approved by the user.
- Pure C# implementations (like [`UtmCoordinateConverter`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/UtmCoordinateConverter.cs)) are strongly preferred over native C/C++ GIS wrappers (PROJ, GDAL) to keep deployment self-contained.

### Adding a New Color Theme
1. Add a new `AppThemePalette` entry to [`ThemeManager.Themes`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/ThemeManager.cs#L26-L142).
2. `MainWindow` automatically populates `CmbTheme` from `ThemeManager.Themes` at startup—no XAML changes are required unless adding a new brush resource key.

### Adding a New Void Fill Method or Processing Option
1. Update the enums (`FillMethod`, `TileFillModeOverride`) and [`DemProcessingOptions`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Models/DemTile.cs#L8-L42) in `Models/DemTile.cs`, plus `DemTile.ResolveEffectiveFillMethod`.
2. Add the corresponding UI controls in [MainWindow.xaml](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml) (and update `CmbSelectedTileFillOverride` if per-tile override applies).
3. Read the new control state in [`MainWindow.ReadOptionsFromUi()`](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs#L165-L194).
4. Implement the algorithm in [`DemProcessor.FillTileVoids`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs#L169-L256) (respecting `CancellationToken` and writing to `BaseFilledData` and `FilledData`).

### Modifying Export Formats, Layouts, or Naming Rules
- Export logic has **two** touchpoints in [MainWindow.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs) that must be kept in sync:
  1. [`UpdateExpectedOutputFilesList()`](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs#L2275-L2400) — computes the live preview rows in the **Expected Output Files** bottom tab (`DgExpectedOutputs`).
  2. [`ExportCustomAreaSetAsync()`](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs#L2925-L3128) — executes the actual file export loop.

---

## 7. Git Workflow

- **Branch**: `master` tracking `origin/master`.
- **Ignored Artifacts**: Build outputs (`bin/`, `obj/`), Visual Studio cache (`.vs/`), self-test output (`selftest_result.txt`), and default export directories (`ExportedPngs/`, `Exported_16Bit_PNG/`) are ignored via [.gitignore](file:///C:/Users/redla/repositories/DEM%20Cleaner/.gitignore).
- **Commit Hygiene**: Keep commits focused on a single logical change. Verify `dotnet build -c Release` passes before committing, and never commit large test `.tif`/`.tiff` or `.png` datasets to the repository.

