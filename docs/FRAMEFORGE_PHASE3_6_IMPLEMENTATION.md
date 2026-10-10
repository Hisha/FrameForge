# FrameForge Phase 3.6 — Asset Browser and Reference Customization

**Status:** Implemented and verified in the Release application with the configured WoW 3.3.5a build-12340 client  
**Date:** 2026-10-10  
**Branch:** `feature/frameforge-v2`  
**Audited base revision:** `a8f4e392b8297bbd67ef4cdbc375a1c151847def`

## Outcome

Phase 3.6 makes the protected Dungeon Finder composition practical as a design reference without changing its native ownership or exporting Blizzard reference controls. A designer can hide and restore reference elements, search the configured client visually, assign stock artwork to an authored Texture, import PNG/TGA/BLP artwork, prepare PNG artwork as a validated TGA, edit rectangular texture coordinates, and navigate the 218-member hierarchy.

The implementation remains on the V2 `UiDocument`, `SemanticEditingSession`, `UiLayoutResolver`, native canvas, and existing V2 exporter paths. It introduces no V1 `Project` projection, no second persistent model, no Blizzard artwork bundle, and no EPF/MPQ/deployment behavior.

## Reference visibility model

`DocumentEditorMetadata.HiddenReferenceNodes` stores stable semantic IDs for reference-only elements hidden from the editor presentation. `UiPreviewPresentation.ApplyVisibility` applies this metadata after layout resolution in both Design and Preview. It computes effective visibility through native ownership, so showing a child cannot reveal it while an ancestor remains hidden.

The semantic command gateway provides individual and subtree hide/show operations with normal undo/redo snapshots. Restoring a reference composition clears its visibility overrides and restores the client-derived baseline. Strict validation rejects missing, authored, or duplicate hidden-node identities. Export strips this metadata together with reference-only controls before export validation, so hide/show cannot alter generated FrameXML.

The inspector provides **Hide element**, **Hide subtree**, and **Restore original**, plus an explicit notice that this is only an editor aid and does not hide the Blizzard control in WoW. Hidden nodes remain in the tree with a `◌` indicator and remain selectable. Protected references still cannot be structurally deleted; Delete is disabled with an explanatory tooltip.

## Client asset discovery and browser

`WoWClientAssetProvider` now exposes an optional read-only catalog capability. It reads each archive's `(listfile)` through the existing managed MPQ reader, merges build-12340 texture names with already materialized cache entries, and never extracts asset bytes during indexing. The configured client produced 14,071 texture paths from 14 of 14 archive listfiles.

MPQ listfiles are optional and can omit otherwise valid hashed entries. The browser therefore labels coverage as partial even when every discovered archive contains a listfile. A typed valid path can still be previewed and materialized on demand.

The asset browser provides:

- directory filtering and name/path search;
- virtualized list rows with serialized lazy thumbnails (at most 64 per browser window);
- a larger selected preview with dimensions, physical format, and alpha status;
- missing/decode diagnostics without blocking the project;
- stock, currently used, cached, and imported project artwork in one V2 Texture workflow.

Stock selection persists only the normalized `Interface\...` path. Blizzard bytes remain in the client-managed cache and are neither copied into the project nor included in export.

## Custom artwork lifecycle

Imported files are copied into the saved project's `assets/` namespace when the selected file is external. `SemanticProjectAsset` records a stable ID, copied source reference, preview reference, prepared project reference, intended client-relative path, dimensions, original format, conversion status, and validation status. These records round-trip in schema V2 editor metadata and appear in the asset browser after reopen.

The three forms are deliberately distinct:

1. **Source artwork** is the portable project copy of the selected PNG, TGA, or supported BLP.
2. **Preview artwork** is the decoded project source used to describe and browse the asset.
3. **Client artwork** is the prepared TGA/BLP referenced by authored FrameXML after export rewriting.

PNG input is decoded through the existing Skia-backed reader and converted deterministically to an uncompressed, top-origin, 32-bit TGA. The output is written below `assets/prepared/` with a source hash in its name, then validated through the normal decoder/export header checks. PNG paths never enter generated FrameXML. TGA and supported BLP inputs remain lossless copies; no speculative BLP encoder was added.

The existing V2 exporter remains the content handoff boundary. It hashes and copies prepared TGA/BLP files to `Artwork/`, rewrites their references into `Interface\FrameForge\Artwork\...`, and lists required artwork in `frameforge.manifest.json`. This is a neutral module handoff manifest. FrameForge does not create an EPF, MPQ, patch activation, server deployment, or Portalkeeper integration; the module-owned adapter and `mod-content-manager` remain responsible for those stages.

## Texture inspector and coordinates

The V2 Texture inspector now includes client browsing, custom import, current asset diagnostics and dimensions, rectangular normalized texture-coordinate controls, and existing RGBA tint/alpha editing. Changes use one semantic transaction, update the canvas immediately, and support undo/redo.

The current V2 schema represents a rectangular crop as `left`, `right`, `top`, and `bottom`. Values must be finite, ordered, and in `[0,1]`; invalid edits are rejected by the inspector and `FFV2-PROP-025`. Export emits the existing build-12340 `<TexCoords>` representation. Arbitrary four-corner/non-rectangular mappings remain unsupported and are disclosed in the inspector.

## Hierarchy navigation

The original Blizzard parent/child graph is unchanged. The hierarchy adds editor-only navigation controls:

- collapse all Blizzard reference subtrees;
- expand/reveal the selected reference path;
- search display labels, runtime names, kinds, and semantic IDs;
- filter all, reference-only, or authored-only controls;
- retain hidden-element indicators and selection;
- retain normal per-node expand/collapse behavior.

