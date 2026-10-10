# FrameForge Phase 3 — Visual Editing and Designer Productivity

**Status:** Automated acceptance verified for the requested visual-editing scope  
**Date:** 2026-10-10  
**Branch:** `feature/frameforge-v2`  
**Audited HEAD:** `a8f4e392b8297bbd67ef4cdbc375a1c151847def`

## Outcome

Phase 3 adds practical direct manipulation to the V2 editor without changing the V2 authority chain:

`UiDocument → SemanticEditingSession → UiLayoutResolver → ResolvedUiLayout → canvas/inspector/export`

All persistent edits still pass through `UiDocumentEditor` and `SemanticEditingSession`. The live V2 path does not use a V1 `Project`, `FrameDef`, or drawable identity as authority. The existing V2 exporter was not rewritten, V1 was retained, and no golden output was changed.

## Visual editing architecture

- `SemanticSelection` remains an ordered set of stable `SemanticId` values; its last member is primary.
- Canvas and hierarchy selection requests carry typed semantic identity. Ctrl/Cmd-click toggles, Shift-click adds/toggles, and a plain click selects one item. Clicking an already-selected canvas member preserves the multi-selection for dragging.
- The view model projects the session selection into the hierarchy, inspector, and canvas after every layout rebuild. Rename, property edits, movement, save/reopen, and layout reconstruction do not replace identity.
- `UiDocumentEditor.Visual` contains the V2-native batch geometry, resize, arrange, ordering, deletion, and group commands. Each command validates its complete input before returning a candidate document.
- Continuous pointer and held-key operations use session transactions. A gesture stores one before-snapshot, not one snapshot per pointer delta.

## Multi-object movement

Dragging any selected editable element moves the selected set by one model-space delta. Arrow keys use the same batch command. Locks, missing nodes, zero-anchor/multi-anchor controls, and non-finite deltas reject the whole operation; there is no partial movement.

The ancestor/descendant policy is: only selected motion roots receive authored anchor offsets. A selected descendant of a selected owner is not offset again because it already follows its owner. Similarly, a selected node anchored to another selected local target follows that target instead of receiving a duplicate offset. Ownership, identity, typed anchor targets, and relative spacing are preserved.

## Direct resize

Exactly one supported, unlocked V2 element displays eight 9-pixel screen-space handles: four edges and four corners. Handles have distinct normal, hover, and active rendering. Pointer deltas are converted through the native viewport, so authored changes are expressed in UI units rather than screen pixels.

Direct resizing supports `Frame`, `Button`, `StatusBar`, and `Texture`. `FontString` is intentionally excluded because its effective size may be content-driven. A valid resize authors width/height and adjusts the existing single anchor offset to preserve the stationary edge(s). Template-derived dimensions become explicit overrides only when the resize succeeds. Dimensions cannot fall below one UI unit. Multi-anchor sizing is rejected rather than simplified or contradicted. One resize gesture is one undo entry; Escape restores its before-snapshot.

## Alignment and distribution

The six align commands use the primary selection's resolved rectangle as the reference. The primary rectangle is unchanged. Commands retain each moving element's size and translate its single authored anchor.

Horizontal and vertical distribution require at least three elements. Elements are sorted geometrically; the outer elements retain their boundary edges and the intermediate gaps are equalized. Unresolved geometry, unsafe anchors, or a locked element that would need to move reject the entire command. Each command produces at most one history entry.

## Editor-only groups

`DocumentEditorMetadata.Groups` stores stable group identity, name, ordered `SemanticId` membership, and a lock flag inside the authoritative `UiDocument`. Groups are persisted by the V2 codec, while empty group metadata is omitted so pre-Phase-3 project serialization remains compatible.

The UI supports creating a group from selection, selecting its members, renaming it, replacing membership, locking/unlocking it, and deleting the group without deleting nodes. Group names and lock state appear in the hierarchy. A locked group makes each member non-editable through the semantic gateway. Selecting group members and dragging provides safe group movement under the same batch constraints as any multi-selection. Groups never alter native owners and are ignored by export; a regression test compares XML before and after grouping byte-for-byte.

## Native ordering

Bring forward, send backward, bring to front, and send to back modify authored sibling order only within a meaningful native paint band:

- Frames must share owner, strata, and level.
- Regions must share owner, draw layer, and sublevel.
- Frames and regions are never treated as one Z-index domain.

Commands do not modify strata, frame level, draw layer, or sublevel. An element without another sibling in its band receives `FFV2-EDIT-ORDER-BAND`; a boundary move receives a no-change diagnostic.

## Keyboard and snapping

- Arrow: move selection by 1 native UI unit.
- Shift+Arrow: move selection by 10 native UI units.
- Delete: atomically delete selected editable subtrees.
- Escape: cancel the active drag/resize transaction.
- Ctrl/Cmd+Z: undo.
- Ctrl/Cmd+Y and Ctrl/Cmd+Shift+Z: redo.

