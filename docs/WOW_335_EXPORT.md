# WoW 3.3.5a export

FrameForge exports DESIGN presentation as a deterministic package for World of Warcraft
3.3.5a build 12340. It does not generate Lua behavior, execute Lua, modify imported XML, or
package Blizzard-owned content.

## Pipeline and output

`Project` is first translated into a validated, UI-independent `WowExportModel`. The model is
then written as:

- `FrameForgeLayout.xml`: build-12340 FrameXML presentation.
- `frameforge-manifest.json`: versioned state and runtime-binding contract.
- `assets-manifest.json`: project artwork conversion/packaging contract.
- `export-report.txt`: deterministic diagnostics and importer self-check result.
- `assets/`: deduplicated project-owned source artwork required by the package.

Desktop exposes preferred **WoW 3.3.5a — BLP2/DXT5…**, the same encoder with source resolution
preserved, and **Legacy TGA…** under **Export**. Automation uses:

```text
FrameForge --export-wow335 <project.fforge.json> <directory> \
  [--texture-format blp2|tga] [--preserve-source-resolution]
```

The command-line default is BLP2/DXT5. The three-argument Core API overload retains legacy TGA
behavior for existing callers; new callers select `Wow335ExportOptions.Preferred` explicitly.

## v0.3.0 Native Hunts acceptance result

The preferred exporter was validated in-game on WoW 3.3.5a PTR with the Native Hunts integration.
That test used the default usage-optimized BLP2 output, not **Preserve source resolution**.

| Metric                | Previous TGA                  | v0.3.0 BLP2           |
| --------------------- | ----------------------------- | --------------------- |
| Native Hunts textures | 11                            | 11                    |
| Total size            | 160 MiB                       | 3.54 MiB              |
| Largest texture       | 4096×1024                     | 1024×1024             |
| WoW PTR result        | Crashed when displaying Hunts | Rendered successfully |

This is a concrete acceptance result for one integration rather than a claim that every possible
layout, asset combination, renderer, or graphics device has been validated in-game.

## Runtime identity and state

The DESIGN display name is the preferred runtime identity. Invalid identifier characters are
replaced with underscores, leading digits are protected, and collisions receive stable numeric
suffixes in authored order. The manifest records the internal-to-runtime mapping and object type.

An empty state membership means `allStates: true`. Each authored state has explicit `show` and
`hide` arrays, so later addon code can implement `ApplyState(name)` without FrameForge knowing
anything about a particular module's behavior or data sources. For Texture and FontString
regions, those arrays address the generated owning frame; `visibilityRuntimeId` records that
owner separately from the region's `runtimeId` used for text/value updates.

Runtime values are opt-in DESIGN metadata on FontString and StatusBar objects. **Runtime value**
must be enabled and a semantic **Binding Key** supplied; FrameForge never infers a binding from
blank text, object type, editor identity, or display name. Keys use dot-separated lowerCamel
identifiers such as `hunt.targetName`. The manifest emits `runtimeBinding`, `valueType`, and
`operation` only for explicitly bound objects (`string` / `SetText` for FontString,
`number` / `SetValue` for StatusBar). Static objects do not receive fake keys.

The runtime adapter owns presentation-only switching. For example, two static availability
labels or the standard/elite icon-and-label pairs remain distinct runtime IDs whose visibility
can be toggled from application data. They are not forced into a value-binding expression
language.

## Layout and draw order

The exporter resolves the existing v1 project geometry and writes logical WoW anchors, offsets,
width, and height; it never writes Avalonia canvas coordinates. Its generated root has explicit
size and placement and retains a validated authored external parent (or falls back to `UIParent`).
Anchors against the screen or an editor-only foundation are rebased to that root, while
relationships between authored objects remain native parent-relative anchors. Moving the root
therefore moves the full authored composition without changing the design's initial screen
position.

Texture and FontString regions are hosted in generated frames because WoW regions require an
owning frame and cross-region authored order needs deterministic frame levels. Native Frames and
StatusBars remain their own owners; parent and anchor references target the actual emitted owner
rather than assuming every object has a wrapper. State-specific owners start hidden until the
runtime applies a state.

The conceptual stock-framework group is not flattened or copied. When present, its resolved root
rectangle supplies the independent export root's initial geometry, and the manifest retains the
validated stock identity as provenance. The generated interface is not parented to that stock
window and does not depend on the window being visible. Stock artwork, font styles, and templates
remain legal logical client references.

## Text, StatusBars, and assets

Stock `GameFont*` styles remain logical `inherits` references. Size, colour, alignment, outline,
and shadow overrides use 3.3.5a FontString elements; the logical `Fonts\FRIZQT__.TTF` reference
does not copy or bundle the client font.

StatusBars retain min/max, logical bar texture, and tint. The DESIGN default value is recorded
only as `editorPreviewDefault` in the manifest and is deliberately omitted from FrameXML runtime
initialization, leaving `SetValue` to addon Lua.

