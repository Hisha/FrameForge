# FrameForge Phase 2 — V2 Semantic Editing Foundation

**Status:** Implemented and automated acceptance verified  
**Date:** 2026-10-10  
**Branch:** `feature/frameforge-v2`  
**Starting revision:** `a8f4e392b8297bbd67ef4cdbc375a1c151847def`

## Scope and result

Phase 2 establishes one V2 editing architecture around `UiDocument`, stable `SemanticId` identities and reversible semantic transactions. The active V2 selection, hierarchy, inspector and mutation paths no longer require a V1 `Project`, `FrameDef`, `LayoutResolver` result or a V2-to-V1 editing projection. V1 authoring remains intact as a separate compatibility path.

The Release build and all 689 automated tests pass. The V2 exporter and checked-in golden integration files were not changed. These results verify application behavior and deterministic export; they do not constitute a fresh WoW 3.3.5a client acceptance result.

## Previous editing architecture

At the Phase 1 baseline, `UiDocument` and `ResolvedUiLayout` were already authoritative for V2 rendering and hit testing, but editing coordination still had several shared V1-era seams:

- selection was mirrored as string values through `SelectedName` and `_selectedNames`;
- the native V2 tree did not apply the existing search/filter policy;
- V2 inspector refresh produced a compatibility `LayoutResult` for older summaries/tests;
- inspector commits chained several editor calls outside one history transaction;
- every canvas pointer-move applied a standalone edit;
- there was no V2 undo/redo history; and
- lock metadata was displayed but was not uniformly enforced by the semantic command layer.

The source matched the Phase 1 implementation report. No V1 `Project` projection was found in the live V2 renderer, but the compatibility `LayoutResult` and shared string selection adapters were still present as reported.

## Semantic editing architecture

`SemanticEditingSession` is now the authoritative V2 editing gateway. It owns:

- the current `UiDocument`;
- an ordered `SemanticSelection`;
- the primary selected `SemanticId` (the final ordered identity);
- the active `UiDocumentEditor` and template registry;
- bounded undo and redo stacks; and
- an optional logical transaction.

Every persistent V2 mutation is submitted as a `UiDocumentEditor` command through `SemanticEditingSession.Execute`. A successful candidate document is validated before publication. A rejected operation leaves the current document and history unchanged. The desktop then derives a fresh `ResolvedUiLayout`, tree and inspector view from the accepted document.

The UI does not mutate `UiDocument` collections or node records directly. Inspector record construction occurs inside a single gateway command and is published only after all steps succeed.

## Selection model

`SemanticSelection` supports empty, single and ordered multiple selection. All identities are `SemanticId`; runtime names, display labels and canvas drawable instances are presentation data only.

Selection is retained across:

- layout and template-registry rebuilds;
- display-label and runtime-name changes;
- inspector property edits;
- reparenting and native reorder operations; and
- undo/redo snapshot restoration.

Deletion prunes removed identities. Desktop delete selects the surviving structural owner. Undo restores both the prior document and selection snapshot. The existing `SelectedName`/`SelectedNames` properties remain as UI adapters for shared controls, but they are derived from the semantic selection and are not authoritative V2 state.

Canvas events expose parsed `SemanticId` values for V2 interaction. Tree and canvas gestures update the same `SemanticEditingSession.Selection` instance.

## Hierarchy synchronization

The V2 hierarchy is rebuilt directly from `UiDocument.CompositionRoots`, `UiNode.Owner` and ordered `Children` collections. It preserves native child order and structural ownership independently from anchor targets.

Search is a case-insensitive match over display label, runtime name, native kind and semantic ID. Structural filtering distinguishes frame-like nodes from regions. Matching ancestors remain visible and expand during search. Selecting a nested node expands its owner path, while rename and layout refresh retain semantic selection.

No tree operation implicitly reparents a node. Reparenting is an explicit inspector command through the gateway.

## Inspector architecture

