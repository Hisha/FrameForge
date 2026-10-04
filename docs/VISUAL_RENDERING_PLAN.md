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
- Real source-relative and configured-root TGA artwork plus the demonstrated BLP2 DXT1/DXT5 stock
  subset, including texCoords, RGB modulation, alpha, and imported paint order. Unresolved textures
  retain the Phase 3 stand-in.
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

## 4. Phase 4A asset resolution

- Search order is the opened XML's source content hierarchy first, then application-level asset roots
  in the visible user-configured order. A root can be either the directory containing `Interface`
  or `Interface` itself.
- WoW backslashes and platform separators are normalized without lowercasing physical paths. Exact
  case wins; a unique case-insensitive match is accepted; multiple matches are diagnosed as
  ambiguous. Rooted paths and traversal are rejected. Extensionless references try the literal
  name, `.tga`, `.blp`, and `.png`, and multiple matches are ambiguous.
- Asset roots are machine-local application settings, not project data. This keeps `.fforge.json`
  portable and prevents developer-machine paths leaking into saved projects. The toolbar exposes
  add/browse, remove, current roots, and Refresh Assets.
- Phase 4A decodes TGA type 2 uncompressed true-color images at 24 or 32 bits, including both
  vertical origins and both horizontal origins. The Native Hunts files use 32-bit BGRA, 8-bit alpha,
  top-left origin. Other TGA types fail explicitly.
- Successfully decoded source images are cached by canonical path, length, and modification time.
  Tint variants are separate bitmap views; the straight-alpha source pixels are not changed.
  Project/source changes, root changes, and Refresh Assets invalidate resolution and decode caches.
- The inspector reports declared reference, status, physical path, source/root, detected format,
  dimensions, and fallback reason. Absolute resolved paths remain local UI state and are never
  serialized.

FrameForge must not redistribute Blizzard client assets. Asset discovery is opt-in and points at
files the user already has access to.

## 5. Phase 4B evidence-driven BLP subset

- A decoder registry keeps physical-file resolution independent from byte decoding and makes TGA
  and BLP implementations independently extensible. The renderer still owns texCoords, tint,
  alpha, geometry, and paint order.
- Phase 4B decodes the base mip of only the BLP forms demonstrated by the three supplied 3.3.5a
  stock files: BLP2 encoding 2, opaque/1-bit-alpha DXT1 and 8-bit-alpha DXT5. Decoded pixels are
  straight-alpha BGRA in top-left row order, matching the renderer's existing image contract.
- Malformed headers and truncated mip data are decode failures. BLP1, paletted BLP2, JPEG, raw
  ARGB, DXT3, and unrecognized encoding/alpha combinations receive explicit unsupported-format
  diagnostics. PNG remains identified but undecoded.
- Native Hunts acceptance with the optional extracted root resolves all 19 direct file references:
  eight custom TGA declarations and eleven stock BLP declarations backed by three unique BLP files.
  The eight LFG-frame widgets reuse one cached atlas decode and crop distinct regions with their
  imported texCoords.
- The StatusBar `BarTexture` metadata now resolves and is used for a nonzero design-time fill. Its
  cached source stays immutable while widget color and alpha are applied during rendering. Native
  Hunts' default value is zero, so that fill is absent in the default preview.

The extracted client files remain user-owned, opt-in inputs and are not redistributed. See
`NATIVE_HUNTS_ASSET_INVENTORY.md` for exact headers, hashes, mip tables, and acceptance counts.

## 6. Requires stock Blizzard template emulation

`LFDParentFrame`, `CharacterFrameTabButtonTemplate`, inherited texture paint, and `GameFont*`
references are defined outside this XML. Resolve only the small, documented template subset needed
by supported projects. Store resolved template contributions separately from the source-authored
facts so the inspector can distinguish the two.

Avoid a general FrameXML/template runtime. A versioned library of specific 3.3.5a visual templates
is sufficient for the designer goal.

## 7. Requires runtime/Lua state approximation

Native Hunts uses Lua to choose which hidden state panel is shown, populate 13 text fields, update
the progress bar, size/position the external parent, and change icons or button state. FrameForge
must not execute arbitrary addon Lua.

Later design-time state should use explicit preview inputs: selected panel/state, sample text,
status value, and optional externally supplied parent bounds. Defaults must be labeled as design
samples and never serialized as facts imported from the XML.

## 8. Recommended implementation order

1. Add the small stock-template catalog needed for `LFDParentFrame` and the tab-button template.
2. Add font metrics and styling for the referenced `GameFont*` subset, preserving the neutral-font
   fallback when unavailable.
3. Retain and render Button state textures and other currently skipped paint fields.
4. Add explicit, non-Lua preview-state controls for hidden panels, runtime text, and status values.
5. Extend BLP decoding only when new, verified project assets demonstrate another required subtype.

Phase 4B adds verified stock artwork without broadening into a general BLP promise. Lua, broad
template emulation, unsupported BLP variants, and speculative artwork remain out of scope.
