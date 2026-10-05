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

## Multi-selection

DESIGN keeps one ordered selection with a defined primary. Clicking replaces it; Ctrl/Cmd/Shift-click
on the canvas or in the tree adds or removes one object. The most recently selected surviving object
is the **primary**, and the primary is what the inspector, the anchor chrome, and the composition panel
describe. Align needs two objects, distribution needs three, and the buttons stay disabled until the
selection is that large.

The other members are not invisible. On the canvas the primary is drawn in solid gold and the rest in
thin dashed blue, and in the tree every selected row carries a marker (`◉` primary, `•` otherwise).
A four-object selection that only outlined its primary would be indistinguishable from a one-object
selection, and the user would have no way to see what the next align was about to act on. Anchor lines
stay exclusive to the primary: four widgets' anchors at once is a hairball, not more information.

During a multi-selection the single-object editors (name, parent, size, anchors, offsets, appearance,
state membership, draw order) are hidden rather than left populated. Every one of those fields
describes exactly one object, so leaving them editable while four objects are selected is how a
background panel gets renamed by accident. **Show only primary** narrows the selection back to one
object, and INSPECT states plainly that it is describing the primary only.

Dragging any member of a multi-selection moves the whole selection as one rigid group, by the same
rules an align obeys. Deleting is still single-object, because destroying four frames from one
mis-click is not a recoverable mistake. Switching DESIGN state drops selected objects the new state
does not show and says so; canvas filter toggles do not, because a filtered-out object still exists and
can still be named deliberately in the tree.

## Alignment and distribution

Every arrange command only ever **adds to `OffsetsX`/`OffsetsY`**. It never writes an absolute
position, never touches `Point`, `RelativePoint`, `RelativeTo`, parent, size, stratum, level, kind,
visual, or text, and never touches a frame the caller did not name. In imported Blizzard markup the
anchor relationship is the source of truth, not the resulting number, so rewriting one would change
the file's meaning rather than its appearance.

Model space is **+Y up**, so "align tops" moves objects up to the highest selected top edge and
"align bottoms" moves them down to the lowest. Alignment targets the bounds of the whole requested
selection, **including locked objects**: a locked object is not moved, but it is still part of the
selection the user is aligning against, and ignoring its extent would move the editable objects
somewhere the user did not ask for. Distribution instead works purely among the objects it may move,
because equal spacing needs two fixed endpoints and inventing one on behalf of a locked object would
be a bigger guess than the situation calls for. The outermost two objects stay put and the gaps
between consecutive objects become equal.

A selection containing both a container and one of its children is applied outermost-first with the
layout re-resolved between frames, so each child is measured against where it actually is after its
parent moved and is nudged the remaining distance. For the same reason a selected frame whose own
anchor target is also selected is never given a second offset change during a group drag: it already
follows that frame, and adding one would move it twice.

## What refuses to move, and why

Anything that cannot honour the offsets-only contract is reported instead of approximated. The
interesting failure mode is not "nothing happened" but "three of my four frames moved and I do not know
why the fourth did not", so every command returns a sentence naming what it left alone:

- **Locked objects** are skipped untouched and counted. They still count toward alignment bounds.
- **`SetAllPoints`** frames ignore `OffsetsX`/`OffsetsY` by definition.
- **Multi-anchor stretch frames** take their size from two edges; moving one of those edges changes
  its size, which is not what align or drag promised.
- **Unresolved anchors or unresolved geometry** mean the tool does not know where the object is, so it
  will not claim to know where it should go.

Reporting beats guessing, because a wrong guess is indistinguishable from a correct one until the user
runs the game.
