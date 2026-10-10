# FrameForge Phase 1 — V2-Native Layout and Rendering

**Status:** Implemented; automated acceptance verified  
**Date:** 2026-10-10  
**Implementation baseline:** `feature/frameforge-v2` at `a8f4e392b8297bbd67ef4cdbc375a1c151847def`, plus the uncommitted Phase 0 test correction and this Phase 1 work

## Outcome

Schema-v2 projects now use `UiDocument` as their sole authoritative model for live layout, hierarchy, selection, rendering, hit testing, editing and export. The desktop no longer converts a V2 document into a V1 `Project` or invokes the V1 `LayoutResolver` for the active V2 path.

The V1 implementation and `UiDocumentProjection` remain in the repository for legacy projects, migration/comparison work and existing compatibility tests. They were not removed.

The three optional architecture audit files named in the Phase 1 request (`FRAMEFORGE_AUDIT.md`, `FRAMEFORGE_CAPABILITY_MATRIX.md`, and `FRAMEFORGE_ARCHITECTURE_RECOMMENDATIONS.md`) were not present in the repository. The direction, roadmap, test strategy and Phase 0 baseline documents were used as the governing inputs.

## Architecture

Previous live V2 path:

```text
UiDocument -> UiDocumentProjection -> Project -> LayoutResolver -> LayoutCanvas
```

Current live V2 path:

```text
UiDocument + BlizzardTemplateRegistry + UiPreviewHost + target build
    -> UiLayoutResolver
    -> ResolvedUiLayout
       +-> V2 hierarchy / selection / editing
       +-> LayoutCanvas / native hit testing / render layers
       +-> V2 export conformance validation

UiDocument -> existing V2FrameXmlExporter -> FrameXML
```

`ResolvedUiLayout` is transient and is never persisted. It carries semantic IDs, runtime and display names, native node kinds, owners, authored and effective properties, template provenance, resolved rectangles, effective visibility, all authored anchors and their resolution state, native paint order, and diagnostics. The original `UiDocument` remains unchanged.

## Components preserved

- The schema-v2 `UiDocument`, codec, validator and semantic editor.
- The native V2 FrameXML exporter and established golden output.
- The build-12340 template registry and effective-property resolver.
- Canvas viewport transforms, fit/reveal behavior, zoom/pan, render layers, selection chrome and diagnostics.
- Texture/BLP/TGA resolution and decoding.
- V1 projects, importers, layout, editor and rendering path.

## Components changed

- Added a direct V2 layout resolver and explicit `UiPreviewHost` profile.
- Changed V2 ownership-preserving reparenting to use native resolved geometry.
- Changed V2 load, edit, drag and template-registry refresh to rebuild native layout without `UiDocumentProjection`.
- Added a V2-native tree representation while retaining the shared tree UI.
- Added a native canvas input, native paint/hit order, direct V2 texture/font/button/status-bar rendering, and direct V2 anchor overlays.
- Added layout/export conformance validation to the existing exporter build pipeline.
- Retained a `LayoutResult` compatibility snapshot assembled from `ResolvedUiLayout` for shared legacy inspector summaries and older automation. It is not produced by a V1 `Project` or `LayoutResolver`, and the V2 canvas does not consume it.

## Files added and modified

Added:

- `src/FrameForge.Core/Semantics/V2/ResolvedUiLayout.cs`
- `src/FrameForge.Core/Export/V2LayoutExportConformance.cs`
- `tests/FrameForge.Core.Tests/V2LayoutTests.cs`
- `tests/FrameForge.Desktop.Tests/V2NativeCanvasTests.cs`
- `docs/FRAMEFORGE_PHASE1_IMPLEMENTATION.md`

Modified for Phase 1:

- `src/FrameForge.Core/Semantics/V2/UiDocument.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentEditor.cs`
- `src/FrameForge.Core/Export/V2FrameXmlExport.cs`
- `src/FrameForge.Desktop/Controls/LayoutCanvas.cs`
- `src/FrameForge.Desktop/Rendering/ICanvasLayer.cs`
- `src/FrameForge.Desktop/Rendering/VisualContentLayer.cs`
- `src/FrameForge.Desktop/Rendering/DebugOverlayLayer.cs`
- `src/FrameForge.Desktop/Rendering/SelectionOverlayLayer.cs`
- `src/FrameForge.Desktop/ViewModels/FrameTreeNode.cs`
- `src/FrameForge.Desktop/ViewModels/MainWindowViewModel.cs`
- `src/FrameForge.Desktop/ViewModels/MainWindowViewModel.V2.cs`
- `src/FrameForge.Desktop/Views/MainWindow.axaml.cs`
- `tests/FrameForge.Desktop.Tests/V2EditorWorkflowTests.cs`
- `tests/FrameForge.Desktop.Tests/V2TemplateDesignerTests.cs`
- `docs/FRAMEFORGE_ROADMAP.md`

The pre-existing uncommitted Phase 0 change in `StockTemplateResolverTests.cs` and the user-owned untracked direction/test-strategy/baseline documents were preserved. Golden project, XML and EPF fixtures were not modified.

## Resolved layout and anchor behavior

The preview host explicitly supplies the module-host global name, dimensions, and WoW target (`wow-3.3.5a`, build `12340`). `FillHost` roots use that exact rectangle. Explicit roots resolve their authored root anchor against it. A host-name or target-build mismatch is diagnosed.

For nodes, structural ownership and anchor targets remain independent:

