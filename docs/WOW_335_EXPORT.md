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

Desktop exposes this through **Export → WoW 3.3.5a Layout…**. The same path is available for
automation as `FrameForge --export-wow335 <project.fforge.json> <directory>`.

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
width, and height; it never writes Avalonia canvas coordinates. Its generated root is an
independent `UIParent` child with explicit size and placement. Anchors against the screen or an
editor-only/stock foundation are rebased to that root, while relationships between authored
objects remain native parent-relative anchors. Moving the root therefore moves the full authored
composition without changing the design's initial screen position.

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
Project-owned PNG, TGA, and BLP inputs are copied into the export's `assets/` directory. Each
manifest entry names that package-relative source, its SHA-256, original project reference(s),
logical `Interface\FrameForge\...` identity, and downstream packaging target. Identical content
shares one packaged source file. Same-name files with different content receive deterministic
hash suffixes instead of overwriting each other. PNG conversion and placement in a client patch
remain downstream packaging responsibilities.

Re-export replaces the four root contract files and its declared asset files. It removes a stale
asset only when the previous version-2 FrameForge asset manifest proves ownership of that exact
path; unrelated files in the selected directory are preserved. A conflicting unowned file blocks
the export rather than being overwritten.

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