Stock textures such as `Interface\TargetingFrame\UI-StatusBar` remain logical client references.
Project-owned PNG, TGA, and BLP inputs are copied unchanged into the export's `assets/` directory.
PNG inputs preferably produce BLP2 type-1 textures using DXT compression, eight-bit alpha,
DXT5/BC3 alpha encoding 7, and a complete mip chain. `BCnEncoder.Net` performs only BC3 block
compression. FrameForge writes and verifies the BLP2 container, every mip offset and length, file
bounds, and an independent base-mip decode. Legacy export still writes deterministic,
uncompressed TGA type 2: 32-bit BGRA, eight alpha bits, and top-left origin.

BLP preparation uses a conservative usage-based policy. For every shared PNG, FrameForge inspects
all consumers, their exported rectangles, and each rectangular crop. It chooses enough source
pixels for two texels per largest displayed UI pixel, preserves the source aspect ratio, never
upscales, and uses the largest requirement across consumers. The prepared image is resampled with
a deterministic, alpha-aware premultiplied area filter and placed at the top-left of the next
power-of-two canvas. **Preserve source resolution** disables only downsampling; power-of-two
padding and coordinate composition still apply. This override is appropriate when future runtime
use is not represented by the authored consumers.

Exported rectangular texture coordinates are composed with the prepared-to-padded width and
height ratios, including coordinates that already crop the source, so different objects continue
to share and crop one generated texture. Object positions, dimensions, anchors, hierarchy,
states, text, fonts, and colors are unchanged. The editor model currently supports the four-edge
rectangular FrameXML mapping; it does not contain an eight-value corner mapping to transform.

The coordinate composition maps corresponding source texel centres exactly. No opaque edge
extrusion is written because that would violate the transparent-black padding contract. Precise
filtering at the outermost edge remains dependent on the WoW 3.3.5a renderer and graphics device;
the exporter does not claim a client-side filtering result without an in-game test.

The exporter rejects non-positive dimensions, invalid usage calculations, and textures requiring
more than BLP2's sixteen mip slots. Decode failures, incomplete pixel data, malformed headers,
incorrect mip lengths or offsets, out-of-bounds payloads, trailing payload bytes, and independent
decode results outside conservative lossy-error limits block publication.

Each manifest entry retains the package-relative source, source SHA-256, original project
reference(s), logical `Interface\FrameForge\...` identity, and packaging target. Converted PNG
entries additionally record `generatedFile`, `generatedContentSha256`, `generatedFormat`, image
dimensions, and the exact `.blp` or `.tga` `requiredMpqDestination`. `sourceWidth` and
`sourceHeight` describe the PNG; `preparedWidth` and `preparedHeight` describe the usage-sized
artwork; `width` and `height` describe the power-of-two texture; and `paddingApplied`, `resized`,
`mipCount`, and independent-decode error metrics describe the output. Identical content shares one
packaged source file.
Same-name files with different content receive deterministic hash suffixes instead of overwriting
each other. Existing project-owned TGA and BLP inputs retain their prior source-packaging behavior.

Re-export replaces the four root contract files and its declared source and generated texture
files. It removes a stale file only when the previous version-2 FrameForge asset manifest proves
ownership of that exact path; unrelated files in the selected directory are preserved. A
conflicting unowned file blocks the export rather than being overwritten. PNG conversion occurs in
the staging directory, so conversion or validation failure leaves a previous successful package
unchanged.

## Content Manager integration

Generated XML keeps extensionless logical references such as
`Interface\FrameForge\Dungeon_Finder_UI\hunt_header`; the MPQ destination recorded by the asset
manifest supplies the matching `.blp` or `.tga` file. Existing explicit EPF `content` entries can
therefore package BLP output without a Content Manager change.

Content Manager's optional `frameForgeWowUi` ingestion currently consumes the version-2 source-PNG
contract and performs its own PNG-to-TGA conversion. It does not ingest FrameForge's generated BLP
payload. A BLP-based EPF must declare the generated `.blp` files as ordinary explicit content until
that separate component deliberately adds BLP ingestion. Do not include both generated TGA and BLP
destinations for the same logical texture in one test package.

## Validation

Errors stop all output for unsupported authored widgets, bad geometry or references, bad state
membership, invalid StatusBar ranges, unresolved or escaping project assets, invalid logical
stock paths, unsupported text flags, machine-local paths, missing or malformed runtime binding
keys, bindings on unsupported object kinds, bindings without the Runtime value flag, accidental
editor/source IDs, and one semantic key reused across incompatible value types. Reusing a key on
multiple compatible objects is valid (for example, one `hunt.huntmaster` value can feed tracker
and turn-in FontStrings). Name sanitization and stock-member overrides are warnings. After
writing, XML is parsed independently and re-imported through the existing FrameXML importer;
every exported runtime identity must survive.
