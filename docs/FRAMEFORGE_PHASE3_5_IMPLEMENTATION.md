# FrameForge Phase 3.5 — Blizzard UI Templates and Preview Restoration

**Status:** Static build-12340 visual acceptance verified in the real application; runtime Lua differences documented  
**Date:** 2026-10-10  
**Branch:** `feature/frameforge-v2`  
**Audited HEAD:** `a8f4e392b8297bbd67ef4cdbc375a1c151847def`

## Outcome

Phase 3.5 restores the discoverable Dungeon Finder starting workflow and a clear Design/Preview experience without restoring the V1 live architecture. The authoritative path remains:

`UiDocument → SemanticEditingSession → UiLayoutResolver → ResolvedUiLayout → existing canvas renderer/exporter`

The New button now presents **Blank Interface** and **Blizzard UI → Dungeon Finder / LFD**. It does not create a blank document before a choice is made. The starter requires a configured, validated WoW 3.3.5a build-12340 client and reports missing source XML or artwork without replacing the current document.

## Original workflow findings

- V1's `NewDungeonFinderProject` validated the configured client, materialized `Interface\FrameXML\LFDFrame.xml`, imported it as a V1 `Project`, built a protected editor group, and materialized its artwork.
- The reusable infrastructure remains present: MPQ lookup, client/build validation, asset cache/provenance, texture normalization, BLP/TGA decoding, template registry loading, and the canvas visual layers.
- V1 represented stock controls and authored controls in the same `Project`; its lock group prevented casual edits. Preview modes and design-time state mechanisms also remained available, but the V2 toolbar did not expose equivalent workflows.
- V2 retained native layout and content rendering, yet New immediately created a blank V2 document, Preview still showed selection chrome/handles, interactions were not comprehensively suppressed, and the Dungeon Finder entry point was absent.
- Phase 3.5 reuses the asset and rendering infrastructure. It does not reuse the V1 `Project`, `UiDocumentProjection`, or V1 `LayoutResolver` for the starter or live editor.

## V2-native Dungeon Finder starter

`V2DungeonFinderStarter` is a deliberately narrow, read-only reader for the client-owned `LFDFrame.xml`. It locates `LFDParentFrame` and maps its supported structural frames, buttons, status bars, textures, font strings, sizes, anchors, texture crops, colors, visibility, strata, levels, and asset references directly into V2 nodes.

The reader is not a new general FrameXML import framework. Unsupported stock widget types retain frame-like structure. Stock `inherits` identities are retained as editor-only reference provenance (`ReferenceTemplate`), not assigned as authored V2 button templates; this avoids falsely treating arbitrary stock relationships as supported export templates. Missing/inherited geometry that cannot be established from the source or approved template registry remains visibly unresolved instead of being silently authored.

No Blizzard artwork is bundled or downloaded. The existing client provider materializes the source XML and every referenced texture into the existing cache. Project creation fails with an actionable diagnostic if any required artwork is unavailable; no generic substitute is introduced.

## Reference composition architecture

The persistent model remains one `UiDocument`. Editor metadata adds:

- `SemanticReferenceComposition`: identity, source identity, reference root, lock group, members, and original node baselines.
- Reference-only node metadata: composition identity, source path, and stock template provenance.
- `SemanticPreviewState`: named editor-only presentation overrides.

The stock subtree is marked reference-only and grouped under a protected reference wrapper. It is locked by default, selectable, expandable/collapsible in the normal semantic hierarchy, and rendered through the normal V2 layout/canvas path. Authored controls created while the reference is selected are redirected to the composition root, so they remain authored overlays rather than children silently absorbed into the stock reference.

Unlocking permits ordinary semantic move/resize edits to reference nodes. Those edits persist in the V2 project as editor-only reference presentation and are undoable. **Restore reference** replaces all reference members' structural/layout values with their captured client-derived baselines. Neither unlocked changes nor the baseline become authored runtime controls.

## Design and Preview

An always-visible V2 toolbar selector exposes **DESIGN | PREVIEW**.

- Design uses the existing Hybrid canvas mode: semantic selection, outlines, anchors, resize handles, grid/snapping, groups, and edit commands remain available.
- Preview uses the existing Preview canvas mode and the same `ResolvedUiLayout` plus the same visual content layer. It hides selection/debug overlays, handles, grid, and guides. Pointer edits, semantic commands, undo/redo, drag, resize, and keyboard nudges are rejected while Preview is active.

