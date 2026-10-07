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

Desktop exposes this through **Export → WoW 3.3.5a Layout…**. The same path is available for
automation as `FrameForge --export-wow335 <project.fforge.json> <directory>`.

## Runtime identity and state

The DESIGN display name is the preferred runtime identity. Invalid identifier characters are
replaced with underscores, leading digits are protected, and collisions receive stable numeric
suffixes in authored order. The manifest records the internal-to-runtime mapping and object type.

An empty state membership means `allStates: true`. Each authored state has explicit `show` and
`hide` arrays, so later addon code can implement `ApplyState(name)` without FrameForge knowing
anything about Native Hunts behavior or data sources.

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

The exporter writes logical WoW anchors, offsets, width, and height; it never writes Avalonia
canvas coordinates. Regions are hosted in generated frames so authored order can be expressed
with deterministic frame levels as well as WoW draw layers. Parent and anchor references are
translated to the generated runtime identities. State-specific objects start hidden until the
runtime applies a state.

The conceptual Dungeon Finder group is not flattened. The export root is attached to the stock
`LFDParentFrame`, and the manifest records the validated build-12340 stock identity. Stock
members stay client-owned and behind custom DESIGN content.

## Text, StatusBars, and assets

Stock `GameFont*` styles remain logical `inherits` references. Size, colour, alignment, outline,
and shadow overrides use 3.3.5a FontString elements; the logical `Fonts\FRIZQT__.TTF` reference
does not copy or bundle the client font.

StatusBars retain min/max, logical bar texture, and tint. The DESIGN default value is recorded
only as `editorPreviewDefault` in the manifest and is deliberately omitted from FrameXML runtime
initialization, leaving `SetValue` to addon Lua.

Stock textures such as `Interface\TargetingFrame\UI-StatusBar` remain logical client references.
Project PNGs are not copied into the export. Their manifest entries map the project-relative
source to a logical `Interface\FrameForge\...` identity and a future `.tga` packaging target.
Conversion and placement in mod-content-manager/EPF are intentionally deferred.

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
