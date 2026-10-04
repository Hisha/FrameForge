# Native Hunts Stock Template and Font Inventory

This inventory records the focused Phase 5B investigation against a user-owned World of Warcraft
3.3.5a build 12340 enUS client. It contains metadata and compatibility conclusions only. Blizzard
XML, Lua, fonts, and artwork are never committed or distributed by FrameForge.

## Direct project dependencies

`NativeHuntsFrame.xml` directly references one external Blizzard frame, one button template, and
six font style names.

| Kind | Direct name | Static Phase 5B handling |
| --- | --- | --- |
| external frame | `LFDParentFrame` | scoped parent context; stock size recorded, addon Hunts-state size retained |
| button template | `CharacterFrameTabButtonTemplate` | normal/unselected three-slice presentation and effective sizing |
| font | `GameFontNormal` | gold, 12px, shadowed Friz |
| font | `GameFontHighlight` | white, 12px, shadowed Friz |
| font | `GameFontNormalSmall` | gold, 10px, shadowed Friz; tab normal font |
| font | `GameFontNormalLarge` | gold, 16px, shadowed Friz |
| font | `GameFontHighlightSmall` | white, 10px, shadowed Friz; tab highlight/disabled font |
| font | `GameFontHighlightLarge` | white, 16px, shadowed Friz |

The template itself has no inherited parent template. Its transitive dependencies are the font
styles, three tab atlases, and the `PanelTemplates_TabResize` function inspected in
`UIPanelTemplates.lua`. Font inheritance reaches `SystemFont_Shadow_Small`,
`SystemFont_Shadow_Med1`, and `SystemFont_Shadow_Large`. No multiple template inheritance is needed.

## Effective client resources

Archive precedence is the provider's normal low-to-high build-12340 order. The table names the
effective winning archive observed in the acceptance client; all paths are archive-relative.

| Resource | Effective archive | SHA-256 | Purpose |
| --- | --- | --- | --- |
| `Interface/FrameXML/LFDFrame.xml` | `Data/enUS/patch-enUS-3.MPQ` | `cbb539a5…e209ee4` | `LFDParentFrame` static declaration |
| `Interface/FrameXML/LFDFrame.lua` | `Data/enUS/patch-enUS-3.MPQ` | `09f963c2…3b133c` | runtime boundary evidence only |
| `Interface/FrameXML/CharacterFrameTemplates.xml` | `Data/enUS/patch-enUS.MPQ` | `0237e89c…e70e916` | character tab template |
| `Interface/FrameXML/UIPanelTemplates.xml` | `Data/enUS/patch-enUS-3.MPQ` | `08aabf32…751b00` | associated panel declarations |
| `Interface/FrameXML/UIPanelTemplates.lua` | `Data/enUS/patch-enUS-3.MPQ` | `d9bf4446…5be2f0` | tab resize formula evidence only |
| `Interface/FrameXML/Fonts.xml` | `Data/enUS/patch-enUS-3.MPQ` | `5dedf8fd…87bfc3` | system font roots and metrics |
| `Interface/FrameXML/FontStyles.xml` | `Data/enUS/patch-enUS-3.MPQ` | `b8281665…5aec7` | `GameFont*` style chain |
| `Fonts/FRIZQT__.TTF` | `Data/enUS/locale-enUS.MPQ` | `8f798feb…dc41c` | local Friz Quadrata glyphs/metrics |
| `Interface/PaperDollInfoFrame/UI-Character-InactiveTab.blp` | `Data/enUS/locale-enUS.MPQ` | `b59a5d10…4c9a69` | normal tab slices |
| `Interface/PaperDollInfoFrame/UI-Character-ActiveTab.blp` | `Data/enUS/locale-enUS.MPQ` | `04c686ec…5b396` | disabled/selected slices, indexed but not the default state |
| `Interface/PaperDollInfoFrame/UI-Character-Tab-Highlight.blp` | `Data/enUS/locale-enUS.MPQ` | `cb5b5f06…fefbe8` | highlight metadata, indexed but not the default state |

