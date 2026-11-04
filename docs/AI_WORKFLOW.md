# AI_WORKFLOW.md — Model Tier Selection & AI Development Workflow

This document establishes the strategy for pairing task complexity with the appropriate AI model capability tier in Gemini Antigravity, defines task execution modes, and outlines context-efficient handoff protocols.

---

## 1. Model Capability Tiers

Model tiers represent relative capability categories. **Do not default to the highest capability model for every task.** Choose the tier that matches the task's complexity, architectural risk, and mathematical depth.

### Tier 1 — High-Capability Reasoning Model
Reserve for high-risk, complex, or cross-cutting engineering tasks where flawed logic would introduce subtle regressions:

- **Numerical, Mathematical & GIS Algorithms**:
  - Modifying Gauss-Seidel SOR Laplace relaxation in `DemProcessor.InterpolateVoidsInPlace` or C0/C1 seam smoothing in `DemProcessor.ApplyTileSeamSmoothing`.
  - Bilinear resampling in `DemProcessor.ExtractResampledSubTile` or ellipsoidal coordinate projection math in `UtmCoordinateConverter`.
- **Concurrency & Memory Optimization**:
  - Managing async lifecycles (`_cts` vs. `_cts3D`), `Parallel.For` CPU throttling, or memory optimization across multi-gigabyte tile sets.
- **Architectural Design & High-Risk Reviews**:
  - Designing refactorings to decouple `MainWindow.xaml.cs` or conducting architectural reviews prior to releases.

### Tier 2 — Mid-Tier Balanced Model
The primary workhorse for standard implementation, debugging, and feature development:

- **Standard Feature Implementation**:
  - Adding new resolution presets, new void-filling options, or settings persistence.
  - Adding columns or summary metrics to `DgTiles` or `DgExpectedOutputs`.
- **UI Interaction & Workflow Logic**:
  - Updating 2D canvas overlay interactions, export crop gizmo logic, or scale bar updates in `MainWindow.xaml.cs`.
  - Keeping preview calculations in `UpdateExpectedOutputFilesList` synchronized with `ExportCustomAreaSetAsync`.
- **Localized Debugging & Testing**:
  - Resolving UI state synchronization issues (`_isBusy`, `_isSyncingExportResolutionBoxes`).
  - Expanding `--self-test` in `App.xaml.cs` or authoring new unit tests.

### Tier 3 — Fast / Low-Cost Model
Use for routine, narrow, or low-risk tasks to minimize latency and token expenditure:

- **Styling, Themes & Layout**:
  - Adding or adjusting `AppThemePalette` colors in `ThemeManager.cs`.
  - Updating XAML margins, labels, button text, or control templates in `MainWindow.xaml`.
- **Color Palettes**:
  - Adding a new terrain color ramp to `DemProcessor.EvaluateTerrainColorRamp`.
- **Routine Code Cleanup & Explanations**:
  - Removing dead code hooks, updating comments, or explaining existing methods.
- **Documentation Maintenance**:
  - Updating `AGENTS.md`, `ARCHITECTURE.md`, `DEVELOPMENT.md`, or `README.md`.

---

## 2. Decoupling Model Tier from Execution Mode

Model capability and task execution mode are orthogonal. Any tier can operate in any execution mode depending on the task:

- **Tier 1 in PLAN ONLY mode**: Designing an architectural decoupling strategy for `MainWindow.xaml.cs` before writing code.
- **Tier 2 in IMPLEMENT mode**: Adding a new resolution preset family and wiring it to UI controls.
- **Tier 3 in INVESTIGATE ONLY mode**: Searching the XAML hierarchy to find where a specific theme brush resource key is referenced.
- **Tier 1 in REVIEW ONLY mode**: Auditing a complex PR modifying the seam-smoothing algorithm for mathematical correctness.

---

## 3. Escalation & De-escalation Rules

