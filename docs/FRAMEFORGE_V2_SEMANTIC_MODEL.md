# FrameForge Project Schema v2 semantic model

Status: Milestone 1 semantic foundation  
Target: World of Warcraft 3.3.5a, build 12340  
Namespace: `FrameForge.Core.Semantics.V2`

## Scope

Schema v2 is the authoring model for new module-owned interfaces. It gives FrameForge a
WoW-compatible semantic graph without connecting that graph to the Avalonia editor or generating
FrameXML yet. The model exists beside schema v1; no v1 project is migrated or interpreted as v2.

This milestone does not implement XML import, XML round-trip editing, template resolution,
FrameXML export, artwork packaging, module runtime integration, EPF/MPQ generation, or addon
support. Existing FrameForge code continues to use the v1 types until the editor is connected in a
later milestone.

## Document structure

`UiDocument` is the complete v2 project. It contains:

- `Version`, which must be `2`.
- A stable `DocumentId` stored as a canonical GUID string.
- A `WowTargetProfile`. Only product `wow-3.3.5a`, build `12340`, is accepted.
- `CompositionRoots`. A collection is used so an invalid in-memory graph can receive an actionable
  missing- or multiple-root diagnostic; a valid document contains exactly one entry.
- An ordered `Nodes` collection.
- Ordered, declared `ExternalReferences`.
- Optional document and node editor metadata.
- Persisted diagnostics, for information that must survive a project round trip.

`UiDocumentFactory.Create` creates a valid blank document with one fill-host root and a declared
module host. It does not create a v1 compatibility object.

## Composition root

The composition root is the one boundary between a generated design and its consuming
AzerothCore module. It has an immutable internal identity, a runtime frame name, an explicitly
named module-owned host, positive design dimensions, and ordered child identities.

Two sizing contracts are represented:

- `FillHost`: the future generated root fills the module host. It must not also carry explicit
  dimensions or an anchor.
- `Explicit`: width, height, and one root-to-host anchor are all required.

The host is an explicit external reference. The model does not create, inspect, or integrate that
host at runtime in this milestone.

## Nodes and structural ownership

The initial node kinds are `Frame`, `Texture`, `FontString`, `Button`, and `StatusBar`.

Every node has:

- An immutable `SemanticId`. Renaming a label or runtime global cannot alter it.
- An optional runtime/global name and a separate display label.
- An `OwnerReference` naming either the composition root or a local node by identity.
- Ordered children and ordered anchors.
- An `AuthoredProperties` block appropriate to its kind.
- Optional editor-only metadata.

Ownership is deliberately reciprocal. A child names its owner, and the owner lists that child's
identity in order. Validation requires both sides to agree. This makes hierarchy and child order
explicit and prevents a nullable parent name from acquiring multiple meanings.

`Frame`, `Button`, and `StatusBar` are frames and may own children. `Texture` and `FontString` are
regions; they may be owned by frames but cannot own children. Ownership cycles, dangling owners,
dangling ordered children, duplicate child entries, and regions used as owners are errors.

## Typed anchors

Each `UiAnchor` preserves anchor point, relative point, X/Y offsets, target, and list order. Its
target is one of:

- `Parent`: the node's explicit structural owner.
- `CompositionRoot`: the document's one root.
- `LocalNode`: a node identified by immutable identity, suitable for sibling relationships.
- `ExternalGlobal`: an explicitly named and declared runtime global.
- `Unresolved`: original target text plus diagnostic context that must not be mistaken for a valid
  reference.

There is no null target and no `UIParent` fallback. `UIParent` is usable only as the literal name of
an `ExternalGlobal` target with a matching external-reference declaration. Missing local targets,
self-targets, undeclared/invalid external globals, and unresolved targets are validation errors.

## Authored and effective properties

`AuthoredProperties` stores only facts directly written in the project. It has distinct property
blocks rather than one untyped bag:

- Frame: width, height, frame strata, frame level, and visibility.
- Region: draw layer, sublevel, and tint.
- Texture: texture reference.
- Font string: font reference and literal text.
- Button: enabled state.
- Status bar: range, value, and texture reference.

Frame strata and frame level are valid only for frames. Region draw layer and sublevel are valid
only for regions. Texture, font-string, button, and status-bar blocks are valid only for their
matching node kinds. Dimensions, color components, frame levels, and status-bar ranges are checked
for contradictions.

