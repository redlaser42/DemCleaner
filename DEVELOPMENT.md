# DEVELOPMENT.md — Practical Development Guide for DEM Cleaner

This guide provides instructions for setting up, building, running, debugging, testing, and preparing releases of **DEM Cleaner**.

---

## 1. Development Environment & Prerequisites

- **Operating System**: Windows 10 or Windows 11 (required for `net8.0-windows` and WPF Direct3D 9 hardware acceleration).
- **SDK**: .NET 8.0 SDK (with Windows Desktop workload).
- **IDE**: Visual Studio 2022 / 18+ (supports [DemCleaner.slnx](DemCleaner.slnx)), VS Code with C# Dev Kit, or JetBrains Rider.
- **Dependencies**: Managed NuGet package `BitMiracle.LibTiff.NET` (2.4.660), restored automatically via `dotnet restore` or `dotnet build`.

---

## 2. Build Instructions

### Compilation Verification (Recommended for AI Agents & Unattended Builds)
Always compile in the **Release** configuration when verifying code:

```powershell
dotnet build -c Release
```

> [!NOTE]
> **Known Build Blocker (`CS7064`)**: In the current repository state, `DemCleaner.csproj` references `<ApplicationIcon>icon2.ico</ApplicationIcon>` at the root, while the icon file is stored at `images/icon2.ico`. Updating `DemCleaner.csproj` to reference `images\icon2.ico` (or placing `icon2.ico` in the root) is required for successful compilation.

### The Debug PostBuild Auto-Launch Mechanism
[DemCleaner.csproj](DemCleaner.csproj) contains a `PostBuild` target:
```xml
<Target Name="PostBuild" AfterTargets="PostBuildEvent">
  <Exec Command="if &quot;$(ConfigurationName)&quot; == &quot;Debug&quot; start &quot;&quot; &quot;$(TargetPath)&quot;" />
</Target>
```
- **Why this exists**: Convenient for single-click interactive local development in Visual Studio.
- **Why it matters for automated tools and AI agents**: Running `dotnet build` without flags uses `Debug`, which automatically spawns a live GUI instance of `DemCleaner.exe`. If left open, subsequent builds will fail with file-lock errors on the executable.
- **Future recommendation**: In a future codebase update, this post-build launch hook should be separated from the compilation target (e.g., into a Visual Studio launch profile or dedicated run script). Do not modify the build configuration as part of documentation tasks.

---

## 3. Running the Application

### Run from Release Build
```powershell
./bin/Release/net8.0-windows/DemCleaner.exe
```

### Run without Re-triggering Build
```powershell
dotnet run -c Release --no-build
```

### Asset Lookup
At startup, `MainWindow.LoadLicenseTooltipText` locates [LICENSE.txt](LICENSE.txt) in the application directory (copied by MSBuild via `CopyToOutputDirectory`). Export outputs default to `ExportedPngs/` inside the application base directory unless modified in the UI.

---

## 4. Testing & Validation Architecture

### Current Testing Tiers

| Tier | Status | Command / Procedure | Scope & Limitations |
| :--- | :--- | :--- | :--- |
| **Compilation Verification** | Available | `dotnet build -c Release` | Confirms syntax, type safety, and zero compiler warnings. |
| **CLI Smoke Test** | Available | `./bin/Release/net8.0-windows/DemCleaner.exe --self-test` | Headless check verifying `DemProcessingOptions.FillMethod == Minimum` and `ThemeManager.Themes.Count == 6`. **Does not test numerical algorithms, GeoTIFF I/O, or export.** |
| **Automated Unit Tests** | None | Not yet implemented | No unit test project currently exists in the solution. |
| **Manual Functional Testing** | Available | Interactive GUI execution | Manual verification of file drop, 2D viewport, 3D visualizer, and export. |

### Recommended Numerical Regression Testing Strategy

Because `--self-test` only checks basic defaults, automated regression testing should be introduced using small synthetic float DEM arrays (e.g., $4 \times 4$ or $16 \times 16$ float matrices with known step edges and NoData holes).

The recommended test coverage should validate:

1. **NoData / Void Detection (`DemProcessor.IsVoidPixel`)**:
   - Sentinel extrema ($\le -1e30f$ and $\ge 1e30f$).
   - IEEE non-finite values (`float.NaN`, `float.PositiveInfinity`, `float.NegativeInfinity`).
   - GDAL NoData tag values within floating-point tolerance $\pm 1e-4$.
   - Custom threshold comparisons (`<=`, `==`, `>=`).
2. **Void Filling (`DemProcessor.FillTileVoids`)**:
   - Minimum, Maximum, and Mean constant fills using per-tile vs. dataset-wide statistics.
   - Wavefront seeding and Gauss-Seidel SOR Laplace relaxation on synthetic hollow grids (verifying smooth gradients and early convergence).
