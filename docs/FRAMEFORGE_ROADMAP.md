# FrameForge — Implementation Roadmap
**Status:** Phases 0–3.6 implemented; build-12340 LFD reference customization and asset workflow verified in the real application · **Date:** 2026-10-10  
**Baseline:** audited `a8f4e39`; V1 reference `7430904`.

## Delivery rules
- Work in small, reversible changes with explicit tests and reviewable commits.
- No greenfield rewrite; preserve validated Core/Desktop, asset decoding, canvas and native V2 export.
- Do not remove a legacy subsystem until its retained capabilities are replaced and acceptance evidence exists.
- Each milestone must include source/tests touched, evidence, limitations and rollback approach.
- Mark statuses **Not started / In progress / Verified / Blocked**. Nothing below is pre-marked verified by this roadmap.

## Phase 0 — Establish reproducible truth (P0)
**Status:** Verified. See `FRAMEFORGE_PHASE0_BASELINE.md`; the original test defect was corrected as test setup and the 661-test baseline passed. The user also reports prior Eitrigg/WoW manipulation of the golden EPF, without a complete reproducible evidence bundle in this repository.

**Scope:** clean checkout/build; execute tests outside audit sandbox; capture failing tests; archive V1 and V2 sample projects/golden exports; perform a minimal real-client V2 host test and record EPF staging behavior.
**Exit:** reproducible test baseline, known-good sample or documented blocking defect, client/build/host/export hash evidence. No assumption that checked-in fixtures equal client acceptance.
**Dependency:** none.

## Phase 1 — Replace the central architectural seam (P0)
**Status:** Verified by Release build, full automated suite, focused V2/layout/export tests, golden fixtures and Avalonia headless rendering. See `FRAMEFORGE_PHASE1_IMPLEMENTATION.md`. A fresh recorded WoW acceptance run remains a release/fidelity activity, not evidence inferred from these tests.

**Scope:** keep `UiDocument` authoritative; introduce typed `ResolvedUiLayout` from V2 + explicit host/build profile; reuse proven `LayoutResolver` math; unify diagnostics; migrate renderer/selection reads away from `UiDocumentProjection`; add semantic conformance tests against native exporter.
**Exit:** a representative V2 project can be edited, previewed, saved and exported without a V1 `Project` in its live editor/render path. Multiple-anchor and unresolved external-reference cases have explicit diagnostics.
**Dependencies:** Phase 0 baseline.
**Guardrail:** preserve the existing V2 exporter rather than rewriting it.

## Phase 2 — Consolidate editing foundation (P0)
**Status:** Verified by Release build, complete and focused V2 suites, semantic editing/transaction tests, unchanged golden export fixtures and Avalonia headless canvas rendering. See `FRAMEFORGE_PHASE2_IMPLEMENTATION.md`. The separate Xvfb application smoke remains unexecuted because `xvfb-run` is unavailable; no fresh WoW client claim is made.

**Scope:** V2-native hierarchy and selection IDs; one inspector; atomic semantic commands; enforce locks; undo/redo transaction boundaries; move responsibilities out of the 4,479-line coordinator incrementally.
**Exit:** create, select, move, resize numerically, reparent, rename, undo/redo, save/reopen and export preserve identities and references. UI never edits render projections.
**Dependencies:** Phase 1 typed layout and command gateway.

## Phase 3 — Restore productive visual editing (P1)
**Status:** Verified for the visual-editing scope: semantic multi-selection and movement, direct resize, align/distribute, keyboard precision, grid snapping, paint-band ordering, and persistent editor-only groups. Release build and all 716 automated tests pass. See `FRAMEFORGE_PHASE3_IMPLEMENTATION.md`. Asset picker/import portability, richer font styling, texCoords, and overlap/reveal tooling remain roadmap work and are not claimed complete.

**Scope:** multi-select/group movement; align/distribute; locks and editor-only groups; asset picker and portable imports; rich supported font styling; texCoords; native layering controls; overlap/reveal; visual resize handles.
**Exit:** V1 reference workflows reproduced on V2 projects, with keyboard/mouse interaction tests and no preview/export semantic divergence.
**Dependencies:** Phase 2.
**Order within phase:** selection/locks → arrange → assets/text → order/resize.

## Phase 3.5 — Restore Blizzard starters and Preview UX (P1)
**Status:** Verified for the requested static scope. The hashed build-12340 source and its required template XML now expand into a protected 218-member V2 reference. Design and Preview render 25 visible real-client textures through the normal canvas; an authored overlay remains independent; and reference-only controls remain excluded from export. Release build and all 725 automated tests pass. A focused run of the real application on the active X11 session captured Design, Preview, and overlay evidence. See `FRAMEFORGE_PHASE3_5_IMPLEMENTATION.md` and `FRAMEFORGE_DUNGEON_FINDER_RENDERING_FIX.md`. Lua-selected atlas crops, localized automatic text measurement, and runtime-populated panels remain explicit Phase 4 fidelity work.

