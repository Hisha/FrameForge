# FrameForge — Product Direction
**Status:** Proposed authoritative direction · **Date:** 2026-10-10  
**Evidence baseline:** FrameForge audit of `feature/frameforge-v2` at `a8f4e39`; V1 baseline `7430904` (`v0.2.6`).

## 1. Mission
FrameForge is a cross-platform visual frontend designer for **World of Warcraft 3.3.5a, build 12340**, supporting custom **AzerothCore modules**. It lets a developer construct, inspect, edit, save, and export native WoW visual layouts with predictable anchoring and reusable client artwork.

**Definition of success:** An interface designed in FrameForge can be integrated by a module, rendered in the real client as intended, reopened, revised, and redeployed without manually reconstructing the visual layout.

## 2. Product boundaries
| Owner | Responsibility |
|---|---|
| **FrameForge** | Visual structure, named controls, native frame/region hierarchy, anchors, dimensions, textures, fonts, static visual properties, preview, diagnostics, `Design.xml`, resources, identity/dependency manifest. |
| **Consuming AzerothCore module** | Lua behavior, handlers, events, runtime values and visibility, gameplay and server communication, module-owned host and integration/load-order contract. |
| **mod-content-manager** | EPF processing, MPQ generation, staging, publishing and distribution; a module-owned adapter maps FrameForge output to the EPF manifest. |

FrameForge **does not** author gameplay Lua, event handlers, AzerothCore server code, standalone addon/TOC packages, EPFs, MPQs, or deployment operations. No multi-version WoW compatibility target. It is not a WoW emulator.

## 3. Product principles
1. **One source of truth:** V2 `UiDocument` is the only persistent authoring model.
2. **Visual ease without false fidelity:** the editor should feel like V1, but every authored property must have a valid native representation or a clear unsupported diagnostic.
3. **Native semantics first:** distinguish structural owner from anchor target; display label from runtime name; frame strata/level from region layer/sublevel; authored from template-derived properties.
4. **Explicit host contract:** composition root and module-owned host are mandatory. Preview host dimensions are assumptions, never silently serialized runtime facts.
5. **Fail closed:** do not invent UIParent fallbacks, unknown dimensions, template effects, or runtime Lua behavior.
6. **Preserve work:** saving, reopening, migrating, and re-exporting must be deterministic and transparent about losses.
7. **Small default surface:** one Design workspace, advanced diagnostics available on demand.
8. **Client evidence matters:** passing XML and unit tests is not proof of real-client fidelity.

## 4. Target architecture
```text
UiDocument (schema V2; sole persistent authority)
    ├── UiDocumentCodec / Validator
    ├── Semantic command layer (atomic edits; history; locks)
    └── Build-12340 template + asset resolution
                  ↓
       ResolvedUiLayout (typed, diagnostic-rich)
           ├── V2-native hierarchy/selection/canvas renderer
           └── Export conformance validation
                  ↓
       V2 native FrameXML exporter
           ├── Design.xml
           ├── Artwork/ (TGA/BLP)
           ├── frameforge.manifest.json
           └── validation diagnostics
                  ↓
          module-owned integration adapter → EPF pipeline
```
The XML writer may serialize authored typed anchors directly; conformance checks must verify that the emitted structure agrees with the resolved interpretation. The editor must never mutate a V1 projection.

## 5. Essential authoring capabilities
**Baseline:** create/select/rename/delete frames and native regions; tree hierarchy; correct parent and anchor editing; drag and numeric positioning; dimensions; texture and asset selection; text/font styling; buttons and status bars as visual controls; draw order; lock/unlock; project save/load; validated export.

**Recovered V1 strengths:** multi-selection/group drag, align/distribute, overlap cycling/reveal, editor-only groups and locks, image import/picker, rich native-supported text styles, native-aware order controls, helpful template provenance.

**New baseline gaps:** undo/redo and visual resize handles. These are not historical V1 features and need deliberate design.

**Conditional capabilities:** declarative visual variants/presets, generic FrameXML import, and advanced template support only where they demonstrably serve the visual design workflow. None may introduce gameplay-state machinery or a second authoring model.

## 6. Layout and rendering contract
- Semantic IDs are immutable editor identities; runtime names are optional, validated export identities.
- Ownership and anchor references are typed; no ambiguous nullable-string fallback.
- Resolve layout using an explicit client build/template/host preview profile.
- Capture effective dimensions, owner/anchor targets, resolved rectangles or reasons unresolved, native order, visibility, dependencies, and limitations.
- Preserve authored multi-anchor constraints. If the preview cannot solve a native case faithfully, report it rather than guessing.
- Use the existing coordinate/anchor math and canvas infrastructure where valid, adapted to V2.
- Preview approximations (font metrics, blending, template scripts) must be visible in diagnostics.

## 7. UX direction
One primary Design view: hierarchy on left, canvas center, selected-object inspector on right, compact toolbar and collapsible diagnostics. Advanced source/provenance and anchor traces are inspector sections or overlays, **not parallel editing modes**. Editing commands and lock enforcement live below the UI.

## 8. Import, compatibility and cleanup
Historical V1 projects require an **explicit V1→V2 migration** if continued use is supported, with a fidelity report: exact / approximated / dropped / blocked. Never silently infer host ownership. Retire V1 persistent editing, projected renderer dependence, duplicate template resolver, old exporters and product-specific preview machinery **only after parity and real-client evidence**.

## 9. Non-negotiable acceptance
A representative module-hosted visual interface can be designed, exported, integrated by its module, displayed in a real build-12340 client, reopened, edited and redeployed. Named controls remain accessible to module Lua. Anchors, hierarchy, resources and native layering behave as documented. Unsupported cases produce actionable diagnostics.

## 10. Evidence and open decisions
**Confirmed by audit:** dual model path; V2 native export and typed semantics; V1 editing regressions; duplicated template/export/inspector systems.  
**Not yet verified:** passing tests in the audit environment; real WoW V2 render; operational EPF staging; exact template/font/paint fidelity.  
**Decisions deferred:** general XML import scope, visual variants, PNG conversion policy, editor-only grouping semantics, migration compatibility horizon. Resolve via focused evidence and user workflow tests, not assumption.

**Source basis:** `FRAMEFORGE_AUDIT.md`, `FRAMEFORGE_CAPABILITY_MATRIX.md`, `FRAMEFORGE_ARCHITECTURE_RECOMMENDATIONS.md` (audit revision `a8f4e39`).
