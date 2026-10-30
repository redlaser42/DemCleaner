# DEM Cleaner

**Turn raw elevation tiles into clean, continuous terrain heightmaps.**

**DEM Cleaner** is a Windows desktop application by **Sipp Studio** for importing, inspecting, repairing, stitching, previewing, cropping, and exporting digital elevation model (DEM) data. It is designed for terrain workflows involving GIS elevation sources and 3D applications such as **Unreal Engine**, **Unity**, and **Gaea**.

**Windows** · **.NET 8 / WPF** · **GeoTIFF in** · **16-bit PNG or GeoTIFF out**

> **Project status:** Functionality described below is documented in the repository's architecture notes. Distribution packaging and compatibility with individual datasets should be verified for each release.

## At a glance

| Import | Clean & blend | Inspect | Export |
|---|---|---|---|
| Single or multiple DEM GeoTIFF tiles | NoData detection and four filling modes | Interactive 2D mosaic and before/after comparison | 16-bit PNG, 16-bit GeoTIFF, 32-bit float GeoTIFF |
| Scanline and tiled TIFF sources | Per-tile overrides and cross-tile seam smoothing | Interactive 3D terrain preview | Merged, source-tile, or custom grid layouts |

## Features

### 🗺️ Import and arrange elevation tiles

- Import one or more `.tif` / `.tiff` DEM files, including scanline- and tile-organized TIFFs.
- Read supported integer and floating-point elevation sample formats.
- Automatically place tiles into a mosaic using recognized **USGS 1-meter filename coordinates**, generic X/Y or row/column patterns, or GeoTIFF tiepoint metadata.
- Use alphabetical near-square arrangement as a fallback when tile positioning metadata is unavailable.
- Inspect individual tiles and the assembled mosaic.

### 🧹 Detect and repair missing elevations

DEM datasets often contain NoData values, gaps, and invalid elevation samples. DEM Cleaner identifies voids using:

- GeoTIFF/GDAL NoData metadata, when enabled.
- `NaN`, infinity, and extreme floating-point sentinel values.
- User-defined comparison thresholds (`≤`, `=`, `≥`).

Choose from four filling methods:

| Method | Behavior |
|---|---|
| **Minimum** | Replaces voids with the minimum valid elevation. |
| **Maximum** | Replaces voids with the maximum valid elevation. |
| **Mean** | Replaces voids with the mean valid elevation. |
| **Interpolated** | Seeds missing regions from nearby elevations, then applies iterative Laplace/SOR relaxation. |

Use per-tile or dataset-wide statistics for constant fill methods, and override the fill method for individual tiles when needed.

### 🧩 Smooth seams between neighboring tiles

- Reduce visible elevation discontinuities where tiles meet.
- Feather height differences across adjacent edges.
- Apply additional cross-boundary relaxation to improve transitions between tiles.
- Keep a pre-seam-smoothed buffer so the seam-smoothing process can be reapplied without accumulating earlier smoothing edits.

> Seam smoothing reduces boundary artifacts; results depend on the source data and tile compatibility. It does not guarantee perfect continuity for every dataset.

### 🔎 Preview in 2D and 3D

**2D mosaic viewer**
- Zoom and pan across the imported elevation grid.
- Inspect individual tiles and compare original versus processed data.
- Highlight missing/void pixels for troubleshooting.
- Select a square region for export, with a movable center gizmo and scale-bar feedback.

**3D terrain viewer**
- Explore terrain in an interactive WPF 3D viewport.
- Choose preview quality tiers labeled **1K, 2K, 4K, 8K, and 16K**.
- Switch between **Hypsometric, Grayscale, Alpine, and Desert** terrain color ramps.
- Adjust elevation exaggeration (Z-scale) and sun direction.
- Inspect optional water visualization and terrain wireframe overlays.

**Preview-resolution note:** The 16K tier increases mesh detail and sampling precision; according to the project architecture, its rendered texture is capped at 8192 × 8192. It is **not** a 16K texture output mode.

### 📦 Export terrain for downstream tools

Output options include:

