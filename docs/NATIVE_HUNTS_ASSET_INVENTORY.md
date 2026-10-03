# Native Hunts texture asset inventory

This inventory was taken from the read-only source at
`content/client/Interface/FrameXML/NativeHuntsFrame.xml` in `mod-native-hunts`. The source contains
**21 `Texture` elements and 19 declared texture references**. Counts, paths, casing, dimensions,
coordinates, colors, alpha, anchors, and visibility below come from that file; physical-file facts
come from the adjacent `content/client/Interface` tree. Missing stock assets are not inferred to be
present.

All texture elements are descendants of `NativeHuntsFrame`, whose source declares
`hidden="true"`. Consequently none is runtime-visible with Preview's default Hidden-off policy.
Debug's default and the explicit Hidden toggle make them eligible for designer inspection without
changing the imported visibility state. Rows additionally marked hidden have their own
`hidden="true"` declaration.

## Element inventory

`Geometry` gives the resolved size and the declared placement relationship. `TL`, `TR`, `T`, and
`BR` mean TOPLEFT, TOPRIGHT, TOP, and BOTTOMRIGHT. All rows have no declared `subLevel`; paint order
therefore remains imported layer plus document order. BACKGROUND and ARTWORK are the enclosing
layer levels (the importer maps ARTWORK to its retained paint stratum).

| # | Imported element | Declared file | Classification / physical evidence | TexCoords | Tint / alpha | Layer | Geometry | Own visibility |
|---:|---|---|---|---|---|---|---|---|
| 1 | `Texture#1` | none | inherited/template-supplied tint only | full | `.12,.07,.04,1` / 1 | BACKGROUND | 326×314; TL +21,-156 | shown |
| 2 | `Texture#2` | `Interface\LFGFrame\UI-LFG-BACKGROUND-QUESTPAPER` | stock; extension omitted; no file in checkout | 0,.63671875 / 0,1 | none / 1 | BACKGROUND | 326×256; TL +21,-156 | shown |
| 3 | `Texture#3` | same quest-paper path | stock; extension omitted; no file in checkout | 0,.63671875 / .390625,.6171875 | none / 1 | BACKGROUND | 326×58; TL +21,-412 | shown |
| 4 | `Texture#4` | `Interface\LFGFrame\UI-LFG-FRAME` | stock atlas; extension omitted; no file in checkout | 0,.6953125 / 0,.3046875 | none / 1 | BACKGROUND | 356×156; TL | shown |
| 5 | `Texture#5` | same frame atlas | stock atlas | 0,.046875 / .3046875,.56640625 | none / 1 | BACKGROUND | 24×134; TL +0,-156 | shown |
| 6 | `Texture#6` | same frame atlas | stock atlas | .64453125,.6953125 / .3046875,.56640625 | none / 1 | BACKGROUND | 26×134; TL +330,-156 | shown |
| 7 | `Texture#7` | same frame atlas | stock atlas | 0,.046875 / .44921875,.56640625 | none / 1 | BACKGROUND | 24×60; TL +0,-290 | shown |
| 8 | `Texture#8` | same frame atlas | stock atlas | .64453125,.6953125 / .44921875,.56640625 | none / 1 | BACKGROUND | 26×60; TL +330,-290 | shown |
| 9 | `Texture#9` | same frame atlas | stock atlas | 0,.046875 / .56640625,.80078125 | none / 1 | BACKGROUND | 24×120; TL +0,-350 | shown |
| 10 | `Texture#10` | same frame atlas | stock atlas | .64453125,.6953125 / .56640625,.80078125 | none / 1 | BACKGROUND | 26×120; TL +330,-350 | shown |
| 11 | `Texture#11` | same frame atlas | stock atlas | 0,.6953125 / .80078125,.859375 | none / 1 | BACKGROUND | 356×30; TL +0,-470 | shown |
| 12 | `Texture#14` | `Interface\NativeHunts\hunt_divider.tga` | custom; file found; TGA | 0,.5078125 / 0,1 | none / 1 | BACKGROUND | 260×8; T +0,-17 | shown |
| 13 | identity background | `Interface\NativeHunts\hunt_panel_identity.tga` | custom; file found; TGA | 0,.546875 / 0,.6875 | none / 1 | BACKGROUND | 280×88; fills identity TL | shown |
| 14 | `…IdentityIcon` | none | runtime/template supplied; Lua chooses standard/elite icon | full | none / 1 | ARTWORK | 58×58; TL +13,-15 | shown |
| 15 | hunt-state background | `Interface\NativeHunts\hunt_panel_state.tga` | custom; file found; TGA | 0,.546875 / 0,.921875 | none / 1 | BACKGROUND | 280×118; fills state TL | shown |
| 16 | `…HuntStateDecoration` | `Interface\NativeHunts\hunt_trail_prints.tga` | custom; file found; TGA | 0,.75 / 0,.75 | none / .38 | BACKGROUND | 72×36; BR -12,+10 | **hidden** |
| 17 | `…HuntStateReadyIcon` | `Interface\NativeHunts\hunt_icon_turnin.tga` | custom; file found; TGA | full | none / 1 | ARTWORK | 44×44; TR -16,-34 | **hidden** |
| 18 | `Texture#16` | `Interface\TargetingFrame\UI-StatusBar` | stock; extension omitted; no file in checkout | full | `.08,.08,.08,.9` / .9 | BACKGROUND | `setAllPoints`; 184×12 resolved from status bar | shown (parent bar hidden) |
| 19 | idle background | `Interface\NativeHunts\hunt_panel_idle.tga` | custom; file found; TGA | 0,.546875 / 0,.828125 | none / 1 | BACKGROUND | 280×212; fills idle TL | shown |
| 20 | record background | `Interface\NativeHunts\hunt_panel_record.tga` | custom; file found; TGA | 0,.546875 / 0,.671875 | none / 1 | BACKGROUND | 280×86; fills record TL | shown |
| 21 | `…RecordSealIcon` | `Interface\NativeHunts\hunt_icon_seal.tga` | custom; file found; TGA | full | none / 1 | ARTWORK | 14×14; TR -37,-67 | shown |

