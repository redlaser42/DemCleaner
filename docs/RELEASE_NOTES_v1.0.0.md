# DEM Cleaner v1.0.0 — First Public Testing Release

Welcome to the initial testing release of **DEM Cleaner** by **Sipp Studio**!

DEM Cleaner is a Windows desktop application designed to transform raw digital elevation model (DEM) GeoTIFFs into clean, continuous, game-engine-ready 16-bit heightmaps for **Unreal Engine 5**, **Unity**, **Gaea**, **World Creator**, **Blender**, and **Houdini**.

---

## 🌟 Highlights & Features

- **Multi-Tile Mosaic Stitching**:
  - Automatically loads and arranges multi-tile DEM GeoTIFFs using USGS 1-meter naming conventions (`USGS_1M_*_x*y*_*`), standard grid coordinates, or geospatial tiepoints.
  - Scanline and tiled TIFF support powered by `BitMiracle.LibTiff.NET`.

- **Comprehensive Void Detection & 4 Filling Modes**:
  - Automatically detects missing elevations from GDAL NoData tags, IEEE non-finite values (`NaN`, `±Inf`), sentinel values ($\le -1e30$, $\ge 1e30$), and custom comparison thresholds.
  - Four void-filling algorithms: **Minimum**, **Maximum**, **Mean**, and **Gauss-Seidel SOR Laplace Interpolation** for smooth topological reconstruction.
  - Per-tile fill overrides for specialized local terrain handling.

- **Non-Destructive Seam Smoothing**:
  - Eliminates tile edge steps and boundary discontinuities with smoothstep feathering and iterative cross-boundary relaxation.
  - Non-destructive caching allows infinite re-adjustment without accumulating edits.

- **Dual 2D & Hardware-Accelerated 3D Previews**:
  - 2D mosaic viewport with real-time pan/zoom, before/after difference inspection, and red void overlays.
  - WPF Direct3D GPU 3D terrain visualizer with hypsometric, alpine, desert, and grayscale color ramps, adjustable lighting, wireframe, and water plane.
  - Preview quality tiers from 1K up to 16K mesh detail.

- **Game Engine Resolution Presets & Custom Cropping**:
  - Interactive square crop gizmo with live scale-bar feedback and human-scale silhouette ($1.80\,\text{m}$).
  - One-click presets for **Unreal Engine 5** (`8129×8129`, `4033×4033`, `2017×2017`, `1009×1009`, `505×505`), **Unity** (`4097×4097`, `2049×2049`, `1025×1025`), and **Power-of-Two** (`8192×8192`, `4096×4096`, `2048×2048`).
  - Merged (1×1), source grid, or custom $N \times N$ multi-tile split layouts.
  - Exports normalized 16-bit Gray PNGs, 16-bit GeoTIFFs, and 32-bit float GeoTIFFs.

- **6 Built-in Color Themes**:
  - Sage & Sandstone, Topo Buckskin & Olive, Canyon & Pine, Forest Canopy, Desert Mesa, and Midnight Slate.

---

## 📦 Getting Started

### Installation
1. Download `DemCleaner-v1.0.0-win-x64.zip` from the release assets below.
2. Extract the archive into any folder on your PC.
3. Ensure you have the **[Microsoft .NET 8.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)** installed.
4. Double-click `DemCleaner.exe` to run.

---

## 📝 Reporting Feedback & Issues
As this is the first testing release, your feedback on dataset compatibility, tile arrangement, and export accuracy is invaluable!
- Please report any unexpected errors, coordinate misalignment, or feature suggestions on our [GitHub Issues](https://github.com/sippstudio/dem-cleaner/issues) page.
- When reporting an issue, please include the DEM dataset source (e.g. USGS 1m, Copernicus 30m, SRTM), tile dimensions, and a screenshot if possible.