Key repeats are coalesced until key-up. Shortcuts are suppressed when focus is within a text box, combo box, or numeric input.

Grid snapping is optional and uses a configurable 1–256 UI-unit spacing. Movement snaps the primary anchor's native offsets and applies the same delta to the selection. Resize deltas snap in native UI units. Snapping is independent of zoom; the optional visual grid is transformed by the same viewport and is suppressed when its rendered spacing would be too dense. Advanced smart guides and inferred constraints are out of scope.

## Undo, cancellation, and performance

- Batch commands create one immutable candidate document after validation.
- Pointer deltas do not serialize the document and do not rebuild template registries or decode assets.
- An active transaction owns one before-snapshot; 100 movement deltas produce one undo entry.
- Cancel restores document and selection without adding history.
- Resolved layout is still recomputed after successful deltas. This is predictable and correct, but very large documents may eventually benefit from measured incremental layout invalidation. No unbounded cache was introduced.

## Files added

- `src/FrameForge.Core/Semantics/V2/UiDocumentEditor.Visual.cs`
- `src/FrameForge.Desktop/ViewModels/MainWindowViewModel.V2.Visual.cs`
- `tests/FrameForge.Core.Tests/V2VisualEditingTests.cs`
- `docs/FRAMEFORGE_PHASE3_IMPLEMENTATION.md`

## Principal files modified

- `src/FrameForge.Core/Semantics/V2/UiDocument.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentCodec.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentEditor.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentValidator.cs`
- `src/FrameForge.Core/Semantics/V2/SemanticEditingSession.cs`
- `src/FrameForge.Desktop/Controls/LayoutCanvas.cs`
- `src/FrameForge.Desktop/ViewModels/FrameTreeNode.cs`
- `src/FrameForge.Desktop/ViewModels/MainWindowViewModel.cs`
- `src/FrameForge.Desktop/ViewModels/MainWindowViewModel.V2.cs`
- `src/FrameForge.Desktop/Views/MainWindow.axaml`
- `src/FrameForge.Desktop/Views/MainWindow.axaml.cs`
- `tests/FrameForge.Desktop.Tests/V2NativeCanvasTests.cs`
- `tests/FrameForge.Desktop.Tests/V2EditorWorkflowTests.cs`
- `docs/FRAMEFORGE_ROADMAP.md`

Phase 0–2 files were already modified or untracked in the working tree and were preserved. No commit or push was made.

## Verification results

Executed on 2026-10-10 in Release configuration:

| Verification | Result |
|---|---:|
| `dotnet build FrameForge.slnx -c Release --no-restore -m:1 /nodeReuse:false` | PASS — 0 warnings, 0 errors |
| Complete automated suite | PASS — 716 passed, 0 failed, 0 skipped |
| Core tests | PASS — 480 |
| Desktop tests | PASS — 236 |
| Focused tests whose names contain `V2` | PASS — 127 (109 Core + 18 Desktop) |
| New Phase 3 visual tests | PASS — 27 test cases (24 Core + 3 Desktop) |
| Existing V2 exporter suite | PASS — 15 |
| Native Avalonia canvas tests | PASS — 3 |
| Golden export compatibility | PASS as part of the complete/export suites; fixtures unchanged |
| V1 regression coverage | PASS as part of the complete suite |
| `git diff --check` | PASS |

The native headless Avalonia canvas exercised render/hit-test behavior and constant-size resize handles. A separate Xvfb application smoke was not executed because `xvfb-run` is not installed. This editor-only phase did not require a WoW deployment or EPF test, and no new real-client compatibility claim is made.

## Known limitations and deferred work

- Direct resize intentionally rejects multi-anchor controls and excludes content-sized `FontString` nodes.
- Grid snapping is simple anchor/delta snapping; smart guides, neighbor-edge snapping, and constraint inference are deferred.
- Groups do not create nested group hierarchies and do not change native ownership.
- Ordering operates within existing native paint bands; changing strata/level/draw-layer/sublevel remains an explicit property edit.
- Layout resolution remains a full successful-delta recomputation; optimize only after profiling representative large documents.
- The broader roadmap's portable asset picker/import workflow, richer font styling, texCoords, and dedicated overlap/reveal tooling were not part of this implementation and remain open.

## Acceptance and recommended next milestone

The requested Phase 3 visual-editing scope meets automated acceptance: multi-selection, batch movement, direct supported resizing, alignment/distribution, locks, undo/redo, keyboard precision, grid snapping, native-band ordering, and safe persistent editor groups are implemented and regression-tested. Existing V1 behavior and V2 export fixtures remain green.

The recommended next milestone is Phase 4 client fidelity: extend the acceptance corpus for native anchors, paint ordering, template/font/texture rendering, and approximation diagnostics, with recorded WoW 3.3.5a build-12340 evidence for every material fidelity claim.
