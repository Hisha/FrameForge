# FrameForge Project Format

`.fforge.json`, format version **1**.

A project stores World of Warcraft model data plus a deliberately separate optional `editor`
metadata block. There are no canvas positions, zoom levels, selection state or pixel measurements.
Groups and locks have no WoW runtime meaning and never alter the imported hierarchy or FrameXML.

## Compatibility rule

New keys are **additive and optional**. A reader that does not know a key ignores it, and a writer
omits every key that holds its default. Two consequences are enforced by tests:

- `examples/native-hunts.fforge.json` stays byte-identical across releases that add keys.
- A file written by an older FrameForge still loads, and re-saving it does not invent keys.

## Top level

```json
{
  "format": "frameforge-project",
  "version": 1,
  "name": "Native Hunts",
  "screen": { "width": 1024, "height": 768 },
  "frames": [ ... ]
}
```

| Key | Type | Notes |
| --- | --- | --- |
| `format` | string | Always `frameforge-project`. |
| `version` | number | Currently `1`. |
| `name` | string | Project name. |
| `screen` | object | Pixel size the layout was authored against. |
| `frames` | array | Flat list; `parent` expresses the hierarchy. |
| `source` | object? | Present only when the project was imported from an external file. |
| `editor` | object? | Optional logical groups and locks; FrameForge-only metadata. |

## Frames

Frames are a flat array. `parent` holds the parent's **name**, not an index, because that is how
both WoW and a human being refer to a frame.

```json
{
  "name": "Content",
  "parent": "MainWindow",
  "width": 296,
  "height": 406,
  "point": "TOP",
  "relativeTo": "MainWindow",
  "relativePoint": "TOP",
  "offsetX": 12,
  "offsetY": -44,
  "visible": true
}
```

| Key | Type | Notes |
| --- | --- | --- |
| `name` | string | Unique within the project. |
| `parent` | string? | `null` means the frame is anchored to the screen. |
| `width`, `height` | number | Model units, or a fraction of the parent when `sizeReference` is `PARENT`. |
| `point` | string | One of the nine anchor points. Required. |
| `relativeTo` | string? | Anchor target name. `null` means the parent, or the screen at top level. |
| `relativePoint` | string | Required. |
| `offsetX`, `offsetY` | number | Model units, `+Y` up. |
| `visible` | bool | Always written. |
| `sizeReference` | string? | `SCREEN` or `PARENT`. Omitted when `SCREEN`. |
| `stratum` | string? | Layer stratum, e.g. `BACKGROUND`, `ARTWORK`. |
| `level` | number? | `Layers/Layer/@level`. |

### Optional import keys

These appear only on frames that came from an imported FrameXML file. They are what lets the editor
say *where a widget came from* instead of implying FrameForge invented it.

| Key | Type | Meaning |
| --- | --- | --- |
| `kind` | string | The FrameXML element: `FRAME`, `BUTTON`, `TEXTURE`, `FONTSTRING`, `STATUSBAR`. Omitted when `FRAME`. |
| `setAllPoints` | bool | `true` when the source asked the widget to fill its anchor target. |
| `extraAnchors` | array | Anchors after the first, **in source order**, kept verbatim. |
| `sourceName` | string | The name exactly as written in the source, before `$parent` expansion. |
| `anonymous` | bool | `true` when the source element had no name. |
| `inherits` | string | The `inherits` template name, retained **unresolved**. |
| `placeholder` | bool | `true` for a stand-in FrameForge synthesized for a frame the file references but does not define. |
| `sourceLocation` | object | One-based parser-reported `line` and `column` of the declaring XML element. |

`extraAnchors` entries use the same shape as the primary anchor minus `point`'s redundancy:

```json
{ "point": "BOTTOMRIGHT", "relativeTo": "Panel", "relativePoint": "BOTTOMRIGHT",
  "offsetX": -4, "offsetY": 4 }
```

They are **read-only in the editor**. WoW solves several anchors simultaneously, and rewriting one
without knowing the order the game would apply them in is not safe, so they are preserved and
displayed rather than silently dropped or optimistically rewritten.

## `source`

```json
"source": {
  "type": "wow-framexml",
  "fileName": "NativeHuntsFrame.xml",
  "referencePath": "content/client/Interface/FrameXML/NativeHuntsFrame.xml",
  "readOnly": true
}
```