`EffectiveNodeProperties` is a separate, non-persisted result type with property provenance. A
future template resolver can produce it, but inherited/effective values can never overwrite the
authored project facts or be flattened into `UiDocument`.

## Validation

`UiDocumentValidator.Validate` is a pure semantic graph validator. It returns diagnostics and never
changes the document, supplies a parent, or substitutes an anchor. Every diagnostic has a stable
code, severity, message, property path, and, where applicable, the affected internal identity.

It detects:

- Missing or multiple roots and unsupported schema/build values.
- Invalid or duplicate internal identities and runtime/global names.
- Missing, inconsistent, invalid, or cyclic ownership.
- Regions acting as frame containers.
- Dangling local anchors, self-anchors, ambiguous root anchors, unresolved anchors, and invalid or
  undeclared external globals.
- Invalid root sizing definitions and undeclared module hosts.
- Frame/region property mismatches and kind-specific property mismatches.
- Non-positive/non-finite dimensions, invalid tint components, negative frame levels, and
  contradictory status-bar ranges or values.

An intentional, declared external anchor is not an error. Future export policy may impose stronger
host-bound restrictions without changing the semantic distinction.

## Serialization

`UiDocumentCodec` reads and writes deterministic, indented JSON with a trailing newline. The format
marker is `frameforge-ui-document` and the version is `2`. Internal identities are canonical GUID
strings. Array order carries document, ownership, and anchor order. External references, editor
metadata, and persisted diagnostics round trip.

The codec rejects invalid JSON, missing required members, unknown members, integer enum values,
wrong format markers, and unsupported schema versions with a path-bearing error. In particular, a
v1 `frameforge-project` is rejected with an explicit statement that it is not converted
automatically. Semantic validity remains the validator's responsibility, which allows an unresolved
graph to be loaded and diagnosed without silently repairing it.

## Illustrative document

The example is shortened only by omitting optional null property blocks. Its relationships are
complete: the root declares its host, the panel is owned by the root, and its anchor explicitly
targets that owner.

```json
{
  "format": "frameforge-ui-document",
  "version": 2,
  "target": { "product": "wow-3.3.5a", "build": 12340 },
  "documentId": "10000000-0000-0000-0000-000000000001",
  "compositionRoots": [
    {
      "id": "10000000-0000-0000-0000-000000000002",
      "runtimeName": "QuestDesignRoot",
      "externalHostName": "QuestModuleHost",
      "designWidth": 800,
      "designHeight": 600,
      "sizing": { "kind": "fillHost" },
      "children": ["10000000-0000-0000-0000-000000000003"]
    }
  ],
  "nodes": [
    {
      "id": "10000000-0000-0000-0000-000000000003",
      "kind": "frame",
      "runtimeName": "QuestPanel",
      "displayLabel": "Quest panel",
      "owner": {
        "kind": "compositionRoot",
        "id": "10000000-0000-0000-0000-000000000002"
      },
      "children": [],
      "anchors": [
        {
          "point": "center",
          "target": { "kind": "parent" },
          "relativePoint": "center",
          "offsetX": 0,
          "offsetY": 0
        }
      ],
      "authoredProperties": {
        "frame": {
          "width": 420,
          "height": 300,
          "strata": "medium",
          "level": 1,
          "visible": true
        }
      }
    }
  ],
  "externalReferences": [
    {
      "globalName": "QuestModuleHost",
      "description": "Module-owned composition host"
    }
  ],
  "diagnostics": []
}
```

## Relationship to v1 and future module integration

Schema v1 (`Project`, `FrameDef`, and `ProjectCodec`) is untouched and remains the implementation
used by the current editor, loaders, and exporters. Schema v2 has a separate namespace, format ID,
factory, validator, and codec. There is no shared persistence authority and no migration layer.

The root contract, immutable IDs, typed external references, and explicit ownership establish what
a future generic AzerothCore module integration can rely on: one host boundary, deterministic
control identities, locally provable hierarchy, and no accidental global positioning. The module
will continue to own gameplay behavior, Lua controllers, server communication, and runtime state;
FrameForge will own visual structure and eventually deterministic FrameXML. Content Manager will
remain responsible for EPF/MPQ packaging and distribution.