- **16-bit normalized PNG heightmaps**.
- **16-bit normalized GeoTIFF**.
- **32-bit floating-point GeoTIFF**.

Choose how to divide the export:

- A single merged output.
- A layout matching the source tile grid.
- A custom **N × N** grid.

Other export controls:

- Crop to a selected square area.
- Resample with bilinear interpolation.
- Use resolution preset families for **power-of-two**, **Unreal Engine Landscape**, and **Unity Terrain** workflows.
- Preview the expected output files before exporting.

> **Height-value distinction:** Normalized 16-bit exports encode elevations into an output value range, while 32-bit floating-point GeoTIFF is intended for floating-point elevation samples. Confirm the normalization range and geospatial metadata in a sample output before relying on exact downstream elevation or placement behavior.

### 🎨 Customizable interface

Switch between six built-in themes: **Sage & Sandstone**, **Topo Buckskin & Olive**, **Canyon & Pine**, **Forest Canopy**, **Desert Mesa**, and **Midnight Slate**.

The application also provides shortcuts to open a detected dataset center in Google Maps or Google Earth when suitable coordinate information is available.

## Getting started

### System requirements

- Windows 10 or Windows 11.
- A Windows-compatible machine capable of running the application's .NET 8 WPF interface.
- Sufficient memory for your dataset. The application holds multiple in-memory elevation buffers; very large DEM mosaics may require substantial RAM.

### Run a packaged release

When a packaged Windows build is attached to a GitHub release, obtain it from the repository's **Releases** section and follow the instructions supplied with that asset. The required .NET runtime, if any, depends on how the release is published (framework-dependent versus self-contained).

**Do not assume that a source-code archive is a ready-to-run Windows application.**

### Build from source

Requires the **.NET 8 SDK** with Windows desktop/WPF support.

```powershell
# From the repository root
dotnet restore
dotnet build -c Release

# Run the built application
./bin/Release/net8.0-windows/DemCleaner.exe
```

> **Important:** The repository currently contains a Debug post-build step that automatically launches the application. Use `dotnet build -c Release` for unattended compilation to avoid launching extra GUI instances.

## Basic workflow

1. **Import** one or more DEM GeoTIFF files or a folder of tiles.
2. **Review arrangement** and ensure neighboring tiles are positioned correctly.
3. **Configure NoData detection** and select a void-filling method.
4. **Process the elevation data** and inspect differences in the 2D viewer.
5. **Adjust seam smoothing**, then check boundaries and terrain shape in the 3D viewer.
6. **Select an export area**, resolution preset, format, and grid layout.
7. **Review expected output files** and export to your chosen folder.

## Limitations and considerations

- **Windows-only interface:** DEM Cleaner is built with WPF.
- **Memory-intensive datasets:** Tiles retain original, intermediate, and final elevation arrays in memory. Large imports can use multiple gigabytes of RAM.
- **Square custom export selection:** Interactive custom selection is designed for square terrain heightmaps.
- **Different-sized neighboring tiles:** Some advanced seam-relaxation operations require matching adjacent tile dimensions.
- **No persistent settings yet:** UI preferences reset when the app restarts.
- **Basic automated checks:** The current `--self-test` covers a few application defaults, not full numerical processing or export correctness.

## Developer documentation

| File | Purpose |
|---|---|
| [AGENTS.md](AGENTS.md) | Instructions and invariants for coding agents. |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Components, data flow, algorithms, and limitations. |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Build, run, debug, and test procedures. |
| [AI_WORKFLOW.md](AI_WORKFLOW.md) | Model tiers, escalation rules, and efficient development workflow. |

For a basic smoke check after building:

```powershell
./bin/Release/net8.0-windows/DemCleaner.exe --self-test
Get-Content ./bin/Release/net8.0-windows/selftest_result.txt
```

## License

**DEM Cleaner is proprietary software.** Copyright © Sipp Studio. All rights reserved.

See [LICENSE.txt](LICENSE.txt) for the applicable terms. Availability of source code in a repository does **not** by itself grant permission to copy, modify, redistribute, or commercially use it.

---

*Built by Sipp Studio for cleaner, more usable terrain data.*
