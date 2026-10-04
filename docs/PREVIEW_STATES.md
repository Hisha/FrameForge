# Design-Time Preview States

FrameXML often declares the shape of an interface while Lua supplies the presentation that a
player actually sees. FrameForge does not execute addon Lua. Instead, its preview-state layer can
apply an explicit, reviewable set of **design-time-only** effective values over an imported
project. The imported model, source XML, and serialized `.fforge.json` remain unchanged.

## Layering and safety

The presentation pipeline keeps four concerns separate:

1. the imported project is the source of declared frames and visual metadata;
2. a preview-state catalog contributes transient visibility, text, texture, status value, button
   state, color, or anchor overrides;
3. stock definitions contribute external-frame, inherited-template, and font presentation data;
4. asset resolution loads project or machine-local client resources through the normal resolver.

`PreviewStateRegistry` is the generic lookup, validation, and non-mutating application engine.
`PreviewOverrideSet` contains the selected state's valid overrides and diagnostics for missing
targets. `NativeHuntsPreviewStates` is the first project-specific catalog. Rendering layers consume
the effective project and the small amount of presentation-only metadata they need; they do not
contain Native Hunts state branches.

Selecting **XML Defaults** supplies no overrides and therefore reproduces the pre-Phase-5C result.
Every other state is applied to a transient project clone. Saving always serializes the original
project. The selection is intentionally session-only in Phase 5C: it creates no project-format
churn, remains compatible with older v1 projects, and resets to XML Defaults whenever a project is
opened.

The Inspector identifies overridden values, the active state, source/effective values, and whether
text is real behavior copy or design sample content. A missing catalog target is a diagnostic, not
an exception or an invented frame.

## Native Hunts behavior mapping

The initial catalog is based on inspection of the real `NativeHuntsFrame.xml` and
`NativeHuntsFrame.lua`; those files are read as evidence and are never modified or executed.

| State | Effective presentation |
| --- | --- |
| XML Defaults | No preview contributions; XML visibility and runtime placeholders are preserved. |
| Idle | Root, Idle, and Hunt Record shown; Identity and Hunt State hidden; `NO ACTIVE HUNT` and the Huntmaster prompt supplied; progress, trail decoration, and ready icon hidden. |
| Standard Hunt | Identity and Hunt State shown; Idle hidden; standard icon and tier selected; Lua tracking layout used; progress and trail decoration shown. |
| Elite Hunt | Same tracking behavior with the elite icon and tier selected. |
| Hunt Complete | Identity and Hunt State shown; complete/ready copy and ready icon supplied; progress and trail decoration hidden; Lua completion anchors used. |

All non-default Native Hunts states reproduce the `PanelTemplates_SetTab(..., 2)` result by showing
the Dungeon Finder tab normally and the Hunts tab with the stock selected/disabled atlas pieces and
selected font style. The Hunt Record is visible and populated in the same states. `Elite Today` uses
the green color demonstrated by the Lua code, and the seal icon is shown for the representative
available-seal state.

The inspected Lua uses a `0..100` progress range and percent text. Tracking places the primary and
secondary text at `(15, -34)` and `(15, -78)` relative to the state panel; completion uses
`(15, -43)` and `(15, -75)`. The completed state displays `HUNT COMPLETE`, `READY TO TURN IN`, a
return-to-Huntmaster instruction, and the ready icon rather than a progress bar.

## Real behavior and sample content

Visibility rules, fixed state labels, icon selection, progress semantics, anchors, color behavior,
and tab selection come from the inspected implementation. Names, locations, progress values, and
record counts below are FrameForge design samples; they are not live WoW data:

- Standard: Ashfang in Durotar, issued by Huntmaster Gorrak in Orgrimmar, 60%.
- Elite: The Oathbreaker in The Barrens, issued by Huntmaster Gorrak in Orgrimmar, 65%.
- Record: 12 Standard hunts, 2 Elite hunts, Elite available today, and 3 Huntmaster's Seals.

Runtime text receives the already-resolved `GameFont` presentation—font, size, color, shadow,
outline, justification, clipping, and geometry—unless the real behavior specifically supplies a
state color. Runtime-selected icons still travel through the ordinary asset resolver and decoder.

## Current limits and extension path

- FrameForge does not execute Lua, react to gameplay events, or emulate a WoW client.
- The catalog exposes the five requested design states. The Lua also has located and final-
  confrontation presentations, but they are not added merely to enlarge the selector.
- Text-color overrides apply to the whole FontString. Mixed inline WoW color markup is not parsed.
- Normal and selected stock tab states are supported; this is not an interactive hover/click model.
- Built-in definitions are code-owned and there is no user-facing state editor yet.

Future Eitrigg interfaces can add catalogs behind `IPreviewStateRegistry` while reusing the same
override, validation, Inspector, template, asset, and rendering pipeline. Native Social, AQ UI, or
vendor support should be driven by inspected source behavior rather than renderer conditionals.