The status bar also declares a `BarTexture` with the same
`Interface\TargetingFrame\UI-StatusBar` reference. It is paint metadata, not a 22nd `Texture`
element, so it is not included in the 21/19 element count.

## Physical custom assets

All ten files in `Interface/NativeHunts` are TGA type 2 (uncompressed true-color), 32-bit BGRA with
8 alpha bits, top-left origin, no color map, and no image ID. They identify as sRGBA; every file's
alpha channel spans fully transparent (0) through fully opaque (1), with intermediate coverage.
The eight files
directly referenced by `Texture` elements are:

| File | Stored dimensions | Referenced crop |
|---|---:|---|
| `hunt_divider.tga` | 512×8 | 260×8 source pixels |
| `hunt_panel_identity.tga` | 512×128 | 280×88 source pixels |
| `hunt_panel_state.tga` | 512×128 | 280×118 source pixels |
| `hunt_trail_prints.tga` | 128×64 | 96×48 source pixels, scaled to 72×36 |
| `hunt_icon_turnin.tga` | 64×64 | full image, scaled to 44×44 |
| `hunt_panel_idle.tga` | 512×256 | 280×212 source pixels |
| `hunt_panel_record.tga` | 512×128 | 280×86 source pixels |
| `hunt_icon_seal.tga` | 32×32 | full image, scaled to 14×14 |

`hunt_icon_standard.tga` and `hunt_icon_elite.tga` are also 64×64 files in the checkout. They are
not declared by any `Texture file` attribute; the Lua assigns them to the no-file identity icon at
runtime. FrameForge records that gap and does not execute the Lua.

## Stock and BLP status

There are three unique stock references across eleven declarations: the quest paper (twice), the
LFG frame atlas (eight times), and the targeting status bar (once as a Texture, once as BarTexture).
No corresponding files—and no `.blp` files at all—exist in the Native Hunts checkout. WoW 3.3.5a
normally supplies these extensionless stock paths from client data, where BLP is expected, but the
physical format cannot be verified from this checkout alone. Phase 4A therefore reports them as
missing until a user supplies an extracted asset root; a physically resolved BLP is reported as
unsupported rather than guessed or silently omitted.

## Phase 4A acceptance accounting

With the authoritative XML opened from its checkout and no additional client-asset root:

- Texture elements: 21
- Declared texture references: 19
- Resolved declarations: 8
- Unresolved declarations: 11 (all stock references)
- Rendered TGA declarations: 8
- Unique rendered TGA files: 8
- BLP-required/stock declarations: 11 (expected client assets; physical BLP not locally verified)
- No-file inherited/runtime Texture elements: 2
- Truly missing custom declarations: 0
- Decode failures: 0

The divider, identity panel, state panel, trail decoration, ready icon, idle panel, record panel, and
seal icon now use real artwork whenever their visibility policy makes them eligible. The outer
Blizzard frame, quest-paper background, status-bar texture, inherited tint surface, and runtime
identity icon remain explicit fallbacks.
