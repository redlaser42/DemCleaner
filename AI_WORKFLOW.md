# AI_WORKFLOW.md — Model Tier Selection & AI-Assisted Development Workflow

This document defines the workflow and model-selection strategy for AI-assisted development on **DEM Cleaner** in Gemini Antigravity. Its goal is to maximize engineering quality while minimizing token consumption and turnaround time by matching task complexity to the appropriate model capability tier.

---

## 1. Model Capability Tiers

Treat these three tiers as relative capability categories that map to the high-reasoning, balanced, and fast/lightweight models available in Antigravity. **Do not automatically use a Tier 1 model for every task.**

### Tier 1 — High-Capability Reasoning Model
Reserve Tier 1 for high-complexity, high-risk, or cross-cutting work where subtle errors in math, concurrency, or architecture would cause regressions:

- **Numerical, Mathematical & GIS Algorithms**:
  - Modifying Gauss-Seidel SOR Laplace interpolation ([`DemProcessor.InterpolateVoidsInPlace`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs#L556-L686)) or C0/C1 cross-tile seam smoothing ([`DemProcessor.ApplyTileSeamSmoothing`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs#L262-L497)).
  - Changing GeoTIFF coordinate tiepoint math, bilinear sub-tile resampling ([`ExtractResampledSubTile`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs#L1105-L1155)), or UTM/EPSG projections ([`UtmCoordinateConverter`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/UtmCoordinateConverter.cs)).
- **Concurrency, Threading & GPU Memory Performance**:
  - Modifying async cancellation lifecycles (`_cts` vs. `_cts3D`), `Parallel.For` unsafe pointer/backbuffer writes in [`Build3DHeightmapAssets`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs#L1238-L1609), or reducing memory pressure across multi-gigabyte DEM tile sets.
- **Architectural Design & Major Refactoring**:
  - Extracting subsystems out of the 3,556-line [MainWindow.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs) into dedicated services or view-controllers.
- **Complex Multi-System Debugging & High-Risk Reviews**:
  - Diagnosing elusive bugs spanning TIFF binary decoding, normalized coordinate transforms, and WPF 3D rendering, or reviewing large diffs before release.

### Tier 2 — Mid-Tier Balanced Model
Use Tier 2 as the default workhorse for standard feature development, localized bug fixes, and component integration:

- **Standard Feature Implementation**:
  - Adding a new export resolution preset family, a new void-filling option, or a settings persistence service (e.g., saving/loading user preferences to JSON).
  - Adding new columns or summary metrics to `DgTiles` or `DgExpectedOutputs`.
- **Moderate Refactoring & UI Interaction Logic**:
  - Updating 2D mosaic canvas overlay interactions, gizmo behavior, or scale bar calculations in [MainWindow.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs).
  - Synchronizing changes between [`UpdateExpectedOutputFilesList`](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs#L2275-L2400) and [`ExportCustomAreaSetAsync`](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs#L2925-L3128).
- **Localized Bug Investigation & Testing**:
  - Fixing UI state synchronization bugs (`_isBusy`, `_isSyncingExportResolutionBoxes`, aspect ratio locking).
  - Expanding `--self-test` in [App.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/App.xaml.cs) or setting up unit tests for `DemProcessor` and `TiffDemReader`.

### Tier 3 — Fast / Low-Cost Model
Delegate routine, well-defined, or low-risk tasks to Tier 3 to save cost and latency:

- **UI Styling, Themes & Layout Tweaks**:
  - Adding or tweaking `AppThemePalette` colors in [Services/ThemeManager.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/ThemeManager.cs).
  - Updating XAML margins, tooltips, labels, button text, or control templates in [MainWindow.xaml](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml).
- **Terrain Color Ramps**:
  - Adding a new color ramp case to [`DemProcessor.EvaluateTerrainColorRamp`](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs#L1765-L1876).
- **Simple Bug Fixes & Cleanup**:
  - Fixing a known typo, a null-check, or removing dead code (such as the empty `SyncGridDimensionsBoxes` hook) when requirements are explicit.
- **Documentation & Routine Investigation**:
  - Updating `AGENTS.md`, `ARCHITECTURE.md`, `DEVELOPMENT.md`, `README.md`, or explaining existing methods.

---

## 2. Quick Task-to-Tier Mapping for DEM Cleaner

| Task Type | Target Files | Recommended Tier |
| :--- | :--- | :---: |
| Add/edit color themes or XAML styles/tooltips | `Services/ThemeManager.cs`, `MainWindow.xaml` | **Tier 3** |
| Add a 3D terrain color ramp palette | `Services/DemProcessor.cs`, `MainWindow.xaml` | **Tier 3** |
| Update project documentation | `*.md` | **Tier 3** |
| Add UI controls and wire them to `DemProcessingOptions` | `MainWindow.xaml`, `MainWindow.xaml.cs`, `Models/DemTile.cs` | **Tier 2** |
| Modify export file naming, resolution presets, or expected output table | `MainWindow.xaml.cs`, `Models/DemTile.cs` | **Tier 2** |
| Expand `--self-test` or add unit tests | `App.xaml.cs`, test files | **Tier 2** |
| Modify Laplace void interpolation or inter-tile seam smoothing math | `Services/DemProcessor.cs` | **Tier 1** |
| Modify GeoTIFF tag decoding/encoding or UTM coordinate math | `Services/TiffDemReader.cs`, `Services/UtmCoordinateConverter.cs` | **Tier 1** |
| Refactor `MainWindow.xaml.cs` architecture or optimize 3D/RAM usage | `MainWindow.xaml.cs`, `Services/DemProcessor.cs` | **Tier 1** |

---

## 3. Escalation & De-escalation Rules

### When to Escalate to a Higher Tier (Tier 3 → Tier 2, or Tier 2 → Tier 1)
1. **Unresolved Root Cause**: If a Tier 3 or Tier 2 model cannot confidently identify the root cause of a bug after inspecting the directly relevant methods, stop guessing and escalate.
2. **Cross-Boundary Impact**: If a seemingly simple UI change requires altering `MosaicDescriptor` coordinate mapping, async `CancellationTokenSource` lifecycles, or thread synchronization, escalate to Tier 2 or Tier 1.
3. **Scope Expansion**: If a task expands to touch more than 3 methods in [MainWindow.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs) and [Services/DemProcessor.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs) simultaneously, reassess the model tier.
4. **High Regression Risk**: Any change that modifies pixel buffer indexing (`y * width + x`), elevation normalization ranges (`GetEffectiveNormalizationRange`), or GeoTIFF binary scanline/tile reads/writes should either be implemented or reviewed by a Tier 1 model.

### When to De-escalate to a Lower Tier (Tier 1 → Tier 2/3)
1. **Plan-Then-Execute Split**: When a complex feature requires architectural design, use **Tier 1** to design the implementation plan (specifying exact method signatures and equations), then switch to **Tier 2** or **Tier 3** to execute the boilerplate XAML wiring, implementation, and documentation updates.

---

## 4. Standard AI Development Workflow

All AI agents working on this repository must follow this 10-step workflow:

1. **Understand the Task**: Read the user request carefully. Clarify ambiguities before writing code.
2. **Assess Complexity & Risk**: Consult Section 1 and Section 2 of this document to evaluate the scope and regression risk.
3. **Verify Model Tier Suitability**: If operating on a lower-tier model and the task triggers an escalation rule in Section 3, notify the user and recommend switching to a higher-tier model (or using `/plan` first).
4. **Inspect Targeted Context**:
   - Read [AGENTS.md](file:///C:/Users/redla/repositories/DEM%20Cleaner/AGENTS.md) and the relevant sections of [ARCHITECTURE.md](file:///C:/Users/redla/repositories/DEM%20Cleaner/ARCHITECTURE.md) rather than scanning the entire codebase from scratch.
   - Use targeted line-range reads on large files ([MainWindow.xaml.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml.cs) is 3,556 lines; [Services/DemProcessor.cs](file:///C:/Users/redla/repositories/DEM%20Cleaner/Services/DemProcessor.cs) is 1,877 lines; [MainWindow.xaml](file:///C:/Users/redla/repositories/DEM%20Cleaner/MainWindow.xaml) is 1,463 lines) to conserve context window tokens.
5. **Formulate a Plan**: For multi-file or non-trivial changes, outline the exact methods and UI controls affected before editing.
6. **Implement the Smallest Reasonable Change**: Make surgical edits. Preserve existing comments, formatting, and unrelated uncommitted work.
7. **Build & Verify (`Release` Mode)**:
   - Run `dotnet build -c Release` (avoiding the `Debug` PostBuild GUI launch).
   - Run `./bin/Release/net8.0-windows/DemCleaner.exe --self-test` when applicable.
8. **Review the Diff**: Run `git diff` on modified files to verify no accidental regressions, missing `.Freeze()` calls, or recursive UI event triggers were introduced.
9. **Maintain Documentation**: Update [ARCHITECTURE.md](file:///C:/Users/redla/repositories/DEM%20Cleaner/ARCHITECTURE.md) or [DEVELOPMENT.md](file:///C:/Users/redla/repositories/DEM%20Cleaner/DEVELOPMENT.md) if architectural boundaries, build steps, or workflows changed.
10. **Summarize Concisely**: Report the files changed, verification results, and any remaining questions or follow-ups.