- `Parent` resolves to the typed structural owner.
- `CompositionRoot` resolves to the document root.
- `LocalNode` resolves by `SemanticId`, supporting sibling anchors.
- `ExternalGlobal` resolves only when the explicit preview host supplies that exact global.
- `Unresolved` and unknown external targets fail closed with diagnostics; there is no UIParent substitution.

All authored anchors are retained. Independent equations can derive a missing width or height from multiple anchors. Effective dimensions from authored values or templates remain distinct from authored values. Contradictory over-constraints are reported; the primary/effective geometry is not presented as satisfying the conflicting anchor. Cycles and missing geometry remain unresolved.

Paint order uses region draw-layer/sublevel and frame strata/level, with authored sibling order as the stable tie-breaker. Effective visibility folds in owner visibility without rewriting the authored `visible` property.

## Export conformance

`V2FrameXmlExporter` was preserved. After its existing structural validation, it now compares the export plan with a native resolved layout from the same document. The conformance pass checks:

- common document identity and complete node coverage;
- structural owner identity;
- authored anchor counts;
- single-anchor effective dimensions versus preview geometry;
- authored child ordering under every root/container.

Existing exporter validation continues to cover runtime names, anchor targets, external host relationships, region layers, templates and generated XML structure. The checked-in golden project and XML remain unchanged.

## Tests added or adapted

New Core coverage exercises:

- fill-host and explicit-root layout;
- target-build/host assumptions;
- nested ownership and parent-relative anchors;
- sibling and explicit external-host anchors;
- unresolved external targets;
- multiple-anchor dimension derivation;
- authored versus template-effective dimensions and provenance;
- inherited visibility;
- region layer/sublevel and frame strata/level order;
- native front-to-back hit testing;
- preview/export ownership, anchors, dimensions and ordering conformance.

A new Avalonia headless test renders and hit-tests a V2 status bar with `Project = null`, proving the production canvas consumes `ResolvedUiLayout` directly. Existing V2 workflow/template tests were adapted to assert native layout rather than the old V1 projection.

## Verification results

Run from `/home/smithkt/git/FrameForge` in Release configuration:

- `dotnet build FrameForge.slnx -c Release --no-restore`: **Pass**, 0 warnings, 0 errors.
- Complete suite: **Pass**, 674 tests (444 Core + 230 Desktop), 0 failed, 0 skipped.
- Focused V2 suite: **Pass**, 85 tests (73 Core + 12 Desktop), 0 failed, 0 skipped.
- Existing V2 golden exporter assertions: included in the complete passing suite; golden outputs were not modified.
- Representative synthetic and build-12340 template tests: included in the complete passing suite.
- Avalonia headless rendering: included in the Desktop suite and does not require Xvfb.
- Full application `--smoke`: not executed because `xvfb-run` is unavailable in this environment.

No EPF was built, staged or deployed, and no external repository or server was modified.

## Historical real-client evidence

The user reports that the golden EPF was previously deployed and activated in the Eitrigg AzerothCore environment and tested in WoW. The user reports successfully changing its size and position, removing it, and restoring it through in-game commands. This is useful historical verification, but this Phase 1 report does not invent or claim missing screenshots, logs, revisions, hashes, client settings, or a more precise acceptance verdict. It is not a fresh Phase 1 client-compatibility test.

## Remaining legacy dependencies

- The shared `MainWindowViewModel` still owns V1 editor/coordinator state. A V2 session places an empty, non-derived V1 project in that legacy slot so shared V1 commands remain safe; the V2 semantic/render path does not read it.
- A compatibility `LayoutResult` is published for shared inspector summaries and older automation. It is derived from `ResolvedUiLayout`, not from a projected V1 model.
- `UiDocumentProjection` remains for migration, comparison and explicit legacy compatibility tests.
- Several canvas record types still carry optional V1 fields because the canvas serves both generations. V2 drawables carry `ResolvedUiElement` and do not require a `FrameDef`.
- V2 tree filtering/search currently retains the complete native hierarchy; Phase 2 can move the remaining shared navigation policy onto semantic IDs.

These are shared-shell compatibility seams, not an active V2 `UiDocument -> Project -> LayoutResolver` pipeline. Removing them belongs to the staged legacy cleanup described by later roadmap phases.

## Known limitations

- The preview supports one explicitly supplied module host rectangle. Other external globals remain unresolved until a future host profile explicitly supplies them.
- General cyclic constraint solving is not attempted. Cycles fail closed.
- Over-constrained anchors retain all authored relationships and report conflicts; they are not flattened or silently “fixed.”
- Template/runtime behaviors that resize controls dynamically remain preview approximations and retain their existing diagnostics.
- The compatibility inspector snapshot does not expose every native anchor detail; the native selection overlay and `ResolvedUiLayout` do.
- This work does not add Phase 2/3 features such as undo/redo, richer multi-selection, alignment/distribution, resize handles or a redesigned inspector.
- Generated XML and automated tests do not by themselves establish fresh real-client compatibility.

## Phase 1 status and next step

The Phase 1 acceptance criteria are met for the repository implementation and automated verification: V2 has one authoritative persisted model, the live canvas consumes native resolved layout with no projected V1 project, existing editing/export workflows pass, native anchors fail visibly when unresolved, exporter conformance is checked, and the complete suite is green.

Recommended next milestone: Phase 2, consolidating selection/hierarchy/inspector commands around `SemanticId` and transaction boundaries while preserving the now-native V2 layout contract. Before a release claim or fidelity milestone, repeat the documented real-client acceptance workflow and capture reproducible evidence.