**Scope delivered:** narrow direct LFD starter; existing MPQ/cache/BLP/TGA/template infrastructure; locked/restorable reference composition; Design/Preview selector; editor-only simulations; display labels; inspector provenance organization; export filtering and regression coverage.

**Deferred:** generic Blizzard interface gallery/import, scripted/dynamic stock behavior, localized automatic text measurement, a full preview-state authoring UI, automatic refresh from changed client archives, and pixel-perfect comparison with a running WoW client.

**Guardrail:** reference composition remains editor-only and cannot silently become duplicated authored FrameXML.

## Phase 3.6 — Asset Browser and Reference Customization (P1)

**Status:** Verified for the requested editor scope. Reference hide/show is persisted editor metadata and exporter-neutral; the configured build-12340 client indexes 14,071 partial-coverage listfile/cache texture paths; stock and project artwork can be previewed and assigned to authored Texture controls; PNG imports are deterministically prepared as validated 32-bit TGA; rectangular texCoords edit with undo/redo; and reference/authored hierarchy filtering remains V2-native. Release build, all 730 automated tests, actual-client focused tests, and the real X11 workflow smoke pass. See `FRAMEFORGE_PHASE3_6_IMPLEMENTATION.md`.

**Deferred:** non-rectangular texture-coordinate mappings, a BLP encoder, dynamic Lua-selected atlas crops, complete discovery of MPQ entries absent from listfiles, and downstream EPF/MPQ/live-client deployment.

**Guardrail:** editor reference visibility cannot imply WoW runtime visibility, and stock Blizzard bytes are never copied into project/export artwork.

## Phase 4 — Improve client fidelity (P0/P1; ongoing)
**Scope:** host profiles, typed anchor constraints, native paint order, build-12340 template registry consolidation, font/texture rendering and explicit approximation diagnostics. Test against the actual client at every material capability change.
**Exit:** acceptance corpus covers fixed/fill host, parent/sibling/external anchors, multi-target constraints, frame/region ordering, font/texture/template cases; unresolved cases fail visibly.
**Dependencies:** begins during Phase 1, continues through Phase 5.
**Guardrail:** do not expand template support without build-12340 evidence.

## Phase 5 — Stabilize module handoff (P1)
**Scope:** one V2 export application service for UI/CLI; deterministic `Design.xml`, `Artwork/`, manifest and validation; versioned identity/resource/dependency contract; documented module-owned EPF adapter.
**Exit:** representative `mod-native-hunts` visual export is integrated by its module, staged by mod-content-manager and rendered in WoW; revision/re-export works without hand-rebuilding layout.
**Dependencies:** Phase 1, fidelity checkpoints, module host contract.
**Out of scope:** FrameForge generating gameplay Lua, EPFs or MPQs.

## Phase 6 — Retire duplication safely (P1)
**Scope:** remove V1 active authoring, projection, duplicate inspector, old static/functional exporters, product-specific preview logic and redundant template resolver only after parity. Preserve historical migration/reference tools only if explicitly needed.
**Exit:** one persistent model, one live layout path, one template policy, one supported export contract; migration fidelity reporting available for supported legacy projects.
**Dependencies:** Phases 2–5 acceptance and real-client evidence.
**Stop rule:** do not retire V1 while any retained user capability still depends on it.

## Phase 7 — Release readiness (P2)
**Scope:** consistent version metadata/docs; CI tests + PR headless/Xvfb smoke; Linux/Windows packaging checks; onboarding/tutorial; known limitations.
**Exit:** clean CI, tested packages, reproducible client acceptance record, no silent export approximations.

## Cross-phase risks and mitigations
| Risk | Mitigation |
|---|---|
| V1/V2 semantic drift | V2-only commands and resolved snapshot; contract tests |
| Breaking good V1 UX | capability-by-capability parity fixtures before deletion |
| WoW differs from preview | recorded client acceptance at Phase 0 and each fidelity milestone |
| Template parsing scope explosion | build-12340 evidence-driven allowlist/registry |
| Legacy projects lose data | explicit migration and fidelity report |
| Coordinator refactor destabilizes UI | extract one responsibility at a time with headless smoke |
| False confidence from tests | distinguish static, integration and real-client results |

## Decisions to resolve at gates
**After Phase 0:** host contract and actual client blockers.  
**Before Phase 3:** visual groups, variants, PNG conversion and resize interaction semantics.  
**Before Phase 6:** migration support window, FrameXML import/reference tool retention and functional patcher disposition.

## First implementation ticket (only after approval)
**Ticket FF-001: Baseline and minimal client proof.** Run clean build/tests; capture failing cases; export the smallest module-hosted V2 sample; use module-owned host and existing content-manager pipeline; record client outcome and file hashes. Do not refactor during this ticket.

**Source basis:** three audit reports dated 2026-10-10. Priorities and phase boundaries are planning recommendations, not facts verified by the repository.