The V2 inspector reads authored values from `UiDocument`, resolved geometry/diagnostics from `ResolvedUiLayout`, and effective values/provenance from the build-12340 template registry. The active V2 refresh no longer constructs or assigns a compatibility `LayoutResult`.

The existing editable surface remains supported: labels and runtime names, structural owner, typed anchor target/points/offsets, dimensions, visibility, textures, fonts, button values, status-bar values/colors and approved templates. Authored dimensions and effective template dimensions remain separately described. Template provenance and preview-only approximations remain read-only.

An inspector Apply is one atomic semantic command and therefore one undo entry. A failure at any intermediate step publishes diagnostics without partially updating the live document.

## Lock contract

Lock state is stored in `UiNode.Editor.Locked` and enforced inside `UiDocumentEditor`, not merely in control enablement.

- A locked node rejects rename, property, dimension, anchor, template, movement, reorder, reparent and delete operations.
- A locked local owner rejects child creation, deletion, reorder and reparent operations that would modify its child list.
- Deleting a subtree is rejected if any node in that subtree is locked.
- Lock/unlock is itself an allowed semantic command so a locked node can be unlocked.
- Composition roots do not currently expose lock metadata.
- Lock inheritance is not implemented: locking a parent protects its child list but does not implicitly lock every descendant's independent properties.
- V2 editor-only groups are not implemented. Existing V1 groups remain V1-only; advanced V2 grouping belongs to Phase 3.

Canvas, inspector, hierarchy, keyboard and programmatic editor commands all reach this same enforcement point.

## Undo/redo and transactions

History stores bounded semantic snapshots of `UiDocument` plus `SemanticSelection`. The default limit is 100 undo and 100 redo entries per open session. Snapshot history is appropriate for the current document scale and keeps the implementation explicit; decoded texture assets, template caches and transient `ResolvedUiLayout` objects are not copied into history.

Supported history includes creation, deletion, rename, properties, dimensions, visibility, anchors, reparenting, order, locks and movement. A failed edit creates no entry. Undo followed by a new successful edit clears redo.

History is intentionally session-local and is reset when a project is opened or created. It is not serialized into `.fforge.json`.

## Canvas and keyboard integration

A V2 drag lazily begins one transaction at the first non-zero movement. Pointer-move deltas update the semantic document for live preview, but completion commits one history entry regardless of event count. Escape while the V2 canvas owns an active drag cancels it, restores the before snapshot and leaves history unchanged.

Dragging preserves the existing single authored anchor and adjusts only its typed offsets. Multi-anchor movement remains rejected with an actionable semantic diagnostic rather than simplifying the anchors.

Arrow keys move the primary V2 selection through the same command gateway (one unit, or ten with Shift). Ctrl/Cmd+Z and Ctrl/Cmd+Y invoke V2 undo and redo. Keyboard handling does not intercept text or combo-box editing.

Zoom, pan, native hit testing, selection visualization, anchors, templates, textures and status-bar rendering continue to use the Phase 1 canvas and `ResolvedUiLayout` path.

## Components

### Added

- `src/FrameForge.Core/Semantics/V2/SemanticEditingSession.cs`
- `tests/FrameForge.Core.Tests/V2SemanticEditingSessionTests.cs`
- `docs/FRAMEFORGE_PHASE2_IMPLEMENTATION.md`

### Extended

- `UiDocumentEditor`: command-layer lock enforcement and semantic lock/unlock.
- `MainWindowViewModel.V2`: session ownership, atomic inspector commands, history, locks, native hierarchy filtering and presentation refresh.
- `MainWindowViewModel`: V2 semantic selection adapters, drag transaction integration and V2 tree filter dispatch.
- `LayoutCanvas`: typed V2 event identities and cancellable V2 drag gesture.
- `MainWindow`: minimal undo/redo/lock controls and keyboard integration.
- `FrameTreeNode`: existing V2-native identity, label, kind and lock projection is retained.

### Reused unchanged in responsibility

- `UiDocument`, `UiLayoutResolver` and `ResolvedUiLayout` remain the semantic and resolved layout foundations.
- The native V2 canvas renderer and hit tester remain authoritative.
- The existing V2 FrameXML exporter remains the exporter.
- V1 authoring, import and migration-compatible code remain present.