Switching modes does not create a second document, layout resolver, or renderer. Tests verify unchanged coordinates and authored serialization.

## Preview states

`DocumentEditorMetadata.PreviewStates` contains named, editor-only override sets. Supported transient values are visibility, display text, status progress, and texture reference. `UiPreviewPresentation` creates a presentation copy and applies visibility to the resolved view; it never updates the editing session's authored document.

The toolbar labels these values **Simulation**, begins with **XML Defaults**, and the Dungeon Finder starter supplies one **Dungeon Finder open** state. That state transiently shows the source-authored hidden root, matching V1's design-time behavior without changing or exporting the authored flag. No gameplay Lua is generated and no Native Hunts-specific state is hardcoded.

## Export boundary

The existing V2 exporter remains responsible for output. Its build input is filtered to remove reference-only nodes before normal validation and writing. Root/child membership is cleaned, preview/reference metadata is excluded, and an authored anchor aimed at a named stock reference becomes a legitimate external-global anchor. Unused reference-only external declarations are removed.

Consequently, an authored overlay exports normally while `LFDParentFrame`, its artwork, unlocked reference edits, and preview simulations do not appear as duplicated native controls. Existing export validation remains active, and golden fixtures were not modified.

## Labels and inspector

V2 canvas labels now prefer `DisplayLabel`, fall back to `RuntimeName`, and retain the `SemanticId` only as the internal selection key/final diagnostic fallback. Hierarchy and editing identity are unchanged.

Frequently edited semantic properties remain in the main inspector. Template registry status, provenance, asset details, and verbose diagnostics are grouped under the existing **Advanced · templates and diagnostics** expander. A selected reference exposes its source/export semantics and explicit lock/unlock and restore actions.

## Files added

- `src/FrameForge.Core/Semantics/V2/UiPreviewPresentation.cs`
- `src/FrameForge.Desktop/Services/V2DungeonFinderStarter.cs`
- `tests/FrameForge.Desktop.Tests/V2BlizzardReferenceTests.cs`
- `docs/FRAMEFORGE_PHASE3_5_IMPLEMENTATION.md`

## Principal files modified

- `src/FrameForge.Core/Export/V2FrameXmlExport.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocument.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentCodec.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentEditor.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentEditor.Visual.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentValidator.cs`
- `src/FrameForge.Desktop/Controls/LayoutCanvas.cs`
- `src/FrameForge.Desktop/Rendering/DebugOverlayLayer.cs`
- `src/FrameForge.Desktop/Rendering/ICanvasLayer.cs`
- `src/FrameForge.Desktop/Rendering/SelectionOverlayLayer.cs`
- `src/FrameForge.Desktop/Rendering/VisualContentLayer.cs`
- `src/FrameForge.Desktop/ViewModels/FrameTreeNode.cs`
- `src/FrameForge.Desktop/ViewModels/MainWindowViewModel.cs`
- `src/FrameForge.Desktop/ViewModels/MainWindowViewModel.V2.cs`
- `src/FrameForge.Desktop/Views/MainWindow.axaml`
- `src/FrameForge.Desktop/Views/MainWindow.axaml.cs`
- `docs/FRAMEFORGE_ROADMAP.md`

Phase 0–3 changes already present in the working tree were preserved. No commit or push was made.

## Verification results

Executed on 2026-10-10 in Release configuration:

| Verification | Result |
|---|---:|
| `dotnet build FrameForge.slnx -c Release --no-restore -m:1 /nodeReuse:false` | PASS — 0 warnings, 0 errors |
| Complete automated suite | PASS — 725 passed, 0 failed, 0 skipped |
| Core tests | PASS — 480 |
| Desktop tests | PASS — 245 |
| Focused tests whose names contain `V2` | PASS — 136 (109 Core + 27 Desktop) |
| New Blizzard/reference tests | PASS — 9 |
| Native Avalonia V2 canvas plus Phase 3.5 Preview canvas | PASS — 4 |
| Existing V2 exporter suite | PASS — 15 |
| Phase 1–3 and V1 regression coverage | PASS as part of the complete suite |
| Golden export compatibility | PASS as part of the complete/export suites; fixtures unchanged |
| Real X11 Dungeon Finder application smoke | PASS — Design, Preview, and authored-overlay screenshots |
| `git diff --check` | PASS |

The headless Avalonia tests execute the real V2 canvas render pipeline. The later focused visual smoke used the actual Release application on the active X11 session and the configured build-12340 client; it did not require Xvfb. This is static editor-rendering evidence, not a claim that FrameForge executes gameplay Lua or that an EPF was deployed.

