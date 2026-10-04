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
- Literal FontString and Button text, using the resolved local build-12340 font for the supported
  Native Hunts styles and a neutral host fallback otherwise.
- Real source-relative and configured-root TGA artwork plus the demonstrated BLP2 DXT1/DXT3/DXT5 stock
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
  stock files: BLP2 encoding 2, opaque/1-bit-alpha DXT1 and 8-bit-alpha DXT5. Phase 5B added only
  the demonstrated 8-bit-alpha-header/explicit-4-bit-alpha DXT3 tuple used by the character tabs. Decoded pixels are
  straight-alpha BGRA in top-left row order, matching the renderer's existing image contract.
- Malformed headers and truncated mip data are decode failures. BLP1, paletted BLP2, JPEG, raw
  ARGB and unrecognized encoding/alpha combinations receive explicit unsupported-format
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

## 6. Phase 5A local-client asset provider

- A separate `IWoWClientAssetProvider` validates a selected Windows client from any host OS by
  reading the `Wow.exe` PE FileVersion resource as bytes. Only 3.3.5a build 12340 is accepted.
- Locale directories are discovered rather than assuming `enUS`; no locale or multiple plausible
  locales are reported instead of guessed.
- The provider opens the user's MPQs read-only and materializes only unresolved `Interface` paths
  requested by the current project. It does not extract the Interface tree or depend on `smpq`.
- Resolver precedence is source-relative project artwork, explicit manual asset roots in configured
  order, then the FrameForge-managed stock cache. Custom/project artwork therefore cannot be
  shadowed by cached stock art.
- Cached files and provenance are machine-local. Portable projects contain neither the selected
  client path nor cache paths. See `WOW_CLIENT_ASSETS.md` for the full search order and safeguards.
- Native Hunts acceptance resolves three unique stock BLPs from the client and reuses those files
  for all eleven stock declarations; together with eight project TGA declarations, all 19 direct
  texture declarations render without a manual extracted-assets root.

## 7. Phase 5B focused stock template and font fidelity

`LFDParentFrame`, `CharacterFrameTabButtonTemplate`, inherited texture paint, and `GameFont*`
references are defined outside project XML. The build-12340 resolver materializes eleven focused
resources from the user's client and indexes only the definitions Native Hunts demonstrates.
Resolved contributions stay separate from source-authored facts so the inspector can distinguish
declared values, effective values, and source definition provenance.

- The external parent records the stock XML's 355x440 size and the addon's explicit 355x500 Hunts
  state, while omitting the irrelevant Lua-driven Dungeon Finder subtree.
- Character tabs use the real normal three-slice atlas, authoritative texCoords, 32px height,
  text offset, `GameFontNormalSmall`, and the demonstrated zero-padding `PanelTemplates_TabResize`
  formula. Preview deliberately uses normal/unselected state.
- Six Native Hunts styles resolve transitively through `Fonts.xml`/`FontStyles.xml` to the local
  `FRIZQT__.TTF`, including nominal size, color, shadow, outline, and justification.
- Effective auto-size/layout is computed on a transient clone; imported and serialized values are
  not flattened or rewritten. Missing files, unresolved/circular/unsupported inheritance, texture
  gaps, font registration failure, unsupported font flags, and Lua boundaries are diagnostic.

This remains a focused compatibility layer, not a general FrameXML/template runtime. Exact evidence
and omissions are recorded in `NATIVE_HUNTS_TEMPLATE_INVENTORY.md`.

## 8. Requires runtime/Lua state approximation

Native Hunts uses Lua to choose which hidden state panel is shown, populate 13 text fields, update
the progress bar, size/position the external parent, and change icons or button state. FrameForge
must not execute arbitrary addon Lua.

Later design-time state should use explicit preview inputs: selected panel/state, sample text,
status value, and optional externally supplied parent bounds. Defaults must be labeled as design
samples and never serialized as facts imported from the XML.

## 9. Recommended implementation order

1. Add explicit, non-Lua preview-state controls for hidden panels, runtime text, icons, tab state,
   and status values (Phase 5C).
2. Retain/render additional button states only when those preview controls need them.
3. Extend stock compatibility or BLP decoding only when a real project demonstrates the need.

Lua execution, broad template emulation, unsupported BLP variants, and speculative artwork remain
out of scope.