3. **Cross-Tile Seam Smoothing (`DemProcessor.ApplyTileSeamSmoothing`)**:
   - Applying step jumps between adjacent synthetic tiles and verifying that smoothstep feathering eliminates the boundary discontinuity.
   - Verifying that smoothing is non-destructive (calling `ApplyTileSeamSmoothing` with 0 steps restores the original `BaseFilledData`).
4. **Bilinear Resampling (`DemProcessor.ExtractResampledSubTile`)**:
   - Resampling a known linear ramp to $2\times$ and $0.5\times$ resolution, verifying conservation of minimum and maximum bounds.
5. **GeoTIFF I/O (`TiffDemReader` and `DemProcessor`)**:
   - Round-trip reading and writing of 16-bit integer, 32-bit float, and tiled GeoTIFFs, confirming that metadata tags (`42113`, `33550`, `33922`, `34735`) are preserved.
6. **Grid Arrangement (`TiffDemReader.ArrangeTilesInGrid`)**:
   - Parsing USGS 1m filenames and ensuring geographic Y (North) coordinates map to row 0.
7. **Elevation Normalization (`DemProcessor.ExportNormalized16BitPng`)**:
   - Verifying that minimum elevation maps to 0 and maximum elevation maps to 65535 in Gray16 PNG outputs.
8. **Asynchronous Cancellation**:
   - Throwing `OperationCanceledException` mid-pipeline and confirming that UI state is cleanly restored without corrupted buffers.

---

## 5. Release Preparation Checklist

Before publishing or distributing a new version of DEM Cleaner:

- [ ] **Clean Release Build**: Build cleanly using `dotnet build -c Release` with zero warnings and zero errors.
- [ ] **Self-Test Execution**: Run `--self-test` and verify that `selftest_result.txt` reports success.
- [ ] **Representative Dataset Testing**: Manually import a multi-tile DEM set (e.g., USGS 1m tiles and non-georeferenced tiles):
  - [ ] Automatic grid arrangement accurately positions tiles.
  - [ ] 2D mosaic viewport pan, zoom, and crop gizmo operate smoothly.
  - [ ] Before/After tile inspector displays expected differences.
  - [ ] 3D visualizer renders at all resolution tiers (1K through 16K) without GPU driver crashes.
- [ ] **Export Verification**:
  - [ ] Export 16-bit normalized PNG and inspect in an external viewer (Gaea, Photoshop).
  - [ ] Export 32-bit float GeoTIFF and inspect in GIS software (QGIS) to verify real-world elevation values.
  - [ ] Verify that the files match the preview in the **Expected Output Files** table.
- [ ] **Dependency Audit**: Ensure `BitMiracle.LibTiff.NET.dll` and [LICENSE.txt](LICENSE.txt) are present in the output folder.
- [ ] **Repository Hygiene**: Run `git status` to ensure no temporary test TIFFs, PNGs, `selftest_result.txt`, or `.vs/` directories are tracked.
- [ ] **Clean Environment Test**: Launch `DemCleaner.exe` on a clean Windows machine or VM with only the standard .NET 8 Desktop Runtime installed.

---

## 6. Debugging Guidance

- **Void Detection Issues**: Switch to the **Individual Tile Inspector** tab (`RbViewInspector`) or enable **Highlight Voids in Red** (`ChkHighlightVoidsInRed`). Void pixels are rendered in coral-red (`R=245, G=60, B=85`) by `DemProcessor.RenderPreviewBitmap`.
- **Tile Grid Placement**: Set breakpoints in `TiffDemReader.ParseFilenameMetadata` and `TiffDemReader.ArrangeTilesInGrid` to check which naming pattern or tiepoint rule was matched.
- **3D Viewport Freezable Exceptions**: If WPF throws an `InvalidOperationException` stating a DependencySource was created on a different thread, verify that all `BitmapSource`, `Brush`, and `MeshGeometry3D` instances created inside `Task.Run` call `.Freeze()` before passing to UI controls.
- **UI Event Loops**: programmatic updates to linked controls must be guarded with `_isSyncingSelectedTileCombo` and `_isSyncingExportResolutionBoxes`.

---

## 7. Git Workflow

- **Branching**: Work on `master` or feature branches.
- **Untracked Artifacts**: Build outputs (`bin/`, `obj/`), user settings (`.vs/`), exported images (`ExportedPngs/`), and self-test logs (`selftest_result.txt`) are excluded by [.gitignore](.gitignore).
- **Commit Safety**: Verify `dotnet build -c Release` passes before committing. Do not commit large test elevation files to the repository.
