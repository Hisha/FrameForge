# Design workspace

FrameForge has two presentations of one project and one renderer.

- **DESIGN** is the default. It emphasizes friendly object names, canvas geometry, visibility,
  locks, and state membership. Anonymous textures use an identifiable asset filename when possible;
  otherwise the UI keeps a neutral label such as `Texture #23`.
- **INSPECT** preserves the imported hierarchy and internal identities, source file/line/column,
  inheritance, origin, physical asset resolution, texCoords, visual composition, and diagnostics.

Display names are editor metadata. They do not rename imported XML elements or write FrameXML.

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
