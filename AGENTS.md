# AGENTS.md — Operational Reference for AI Coding Agents

This is the primary operational guide for AI coding agents working on **DEM Cleaner**. Read this document before investigating or modifying code. Consult [ARCHITECTURE.md](docs/ARCHITECTURE.md) for system design, [DEVELOPMENT.md](docs/DEVELOPMENT.md) for build and testing workflows, and [AI_WORKFLOW.md](docs/AI_WORKFLOW.md) for model tier selection and handoffs.

---

## 1. Project Overview & Quick Reference

**DEM Cleaner** is a Windows desktop application (.NET 8 / WPF) by Sipp Studio for inspecting, repairing, stitching, previewing (2D & GPU 3D), cropping, and exporting Digital Elevation Model (DEM) GeoTIFF datasets.

- **Stack**: C# 12, .NET 8.0 (`net8.0-windows`), WPF, `BitMiracle.LibTiff.NET` (2.4.660).
- **Entry Points**: [App.xaml.cs](App.xaml.cs) (`OnStartup`, `--self-test`), [MainWindow.xaml.cs](MainWindow.xaml.cs) (UI and workflow orchestration).
- **Core Engine**: [Services/DemProcessor.cs](Services/DemProcessor.cs) (pure compute, void filling, seam smoothing, resampling, export), [Services/TiffDemReader.cs](Services/TiffDemReader.cs) (TIFF I/O and grid arrangement).

---

## 2. Explicit Execution Modes

Every AI task operates in one of four modes. If not specified by the user, infer the mode from the request. Default to investigation or planning when intent is ambiguous.

| Mode | Allowed Actions | Disallowed Actions | Output Requirement |
| :--- | :--- | :--- | :--- |
| **INVESTIGATE ONLY** | Read files, run read-only commands (`git status`, search). | Do not edit or create files. Do not modify git state. | Verified root cause or architectural findings with file and method references. |
| **PLAN ONLY** | Read files, analyze systems and dependencies. | Do not modify application code. | Actionable plan: affected files, methods, invariants, risks, and acceptance criteria. |
| **IMPLEMENT** | Edit targeted files, run build/test commands. | Do not modify unrelated files or perform broad unrequested refactorings. | Smallest correct change, compilation/test results, and diff summary. |
| **REVIEW ONLY** | Inspect diffs and code for correctness, security, or performance. | Do not edit code unless explicitly asked to fix findings. | Prioritized review notes categorized by severity (Critical, Warning, Suggestion). |

---

## 3. Git Safety Rules

- **Inspect First**: Always check `git status --short` before substantial edits to identify untracked or modified files.
- **Preserve User Work**: Never overwrite, discard, or reset uncommitted user changes in the working tree.
- **No Unauthorized Git Actions**: Never commit, amend, push, create branches, or execute destructive Git commands (`git reset`, `git checkout --`, `git clean`) unless explicitly directed by the user.
- **Review Diffs**: Review targeted diffs (`git diff`) before declaring a task complete to ensure zero unintended whitespace or formatting churn.

---

## 4. Critical Application Invariants

These invariants must be preserved across all modifications:

1. **Elevation Buffer Separation**:
   - `DemTile.RawData` is immutable after load.
   - `DemTile.BaseFilledData` stores void-filled elevations *before* seam smoothing.
   - `DemTile.FilledData` stores final elevations *after* seam smoothing. Always restore `FilledData` from `BaseFilledData` at the start of `DemProcessor.ApplyTileSeamSmoothing` so smoothing remains non-destructive and repeatable.
2. **WPF Freezable Thread Affinity**:
   - Background threads (`Task.Run`, `Parallel.For`) producing `BitmapSource`, `WriteableBitmap`, `MeshGeometry3D`, `Brush`, or `Material` **must** call `.Freeze()` before passing them to the UI thread.
   - *Clarification*: Only freeze objects that do not require subsequent mutation on the UI thread.
3. **Independent Cancellation Tokens**:
   - `_cts` controls primary data pipelines (loading, void filling, seam smoothing, export).
   - `_cts3D` independently controls 3D viewport generation so rapid 3D parameter adjustments cancel superseded 3D renders without interrupting data state.
4. **UI Event Guard Flags**:
   - Guard against event recursion using `_isInitializing`, `_isBusy`, `_isSyncingSelectedTileCombo`, and `_isSyncingExportResolutionBoxes`.
5. **Preview & Export Symmetry**:
   - Preview calculations in `MainWindow.UpdateExpectedOutputFilesList` and actual disk export in `MainWindow.ExportCustomAreaSetAsync` must use identical sub-tile bounds, naming patterns, and resampling math.
6. **Virtual Mosaic Sampling**:
   - Never allocate a single giant stitched `float[]` for multi-tile datasets. Query multi-tile regions virtually via `MosaicDescriptor` and `DemProcessor.SampleMosaicPixel`.
7. **LibTiff Tag Registration**:
   - Call `TiffDemReader.EnsureTagsRegistered()` before any LibTiff read or write operation involving custom tags (`42113`, `33550`, `33922`, `34735`, `34737`).
8. **Proprietary License & Branding**:
   - Preserve Sipp Studio copyright, license tooltip loading in `MainWindow.LoadLicenseTooltipText`, and [LICENSE.txt](LICENSE.txt).

---

## 5. Build, Verification & Dependency Rules

- **Build Rule**: Always verify compilation with `dotnet build -c Release`. The `Debug` configuration contains a `PostBuild` target that automatically launches the GUI, which can lock the binary during automated agent execution.
- **Smoke Test**: Run `./bin/Release/net8.0-windows/DemCleaner.exe --self-test` and verify `./bin/Release/net8.0-windows/selftest_result.txt`.
- **Dependency Guardrail**: Do not add new NuGet packages or native C/C++ dependencies without explicit user authorization.

---

## 6. Task Completion Report Requirements

Conclude every implementation task with a concise report containing:
- **Files Changed**: List of modified and created files.
- **Implementation Summary**: Bullet points of specific changes made.
- **Verification Results**: Commands executed (`dotnet build -c Release`, `--self-test`) and their exit status. Explicitly state any checks that were omitted.
- **Known Risks / Unresolved Items**: Any edge cases, limitations, or follow-ups.
- **Documentation Updates**: Confirm whether documentation was updated or explain why no update was required.
