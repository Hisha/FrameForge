# WoW 3.3.5a export

FrameForge has two deliberately separate WoW 3.3.5a build-12340 exports. **Static Layout
Package** writes presentation plus an adapter contract. **Functional Interface Package** composes
the saved design into a verified copy of associated FrameXML while retaining that document's
frame identities, hierarchy, template inheritance, scripts, Lua file relationship, and load-order
role. FrameForge never executes or generates Lua, changes the selected source file, or packages
Blizzard-owned content.

## Pipeline and output

`Project` is first translated into a validated, UI-independent `WowExportModel`. The model is
then written as:

- `FrameForgeLayout.xml`: build-12340 FrameXML presentation.
- `frameforge-manifest.json`: versioned state and runtime-binding contract.
- `assets-manifest.json`: project artwork conversion/packaging contract.
- `export-report.txt`: deterministic diagnostics and importer self-check result.
- `assets/`: deduplicated project-owned source artwork required by the package.

Desktop exposes this through **Export → Static WoW 3.3.5a Layout Package…**. The same path is available for
automation as `FrameForge --export-wow335 <project.fforge.json> <directory>`.

## Functional source composition

Use **Export → Associate Functional FrameXML…** once on an existing DESIGN project. FrameForge
stores the source's portable relative path, SHA-256, selected host frame, state probes, and value
sources in the project; it does not import a second visual design or discard the current one. The
association is rejected when no unambiguous concrete `setAllPoints` host exists or state probes
cannot be inferred safely.

For a runtime FontString or StatusBar, enable **Runtime value**, keep its semantic binding key, and
choose the corresponding existing XML control in **XML source**. Functional export is blocked if
any required value lacks a source, has the wrong widget type, or refers to a missing control. It is
also blocked if the source hash changed, a generated identity collides with an existing one, state
coverage is incomplete, or the design cannot be rebased against its stock-framework foundation.

The output keeps the associated XML file name and inserts one generated design child under the
selected functional host. Root-level DESIGN geometry is rebased from the saved stock foundation,
so an existing screen-space design is not emitted as an unrelated `UIParent` layout. A small inline
bridge mirrors explicitly mapped values and applies authored state visibility from the saved state
probes. The original source bytes before the final `</Ui>` remain unchanged. The package also
contains the normal manifests, report, and project-owned artwork. Keep the addon's existing Lua
files and TOC/XML load-order entries: they remain authoritative and are neither copied nor replaced.

The functional destination must be empty. This prevents export from overwriting either the source
or an unrelated addon package.

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