No speculative Background/Header/Role grouping changes native ownership.

## Tests added and verification

New regressions cover persisted reference visibility, subtree visibility, hidden ancestors, baseline restore, export neutrality, client listfile/cache discovery, partial-coverage diagnostics, actual build-12340 catalog discovery, texCoord validation/undo/redo/export, PNG import and deterministic TGA preparation, project-asset browsing, save/reopen identity, preview rendering, and PNG runtime-path exclusion.

Executed in Release configuration on 2026-10-10:

| Verification | Result |
|---|---:|
| Release build | PASS — 0 warnings, 0 errors |
| Complete automated suite | PASS — 730 (480 Core + 250 Desktop) |
| Focused tests containing `V2` | PASS — 139 (109 Core + 30 Desktop) |
| V2 exporter | PASS — 15 |
| V1 FrameXML importer compatibility | PASS — 80 |
| Asset/Blizzard/native-canvas focus | PASS — 65 |
| Actual hashed build-12340 Dungeon Finder + client catalog | PASS — 2 |
| Real X11 Phase 3.6 application smoke | PASS |
| `git diff --check` | PASS |

Golden exporter fixtures were not changed. The complete exporter suite passed with the existing expected output.

## Visual acceptance evidence

The Release application ran on the active X11 session against `/home/smithkt/WoW-335a`. The smoke loaded the real 218-member Dungeon Finder reference, hid and restored its three role-button subtrees, opened and searched the real asset browser, previewed the 512×512 `UI-LFG-FRAME.blp`, assigned client artwork to an authored Texture, imported a generated PNG, prepared it as TGA, changed its crop, and rendered Design and Preview. The final composition submitted 27 textures and retained the previously measured 44 explicitly unresolved runtime/automatic-text reference elements.

Evidence is under `docs/evidence/phase3-6/`:

- `frameforge-lfd-design.png`
- `frameforge-phase36-role-selection-hidden.png`
- `frameforge-phase36-role-selection-restored.png`
- `frameforge-phase36-asset-browser-lfg.png`
- `frameforge-phase36-authored-client-texture.png`
- `frameforge-phase36-custom-artwork-cropped-design.png`
- `frameforge-phase36-custom-artwork-cropped-preview.png`
- `frameforge-lfd-preview.png`

These images are actual FrameForge windows, not mockups. They prove editor rendering against local client assets; they do not prove gameplay Lua behavior or deployment through an EPF into a running WoW client.

## Files added

- `src/FrameForge.Desktop/Assets/WowTgaEncoder.cs`
- `docs/FRAMEFORGE_PHASE3_6_IMPLEMENTATION.md`
- `docs/evidence/phase3-6/*.png`

## Principal files modified

- `src/FrameForge.Core/Export/V2FrameXmlExport.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocument.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentCodec.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentEditor.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentEditor.Visual.cs`
- `src/FrameForge.Core/Semantics/V2/UiDocumentValidator.cs`
- `src/FrameForge.Core/Semantics/V2/UiPreviewPresentation.cs`
- `src/FrameForge.Desktop/Assets/StockTextureCatalog.cs`
- `src/FrameForge.Desktop/Assets/WoWClientAssetProvider.cs`
- `src/FrameForge.Desktop/Services/SmokeTest.cs`
- `src/FrameForge.Desktop/ViewModels/FrameTreeNode.cs`
- `src/FrameForge.Desktop/ViewModels/MainWindowViewModel.cs`
- `src/FrameForge.Desktop/ViewModels/MainWindowViewModel.V2.cs`
- `src/FrameForge.Desktop/Views/MainWindow.axaml`
- `src/FrameForge.Desktop/Views/MainWindow.axaml.cs`
- `src/FrameForge.Desktop/Views/StockTextureChooserWindow.cs`
- `tests/FrameForge.Desktop.Tests/V2BlizzardReferenceTests.cs`
- `tests/FrameForge.Desktop.Tests/V2EditorWorkflowTests.cs`
- `tests/FrameForge.Desktop.Tests/WoWClientAssetProviderTests.cs`
- `docs/FRAMEFORGE_ROADMAP.md`

Pre-existing Phase 0–3.5 working-tree changes were preserved. No commit or push was made.

## Known limitations

- Listfile indexing cannot discover MPQ entries omitted from every listfile; direct safe paths remain supported.
- Browser thumbnails are intentionally lazy and bounded. Unresolved entries show diagnostics rather than placeholders presented as valid artwork.
- Texture-coordinate editing is rectangular only.
- BLP decoding is retained; no BLP encoder was added. PNG preparation targets validated uncompressed 32-bit TGA.
- Reference visibility is an editor presentation feature, not a runtime WoW visibility authoring mechanism.
- Dynamic Lua-selected crops, runtime-populated panels, localized automatic text sizing, and the remaining Phase 3.5 reference diagnostics are unchanged.
- No EPF, MPQ, Content Manager, AzerothCore, Portalkeeper, or live-game deployment was performed.

## Acceptance status

The Phase 3.6 editor acceptance criteria are met: stock reference artwork can be hidden/restored without source mutation; client textures can be searched and previewed; authored textures can use stock or imported artwork; PNG input receives a validated TGA preparation path; project assets persist and export through the existing manifest; rectangular texCoords edit and export correctly; the large reference remains navigable; and actual application screenshots demonstrate Design and Preview.

Real WoW runtime behavior and content deployment remain separate downstream acceptance activities and are not claimed by this phase.