## Known limitations and remaining usability gaps

- The reader intentionally supports the `LFDParentFrame` starting scenario, not arbitrary FrameXML import.
- Stock scripted behavior, animation, model widgets, Lua, dynamic template sizing, and runtime state are not simulated. Unsupported stock widget types retain frame-like reference structure.
- Some inherited or externally constrained stock geometry may remain unresolved. The stock source/template relationship is preserved as reference provenance rather than exported or flattened.
- Reference restoration uses the baseline captured when the V2 starter document was created. It does not re-read a later-modified client cache automatically.
- The visual result depends on the user's validated build-12340 client files. The recorded screenshots verify this configured client; other installations still require their own comparison.
- Preview states are intentionally lightweight; there is no visual state editor beyond persisted override data and the selector yet.
- The New selector is a compact flyout. A richer gallery and additional Blizzard interfaces are deferred.

## Build-12340 region-dimension correction

An actual application run exposed `FFV2-PROP-019` while creating the Dungeon Finder reference. The authoritative cached source was verified as `Interface\FrameXML\LFDFrame.xml`, SHA-256 `cbb539a5a58b522b2b7bdacaf70ddbdfdf3332ad4b8c590d226460b70e209ee4`.

The source contains fourteen `FontString` regions below `LFDParentFrame` with an explicit positive width and `<AbsDimension ... y="0"/>`. They inherit `QuestFont` or `GameFontNormal`. In WoW these are automatic/content-dependent text heights, not authored zero-area rectangles. Three of the fourteen are beneath `ScrollChild`, which the initial narrow reader also failed to traverse.

Before the correction, the reader copied `0` into `RegionProperties.Height`; strict V2 validation correctly rejected each value. After the correction:

- A zero axis on a client-derived `Texture` or `FontString` is retained as editor reference provenance and represented by a missing authored dimension.
- Positive and negative source values remain explicit. Negative source dimensions still fail strict validation and now identify the affected display label in the creation error.
- Opposing anchors and `setAllPoints` remain available to derive geometry in `UiLayoutResolver`; supported template/font identity remains preserved without being flattened into authored dimensions.
- A single-anchor automatic text height remains unresolved and receives `FFV2-REF-GEOMETRY-001` plus the normal `FFV2L-HEIGHT` layout diagnostic. No fallback height is invented.
- `ScrollChild` structural nodes are traversed, so the three valid nested regions are no longer discarded.

The initial opt-in real-client regression exercised the exact hashed XML, the configured `/home/smithkt/WoW-335a` client, the managed cache, and the application view-model command. It established that direct ingestion succeeded with 72 reference members (10 textures and 22 FontStrings), all fourteen automatic-height regions preserved absent dimensions, no semantic errors, a locked hierarchy, and correct export exclusion. That was ingestion evidence, not visual acceptance.

Before the rendering correction, native layout resolved 20 of 73 root/reference elements and reported 41 anchor-target, 46 height, and 32 width diagnostics. The final template-expanded reference resolves 175 of 219 elements, including 67 of 78 textures; 25 textures are visible in the open state. Remaining diagnostics are 22 anchor-target, 22 height, and 9 width errors, primarily localized/runtime text measurement and dependent runtime panels. See `FRAMEFORGE_DUNGEON_FINDER_RENDERING_FIX.md`.

## Acceptance status

The requested static Phase 3.5 acceptance criteria are met by implementation, automated evidence, and actual-application screenshots: Dungeon Finder is selectable, its client-owned structured reference is protected and recognizably rendered, inherited textures appear in Design and Preview, authored overlays remain independent, simulations do not mutate/export authored XML, stock controls are excluded from export, and the complete suite passes.

The subsequent rendering correction expanded the actual client-owned template closure, restored nested absolute offsets and reference-only opposing-anchor sizing, and added region visibility. The final real-client reference has 218 members: 175 of 219 layout elements resolve, 67 of 78 textures resolve, and 25 textures are visible in the open state. The Release application was exercised on the active X11 desktop and captured Design, Preview, and authored-overlay screenshots. See `FRAMEFORGE_DUNGEON_FINDER_RENDERING_FIX.md` for exact evidence and remaining runtime-only differences.

Pixel-perfect gameplay fidelity remains outside this claim because Lua-selected atlas crops, localized automatic sizing, and runtime-populated states are intentionally not simulated.