### Escalation Triggers (Tier 3 → Tier 2, or Tier 2 → Tier 1)
Stop execution and recommend escalating to a higher model tier when:
1. **Uncertain Root Cause**: A bug's root cause cannot be conclusively identified after focused investigation.
2. **Retry Limit Exceeded**: **Maximum of 2 consecutive failed attempts** on a debugging or implementation task without new evidence. Do not attempt speculative fixes.
3. **Architectural Boundary Shift**: An apparently simple change requires modifying `MosaicDescriptor`, thread synchronization, or core async lifecycles.
4. **Numerical / Coordinate Sensitivity**: The change directly affects elevation sample arrays, boundary interpolation, or GeoTIFF tag encoding.
5. **Multi-System Impact**: The task expands to modify multiple services simultaneously.

### De-escalation Strategy (Plan-Then-Execute)
For complex multi-system tasks, split the workflow:
1. Use **Tier 1** in `PLAN ONLY` mode to formulate a specific, actionable implementation plan.
2. Hand off the plan to **Tier 2** or **Tier 3** in `IMPLEMENT` mode to execute the defined code changes and documentation updates.

---

## 4. Standard Compact Model Handoff Format

When handing off a task to another model tier or starting a new conversation, use this concise template:

```markdown
### AI Task Handoff
- **Objective**: [Concise description of the task goal]
- **Current Mode**: [INVESTIGATE ONLY | PLAN ONLY | IMPLEMENT | REVIEW ONLY]
- **Target Files & Methods**: [Specific files and method names]
- **Verified Findings**: [Factual, confirmed findings from code inspection]
- **Architectural Invariants & Constraints**: [Applicable invariants from AGENTS.md]
- **Changes Completed**: [What has already been modified or tested]
- **Tests Executed & Results**: [Build/test commands run and their status]
- **Unresolved Questions / Blockers**: [What remains uncertain or blocked]
- **Recommended Next Action**: [Immediate next step for the receiving agent]
- **Suggested Model Tier**: [Tier 1 | Tier 2 | Tier 3]
```

---

## 5. Context-Efficiency Rules

To keep token consumption low and maintain responsiveness:

1. **Read AGENTS.md First**: Rely on [AGENTS.md](AGENTS.md) as the default operational reference. Only inspect [ARCHITECTURE.md](ARCHITECTURE.md) or [DEVELOPMENT.md](DEVELOPMENT.md) when the task touches architectural boundaries or build/release procedures.
2. **Targeted Code Reads**: Inspect specific methods or line ranges rather than loading entire large files into context.
3. **Do Not Re-quote Code**: Avoid printing large blocks of existing, unchanged source code in chat responses.
4. **Keep Scope Narrow**: Avoid combining refactoring, feature development, and documentation updates into a single task.
5. **Fresh Sessions for Distinct Tasks**: When switching to an unrelated feature, begin a fresh conversation rather than carrying stale context forward.

---

## 6. Planning & Review Thresholds

- **Formal Planning Required**: Any task that modifies mathematical algorithms (`DemProcessor`), GeoTIFF metadata handling (`TiffDemReader`), or cross-cutting async workflows (`MainWindow`) must begin in `PLAN ONLY` mode.
- **Direct Implementation Allowed**: Localized UI tweaks, documentation maintenance, and self-contained bug fixes with verified root causes may proceed directly in `IMPLEMENT` mode.
- **Mandatory Review**: Any modification touching `RawData`, `BaseFilledData`, `FilledData`, or Freezable `.Freeze()` calls requires verification by a Tier 1 model or peer review before merging.

---

## 7. Documentation Maintenance Rules

Update documentation only when meaningful changes occur:

- **Update [AGENTS.md](AGENTS.md)** when operational invariants, Git rules, or execution modes change.
- **Update [ARCHITECTURE.md](ARCHITECTURE.md)** when system boundaries, data structures, algorithms, or technical limitations change.
- **Update [DEVELOPMENT.md](DEVELOPMENT.md)** when build targets, testing procedures, dependencies, or release steps change.
- **Update [AI_WORKFLOW.md](AI_WORKFLOW.md)** when model tier assignments, handoff standards, or escalation rules change.
- **Do Not Update Documentation** for trivial bug fixes, variable renames, or cosmetic UI styling changes.