## Remaining V1 compatibility seams

Shared desktop controls still expose string-shaped selection and tree names so one Avalonia surface can host both generations. For V2 those strings are serialized semantic IDs derived from `SemanticSelection`; they do not refer to a V1 object. The inactive V1 `Project` and its `LayoutResult` still exist in the shared view model for V1 compatibility, but active V2 selection, hierarchy, inspector, editing, canvas mutation and rendering do not read them.

Removing these shared adapters or the V1 subsystem is Phase 6 work and was deliberately not attempted.

## Automated validation

Validation used .NET 10 in Release configuration.

| Check | Result |
|---|---:|
| Release solution build | Pass — 0 warnings, 0 errors |
| Complete Core suite | 456 passed, 0 failed, 0 skipped |
| Complete Desktop suite | 233 passed, 0 failed, 0 skipped |
| Complete automated suite | **689 passed, 0 failed, 0 skipped** |
| Focused V2 Core suite | 85 passed |
| Focused V2 Desktop suite | 15 passed |
| Focused V2 total | **100 passed** |
| New semantic session/transaction suite | 12 passed |
| V2 exporter suite | 15 passed |
| Avalonia V2 native canvas headless test | 1 passed |
| Checked-in V2 golden integration files | Unchanged |
| Full application `--smoke` under Xvfb | Not executed — `xvfb-run` is unavailable |

The complete suite includes existing V1 compatibility, canvas, layout, importer and exporter coverage. No golden output was modified.

## Tests added or expanded

New focused tests cover:

- stable semantic selection across rename and layout-independent edits;
- ordered multiple selection and primary selection;
- selection pruning and restoration on delete/undo/redo;
- create/delete identity and native order restoration;
- rename, visibility/property, anchor and reparent undo/redo;
- atomic failed edits with no history;
- redo invalidation;
- 100 drag deltas coalesced into one entry;
- cancelled transactions;
- bounded snapshot history;
- node and owner lock enforcement;
- V2 tree search/filter/reveal behavior;
- tree/canvas/inspector semantic selection synchronization;
- native layout refresh without a V1 compatibility snapshot; and
- desktop drag/history/lock integration.

Existing native layout, V2 export/golden, V1 compatibility and headless rendering tests remain green.

## Known limitations

- V2 multi-selection state is established, but Phase 3 group movement, align/distribute and group transforms are intentionally absent.
- V2 editor-only groups and inherited locks are not implemented.
- Dragging supports exactly one authored anchor; complex multi-anchor constraints are rejected rather than rewritten.
- History is in-memory, bounded and reset on open/new; no persistent recovery journal is provided.
- The tree's Structure/Visual split is based on native frame-versus-region semantics; more specialized navigation categories are deferred.
- No new WoW properties, template behavior, gameplay Lua, addon packaging, EPF generation or AzerothCore integration was introduced.
- The separate Xvfb GUI application smoke could not run in this environment.
- No fresh real-client run was performed, so real WoW compatibility remains limited to the evidence recorded in prior baseline documentation.

## Recommended Phase 3 priorities

1. Build multi-object movement and arrange commands on `SemanticSelection` and `SemanticEditingSession` without introducing another mutation path.
2. Define V2 editor-only groups and their lock/selection semantics before implementing group transforms.
3. Add visual resize handles with explicit anchor-preservation rules and one transaction per gesture.
4. Add native layer/order controls, asset picking/import portability, texCoords and supported font styling with preview/export conformance tests.
5. Add an Xvfb-backed application smoke job in CI and continue periodic recorded WoW 3.3.5a acceptance runs.

## Phase 2 completion decision

Phase 2 is complete for its defined automated scope: active V2 editing is semantic-ID based, validated, lock-aware, reversible and independent of V1 document objects. Phase 3 may begin from this foundation. A fresh real-client acceptance pass remains required before making any new client-compatibility or release-readiness claim.
