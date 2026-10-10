# FrameForge — Test Strategy
**Status:** Proposed acceptance and regression policy · **Date:** 2026-10-10  
**Target:** WoW 3.3.5a build 12340, AzerothCore module-hosted visual interfaces.

## 1. Quality objective
Prevent a recurrence of V1→V2 capability loss while proving that authored visuals map predictably to native WoW FrameXML. Separate **code correctness**, **preview/export conformance**, **pipeline integration**, and **actual client behavior**. No single level substitutes for another.

## 2. Evidence states
- **Not run:** test exists but no execution evidence.
- **Pass / Fail:** executable test result with environment and revision.
- **Blocked:** environmental limitation recorded (e.g. VSTest local TCP socket).
- **Client verified:** explicit build-12340 in-game observation with artifacts and steps.
- **Approximate / Unsupported:** preview limitation attached to affected property.

Audit baseline: 535 `[Fact]` and 27 `[Theory]` declarations in 36 test files, **not verified passing** in the audit sandbox. Disposable Release compilation succeeded; real-client V2 rendering and operational EPF staging remain unverified.

## 3. Test pyramid
| Layer | Purpose | Representative assertions |
|---|---|---|
| Semantic/unit | Stable V2 identity and editing | rename does not break refs; invalid owners/anchors rejected; locks enforced; undo/redo inverses |
| Layout conformance | One typed interpretation | same anchors, host profile, bounds/order/visibility for editor and exporter validation |
| Render/headless | Designer UX | select/drag/multi-select/align/resize, hit testing, template preview warnings |
| Export/golden | Deterministic native files | XML parse, native containment, identities, resources, hashes, no Lua/TOC/EPF |
| Pipeline integration | Module-owned packaging | host before design, schema-3 adapter mapping, MPQ staging and file load order |
| Real-client acceptance | WoW truth | actual anchors, fonts, paint order, textures, button identities and status bars |

## 4. Permanent regression corpus
Maintain versioned sample projects and expected outputs for:
1. Frame + texture + FontString + Button under module host.
2. Parent and sibling anchors; all nine points; offsets; reparenting.
3. Fixed and fill host previews at multiple dimensions/scales.
4. Multi-anchor stretching and different-target constraints; unresolved references diagnosed.
5. Region layer/sublevel versus frame strata/level; sibling and cross-owner overlap.
6. Blizzard template inheritance, missing assets, font provenance and approximate scripted effects.
7. BLP/TGA textures, texCoords, tint/alpha and unsupported PNG preflight.
8. StatusBar visual properties; module-owned runtime values.
9. Save/reopen/export deterministic equivalence.
10. V1 migration with exact/approximated/dropped/blocked fidelity categories.
11. V1 UX parity: multi-select, group drag, align/distribute, locks, asset picker, text styling, ordering, reveal/overlap.
12. Negative cases: duplicate runtime names, invalid references, unsafe paths, unsupported properties.

## 5. Real-client acceptance protocol
For each client run record:
- Date, FrameForge Git revision, exported manifest and file hashes.
- WoW executable **build 12340** and client/locale asset profile.
- Module revision, module host runtime name, load order and EPF/MPQ build identity.
- Exact steps, expected outcome, observed outcome, screenshots/log excerpts and verdict.
- Any approximation, manual workaround or known limitation.

Minimum early smoke: module-owned host with static frame, image, label and button; verify native placement and that module Lua can look up the named button (behavior itself remains module-owned). Repeat after changing one anchor and one texture in FrameForge, reopening and re-exporting.

## 6. CI gates
**Every PR:** restore/build, Core/Desktop tests, golden export, headless rendering and application smoke (Xvfb where necessary), artifact schema validation.  
**Every release:** PR suite plus self-contained Linux/Windows package checks, deterministic resource audit and documented client-acceptance status.  
**When changing anchors/templates/export:** targeted client acceptance is required before claiming WoW fidelity.

A sandbox restriction is a **Blocked** result, never a Pass. Run blocked tests in a suitable environment before release.

## 7. Removal gates
A V1 subsystem can be retired only when:
- retained V1 UX cases pass against V2 semantic IDs;
- live renderer/hierarchy no longer depends on V1 `Project`;
- native V2 exporter/manifest and client acceptance are recorded;
- supported historical projects migrate with explicit fidelity reporting;
- no unsupported cases are silently converted into guessed geometry.

## 8. Defect triage
Classify issues as **semantic model**, **command/UX**, **layout resolver**, **preview approximation**, **export**, **module integration**, or **real client**. Fix the earliest authoritative layer; add a regression fixture before closing. Never patch a visual discrepancy solely in the renderer if the underlying layout semantics are wrong.

## 9. Evidence register template
| Case ID | Revision | Environment | Layer | Expected | Actual | Status | Artifact/hash |
|---|---|---|---|---|---|---|---|
| FF-CLIENT-001 | TBD | WoW 12340 + module host | Client | Frame/text/button positioned correctly | Not run | Not run | TBD |

**Source basis:** `FRAMEFORGE_AUDIT.md`, `FRAMEFORGE_CAPABILITY_MATRIX.md`, `FRAMEFORGE_ARCHITECTURE_RECOMMENDATIONS.md`. All test policy and cases above are proposed until executed.