| Key | Type | Meaning |
| --- | --- | --- |
| `type` | string | `frameforge-project` or `wow-framexml`. |
| `fileName` | string? | File name only, so it is portable. |
| `referencePath` | string? | Where the source lived, relative to the project file when known. **Display only.** |
| `readOnly` | bool | `true` when the source must never be written. |

`referencePath` is provenance, not a required project dependency. FrameForge opportunistically uses
it to find source-relative artwork when the original XML still exists; the saved project still opens
and uses texture fallbacks when it has been moved or deleted. When the project file is written, an
absolute source reference is made relative to the project, falling back to just the file name, so no
developer-machine absolute path is serialized and a project/XML pair can be copied together.

## `editor`

```json
"editor": {
  "workspace": "design",
  "activeDesignState": "standard-hunt",
  "lockedElements": ["CustomPanel"],
  "groups": [
    { "name": "Blizzard Dungeon Finder Frame", "locked": true, "expanded": false,
      "concept": "stock-framework",
      "stockIdentity": "wow-3.3.5a-12340:Interface/FrameXML/LFDFrame.xml:LFDParentFrame",
      "members": ["LFDParentFrame", "Texture#1", "LFDParentFrameTab1"] }
  ],
  "designStates": [
    { "id": "standard-hunt", "name": "Standard Hunt" }
  ],
  "designObjects": [
    { "frame": "CustomPanel", "displayName": "Hunt Record", "states": ["standard-hunt"] },
    { "frame": "TitleText", "displayName": "Title Text", "textOverride": "NATIVE HUNTS",
      "textStyle": { "baseStyle": "GameFontNormalLarge", "size": 14,
        "color": { "r": 1, "g": 0.82, "b": 0, "a": 1 },
        "outline": "NONE", "shadow": true, "justifyH": "CENTER" } },
    { "frame": "Divider", "displayName": "Divider", "designAsset": "assets/hunt_divider.png" }
  ],
  "designOrder": ["CustomPanel", "Divider", "TitleText"]
}
```

Every field is optional. `workspace` defaults to `design`. A group is a logical set of frame names; deleting
it never deletes its members or changes their `parent`. An element is protected when it appears in
`lockedElements` or belongs to a locked group. Locks prevent ordinary editor geometry changes and
canvas drag only. `expanded` is presentation state and never changes the lock. `concept` and
`stockIdentity` identify an editor abstraction; they do not contain Blizzard XML or artwork.

`designObjects` keeps user-facing names separate from internal/import identities. `textOverride`
is visible design text applied without changing the imported/source text; null/absent means the
source text remains active, while an empty value is an intentional blank override. `designAsset`
is a normalized, forward-slash, project-relative path to artwork owned by the project; absolute
paths and traversal are invalid. An absent or empty `states` array means **All States**; otherwise the object is visible only in the listed authored
`designStates` when one is active. Authored states are independent from the read-only Native Hunts
preview-state catalog, and neither executes Lua. Older v1 files without `editor`, or with only the
earlier groups/locks fields, load with safe defaults, so these additive fields do not require a
format-version bump.

`textStyle` is optional DESIGN metadata for a FontString. `baseStyle` names an authentic style
resolved from the configured build-12340 client's `Fonts.xml` and `FontStyles.xml`; it does not
embed a font or copy a client definition. The other keys are nullable overrides. Absence inherits
the base style, while `outline: "NONE"` and `shadow: false` explicitly disable those effects.
Colors preserve RGBA. Older projects and imported FontStrings without this object continue using
their declared `FontTemplate` and justification unchanged.

`designOrder` lists custom DESIGN frame identities from back to front. Members of a conceptual
`stock-framework` group are excluded: their existing source paint order stays intact and the group
acts as the foundation beneath normal custom content. Missing or stale names are ignored; custom
objects omitted by an older v1 project are appended deterministically in `designObjects` order.
Changing this list never rewrites source hierarchy, strata, levels, or FrameXML provenance.

## FrameXML is never a save target

A project imported from XML carries no path, so `Save` has nothing to overwrite and routes through
**Save As**. On top of that, any `.xml` save target is refused outright: FrameForge does not write
XML at all, and silently converting an addon's FrameXML into JSON would be the worst possible
outcome. `ProjectCodec.CanSaveTo` is the single place that decision lives.
