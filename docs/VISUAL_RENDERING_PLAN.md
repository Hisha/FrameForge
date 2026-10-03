# Visual Rendering Plan

FrameForge is a layout designer, not a WoW client emulator. The renderer should reproduce the
authored visual result far enough to design and inspect an interface while keeping every unknown
honest: unresolved artwork, templates, fonts, and Lua state remain explicit gaps rather than
plausible-looking guesses.

This plan is grounded in the canonical `NativeHuntsFrame.xml` fixture. That document imports as 52
model frames (51 source elements plus one synthesized external-frame stand-in), including 21
textures, 20 font strings, two buttons, and one status bar.

## 1. Already modeled and renderable

- Resolved bounds, anchors, effective visibility, paint order, strata/levels, `setAllPoints`, and
  parent relationships.
- Debug wireframes, labels, hidden-state styling, placeholders, zero-size markers, unresolved
  diagnostics, selected bounds, all selected anchors, and offset labels.
- Three views over one layered canvas: visual content, debug overlay, and selection overlay.
- View filters for frames, buttons, text, textures, helpers, and hidden subtrees.
- Literal FontString and Button text using a neutral host font.
- Texture stand-ins tinted by declared color and alpha.
- Status-bar tracks and fills at their declared default fraction and color.

## 2. Imported but not visually rendered

The importer recognizes several paint/data elements so they do not become false geometry errors,
but Phase 3 does not yet retain or render all of them. The highest-value examples are button state
textures (`NormalTexture`, `PushedTexture`, `HighlightTexture`, `DisabledTexture`), font files and
heights, vertex colors, tiling/blend details, and template-provided backdrops.

These should be promoted into explicit model records before a renderer consumes them. Rendering
directly from XML would break saved-project independence and duplicate import policy in Desktop.

## 3. Newly retained in Phase 3

- Texture file paths: 19 paths in Native Hunts, including eight uses of
  `Interface\LFGFrame\UI-LFG-FRAME`.
- Texture atlas coordinates, normalized-coordinate and blend-mode declarations, color, and alpha.
- Seven literal FontString strings and 13 FontStrings explicitly identified as runtime/Lua text.
- Font-template references and horizontal/vertical justification.
- Two declared Button labels and their tab IDs.
- StatusBar min/max/default values, bar texture, bar color, and draw layer.
- Visual metadata in `.fforge.json`, with backward-compatible loading when it is absent.

## 4. Requires WoW client asset access

- Resolve `Interface\...` references case-insensitively against a user-supplied 3.3.5a asset root.
- Decode BLP (and load TGA where used by the addon) into a UI-independent image abstraction.
- Apply `TexCoords`, alpha/color modulation, and supported blend modes to decoded images.
- Cache decoded sources by canonical asset path and atlas region.
- Report missing assets in the inspector and diagnostics without replacing them with unrelated art.

FrameForge must not redistribute Blizzard client assets. Asset discovery should be opt-in and point
at files the user already has access to.

## 5. Requires stock Blizzard template emulation

`LFDParentFrame`, `CharacterFrameTabButtonTemplate`, inherited texture paint, and `GameFont*`
references are defined outside this XML. Resolve only the small, documented template subset needed
by supported projects. Store resolved template contributions separately from the source-authored
facts so the inspector can distinguish the two.

Avoid a general FrameXML/template runtime. A versioned library of specific 3.3.5a visual templates
is sufficient for the designer goal.

## 6. Requires runtime/Lua state approximation

Native Hunts uses Lua to choose which hidden state panel is shown, populate 13 text fields, update
the progress bar, size/position the external parent, and change icons or button state. FrameForge
must not execute arbitrary addon Lua.

Later design-time state should use explicit preview inputs: selected panel/state, sample text,
status value, and optional externally supplied parent bounds. Defaults must be labeled as design
samples and never serialized as facts imported from the XML.

## 7. Recommended implementation order

1. Add an asset-root service and deterministic, case-insensitive path resolution with missing-asset
   diagnostics.
2. Decode TGA first, because Native Hunts directly references its own TGA panels and dividers; add
   the minimal BLP formats needed by the referenced 3.3.5a client textures next.
3. Render decoded textures with `TexCoords`, color, and alpha through the existing
   `VisualContentLayer`.
4. Add the small stock-template catalog needed for `LFDParentFrame` and the tab-button template.
5. Add font metrics and styling for the referenced `GameFont*` subset, preserving the neutral-font
   fallback when unavailable.
6. Retain and render Button state textures and other currently skipped paint fields.
7. Add explicit, non-Lua preview-state controls for hidden panels, runtime text, and status values.

The first task for the next phase should be real texture resolution and TGA rendering. It yields the
largest honest improvement to Native Hunts Preview while fitting the current model/layer boundary;
Lua and broad template emulation can remain out of scope.
