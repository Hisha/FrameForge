# FrameForge v2 native FrameXML export

Status: Milestone 3 export foundation  
Target: World of Warcraft 3.3.5a, build 12340  
Implementation: `FrameForge.Core.Export.V2FrameXmlExporter`

## Contract

The exporter consumes `UiDocument` directly. It does not create `FrameDef` records, call
`UiDocumentProjection`, rebase through global coordinates, or modify the document. A successful
export contains:

- `Design.xml`, the module-hosted visual composition;
- `frameforge.manifest.json`, the machine-readable identity and dependency contract; and
- zero or more project-owned files below `Artwork/`.

It does not emit Lua, an addon TOC, EPF data, or an MPQ. The AzerothCore module creates the host
frame and owns behavior. `mod-content-manager` owns packaging and distribution.

`V2FrameXmlExporter.Build(document, projectFilePath)` is a pure planning operation except for
reading referenced artwork. `Export(document, projectFilePath, destinationDirectory)` stages the
complete package and publishes only a valid plan. Errors return an empty XML/manifest plan and no
destination is created. Re-export can replace files declared by a prior v2 manifest; it refuses to
overwrite unrelated conflicting files.

## Native structure and geometry

The composition root is the only top-level generated frame. It has `parent` equal to the declared
module host. A fill-host root uses `setAllPoints="true"`; a fixed-size root emits `Size` and one
explicit host-relative `Anchor`.

Owned frame-like nodes are nested in their owner's `Frames` collection. `Texture` and `FontString`
nodes are nested in their owner's `Layers/Layer` collection. `Frame`, `Button`, and `StatusBar` use
their native element names. Region `DrawLayer` becomes `Layer/@level`; a nonzero sublevel becomes
`Layer/@textureSubLevel` in the native -8 through 7 range. Frame strata and frame levels remain
frame attributes.

Typed anchor targets serialize without coordinate conversion:

| Semantic target | FrameXML representation |
| --- | --- |
| `Parent` | omit `relativeTo`, invoking the native owner-relative rule |
| `CompositionRoot` | `relativeTo` is the exported root name |
| `LocalNode` | `relativeTo` is the target's exported runtime name |
| `ExternalGlobal` | `relativeTo` is the explicitly declared global |
| `Unresolved` | export error; no XML is emitted |

There is no `UIParent` fallback. It can appear only as an explicitly declared external global.
Width and height are emitted together. Multiple anchors retain authored order.

## Runtime identities

Explicit names must match `[A-Za-z_][A-Za-z0-9_]*` and remain unchanged. A collision is an error;
the exporter never silently adds a suffix. An unnamed node receives a stable name:

`FF2_<document GUID without hyphens>_<node GUID without hyphens>`

`FF2_` is reserved for generated names, so authored names may not use it. Display labels do not
participate in naming. The manifest maps every internal identity to its runtime global and owner.

## Artwork references

A texture or status-bar reference beginning with `Interface\` is a client-owned dependency. It is
written unchanged after slash normalization and is never copied. Other references are portable
paths relative to the saved v2 project. Project paths may not be absolute or escape the project
directory.

Milestone 3 accepts project-owned `.tga` and `.blp` files. They are copied as
`Artwork/<safe-stem>-<first-12-SHA256-characters>.<extension>` and referenced through
`Interface\FrameForge\Artwork\...`. The manifest records the source-relative path, package path,
logical client path, format, complete SHA-256, and consumers. PNG conversion is deliberately not
claimed: a PNG project reference is rejected until a deterministic build-12340 conversion pipeline
exists.

## Manifest schema version 1

The manifest has `schema: "frameforge-v2-framexml-export"` and `version: 1`. Its fields are:

- `status`: `valid` for every emitted manifest;
- `target`: product and build;
- `documentId`;
- `compositionRoot`: internal/runtime identities, module host, design dimensions, and fill/fixed
  sizing contract;
- `controls`: internal identity, runtime name, kind, owner identities, and explicit Lua global
  access expression;
- `externalReferences`: module host, authored external globals, Blizzard font-object/template
  references, and Blizzard artwork references with their consumers;
- `artwork`: project-owned source/output/logical paths, format, SHA-256, and consumers;
- `outputs`: SHA-256 for `Design.xml` and emitted artwork; and
- `diagnostics`: warnings retained on a successful plan.

The manifest does not list its own hash because that would be self-referential. A failed plan is
returned through the API diagnostics and writes no manifest.

## Fail-closed rules and current limitations

Semantic validation errors block export, as do persisted error diagnostics. Export-specific errors
include invalid/colliding identities, partial width/height pairs, disabled buttons (which require a
runtime call), status values without an authored range, invalid font-object names, missing or unsafe
artwork, malformed/unsupported artwork formats, and output-file ownership conflicts.

This foundation does not resolve template inheritance, verify that an arbitrary declared external
global exists at runtime, execute Lua, synthesize button behavior, convert PNG, generate gameplay
state logic, or validate against a redistributable copy of Blizzard's `UI.xsd`. Its structural
regressions are based on the installed build-12340 FrameXML cache and native conventions documented
in `FRAMEFORGE_ARCHITECTURE_RESEARCH.md`.

## Manual WoW 3.3.5a validation

1. Save the v2 project so project-relative artwork has an authoritative base directory.
2. Call `V2FrameXmlExporter.Export` into an empty staging directory and confirm it reports success.
3. Review `frameforge.manifest.json`: verify build `12340`, the module host identity, all control
   mappings, external dependencies, and output hashes.
4. Place `Design.xml` and `Artwork/` at the manifest-declared client-relative locations through the
   existing `mod-content-manager` EPF/MPQ pipeline. Do not create a standalone addon or TOC.
5. Ensure the module creates the manifest's host global before loading `Design.xml`.
6. Start a 3.3.5a build-12340 client, open the module-owned surface, and verify host show/hide,
   movement, scaling, frame ordering, region layering, anchors, text, and status-bar appearance.
7. Confirm `/fstack` (or equivalent local inspection) reports the manifest runtime names under the
   intended host. Any missing external font/art dependency or XML parser error is a failed validation,
   not something for module Lua to repair.

The checked-in `examples/frameforge-v2-golden.fforge.json` and
`examples/frameforge-v2-golden.Design.xml` show the exact small project and deterministic output used
by automated tests.
