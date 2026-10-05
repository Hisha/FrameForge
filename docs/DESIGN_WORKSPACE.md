# Design workspace

FrameForge has two presentations of one project and one renderer.

- **DESIGN** is the default. It emphasizes friendly object names, canvas geometry, visibility,
  locks, and state membership. Anonymous textures use an identifiable asset filename when possible;
  otherwise the UI keeps a neutral label such as `Texture #23`.
- **INSPECT** preserves the imported hierarchy and internal identities, source file/line/column,
  inheritance, origin, physical asset resolution, texCoords, visual composition, and diagnostics.

Display names are editor metadata. They do not rename imported XML elements or write FrameXML.

## Draw order

Custom DESIGN objects have an explicit back-to-front order. New Image, Text, and Frame objects are
added at the front, above the conceptual Blizzard framework. Bring Forward and Send Backward move
one custom layer; Bring to Front and Send to Back move to the corresponding custom boundary. The
locked Blizzard composition remains the lowest normal authoring layer, so Send to Back cannot bury
custom work beneath it or reorder its internal pieces.

The DESIGN tree follows the same order: **the bottom item is front**. INSPECT continues to show the
source hierarchy and is not reordered by these authoring controls.

## WoW text styles

DESIGN text uses a curated set of styles demonstrated by the configured WoW 3.3.5a build-12340
client: `GameFontNormal`, `GameFontHighlight`, their Small and Large variants,
`GameFontHighlightMedium`, `GameFontDisable`, `GameFontGreen`, `GameFontRed`, and
`GameFontNormalHuge`. The catalog is filtered through the live stock resolver, so an unavailable
client definition is never presented as supported.

Each preset resolves transitively through machine-local `Interface/FrameXML/Fonts.xml` and
`FontStyles.xml`. DESIGN shows the effective font resource, size, RGBA color, outline, shadow, and
horizontal alignment. Size, color, outline, shadow, and alignment may be overridden without
changing the client definition; Reset returns them to the selected preset. Copy/Paste Text Style
copies only this presentation data, never text, Name, geometry, anchors, or state membership.

Only client-backed WoW styles are in scope. FrameForge stores style names and overrides but never
bundles Blizzard font files. Project-owned custom fonts and content export remain future work.

## Dungeon Finder starting framework

`New → Blizzard UI → Dungeon Finder / LFD` requires a validated local WoW 3.3.5a build 12340 client.
FrameForge materializes only requested resources into its existing machine-local managed cache,
imports cached `Interface/FrameXML/LFDFrame.xml` without executing Lua, and resolves referenced art
through the same client provider. No Blizzard resource is copied into the repository, project, or
release package.

In DESIGN the imported stock composition is represented by one conceptual object, **Blizzard
Dungeon Finder Frame**. It starts collapsed and locked. Expansion only reveals its underlying model
elements; it never unlocks them. Unlocking requires confirmation in the desktop UI, and relocking is
one action. Project-owned objects can be added and edited over the protected framework at any time.

## Authored design states

Authored states are named visibility compositions stored in `.fforge.json`. An object with no state
IDs belongs to **All States**; otherwise it appears when any assigned state is active. Switching
states applies visibility to a transient presentation clone. It does not mutate imported geometry or
execute Lua.

Imported Native Hunts preview states remain a separate evidence-backed catalog: XML Defaults, Idle,
Standard Hunt, Elite Hunt, and Hunt Complete. Keeping these concepts separate avoids pretending an
arbitrary addon Lua state machine has been imported or authored.