These effective definitions and the font/art are locale-archive resources in this enUS client.
FrameForge discovers the configured locale and does not hardcode `enUS` in production.

## Character tab evidence and supported subset

The stock template declares a 10x32 button before runtime resizing. Its normal and disabled artwork
are each three slices from 128x32 atlases: 20px left, 88px middle, and 20px right, using horizontal
texCoord ranges `0..0.15625`, `0.15625..0.84375`, and `0.84375..1`. The text child is centered with
a WoW Y offset of +2 and uses `GameFontNormalSmall`. Highlight and disabled text use
`GameFontHighlightSmall`.

Native Hunts calls `PanelTemplates_TabResize(tab, 0)`. In Lua, zero is truthy, so the demonstrated
effective width is measured local-font text width plus the two 20px sides, with no extra padding.
FrameForge applies that formula on an effective project clone. An explicit project width/height
wins. The normal/unselected state is deterministic for Phase 5B; click, hover, disabled, and
selected state transitions are deferred.

The normal and active atlases are BLP2 encoding 2 with alphaDepth 8 and alphaEncoding 1: DXT3 with
explicit 4-bit alpha. This real dependency is why Phase 5B adds narrowly scoped DXT3 decoding. The
highlight atlas uses the already-supported opaque DXT1 form.

## Font evidence and rendering

All required styles resolve to `Fonts/FRIZQT__.TTF` (internal family `Friz Quadrata TT`). The system
roots declare a black shadow at `(1,-1)`. Normal styles are gold `(1, 0.82, 0)` and highlight styles
are white. Nominal sizes are 10, 12, or 16 as listed above. Style-level justification overrides are
combined with the project `FontString` declaration; project justification wins when present.

FrameForge registers the locally materialized font with Avalonia and uses Skia for deterministic
text measurement. It applies color/alpha, horizontal and vertical justification, shadow, and the
supported `NORMAL`/`THICK` outline flags. Text is clipped to the resolved widget. If the local font
cannot be loaded, authoritative nominal metadata remains available and an explicit diagnostic says
that the host fallback is in use.

## LFD parent scope

Stock XML declares `LFDParentFrame` at 355x440. Native Hunts Lua explicitly changes it to 355x500
for the Hunts state, hides the stock queue frame, positions the two tabs, and supplies live state.
The existing import stand-in therefore remains 355x500 for the addon's effective static context.
The resolver records both values and their origin rather than silently replacing the imported
model.

FrameForge does not reproduce the complete Dungeon Finder subtree. Its queue contents, live role/
queue state, stock navigation, and Lua-managed visibility are irrelevant clutter for Native Hunts
design and remain omitted. Native Hunts' own retained frame/background declarations provide the
visible parent context.

## Provenance, diagnostics, and runtime boundary

The in-memory index is rebuilt after materialization and cache clearing. Effective font/template
facts retain definition and source-file provenance for the Inspector. It emits explicit diagnostics
for missing/invalid definition files, unresolved templates or fonts, missing inherited textures,
circular or multiple inheritance, unavailable local fonts, unsupported font flags, and the two
inspected-but-not-executed Lua boundaries. Status is `FullyResolved`, `PartiallyResolved`, or
`Unresolved` where applicable.

Phase 5B does not execute Lua or invent runtime values. Deferred to Phase 5C are the active hunt
panel, current target/spec, progress, server strings, standard/elite identity icon, tab interaction,
selected/disabled/hover state, and live Dungeon Finder state. Runtime-only FontStrings continue to
render as empty ruled placeholders.

## Acceptance evidence

The opt-in real-client smoke run materialized 11 definition/font/tab resources plus three unique
direct stock textures, resolved all 19 direct Native Hunts texture declarations (8 TGA and 11 BLP),
and decoded the 11 unique physical project/stock texture files once each. Enabling Phase 5B stock
template/font rendering changed 10,487 preview pixels from the Phase 5A baseline. The change is
attributable to the real tab artwork/effective geometry and authoritative font presentation, not to
runtime-state synthesis.
