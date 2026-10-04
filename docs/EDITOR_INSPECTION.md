# Editor Provenance and Composition Inspection

FrameForge keeps source layout, effective presentation, and editor organization distinct.

## Source and composition

Imported widgets retain their parser-reported XML line and column. The Inspector shows the element
name and kind, source filename and resolved/reference path, parent, inherited template, origin
classification, and whether a design-time state contributes effective values. Line information is
recorded only when the XML parser supplies it; FrameForge does not estimate locations.

For a selected container, Visual Composition lists useful direct Texture, FontString, Button, and
StatusBar children rather than dumping the recursive subtree. Each entry is selectable and reports
its declared WoW asset, physical resolution source/path, decoded image dimensions, effective
geometry, texCoords, tint/alpha, and template/runtime provenance where applicable.

Size and appearance are reported separately. A full-frame texture with full coordinates produces a
cautious fixed-artwork stretch warning. Cropped or atlas artwork instead states that resizing
changes rendered geometry but does not claim distortion. Native Hunts' Record panel is the latter:
its 280×86 frame is source geometry and its panel texture is a 280×86 crop of a 512×128 TGA.

## Groups and locks

Groups are FrameForge metadata. They do not reparent frames, rewrite XML, or affect WoW paint order.
Groups can be created, renamed, populated from the current selection, depopulated, locked, and
deleted without deleting their elements. Individual elements can also be locked.

A lock leaves content visible and tree-selectable while blocking canvas drag, Inspector geometry
edits, and deletion until explicitly unlocked. A locked group's members inherit that protection.
Groups and locks are stored only in the optional `.fforge.json` `editor` block.

## Origin presentation

The current evidence-backed categories are Project/imported source, Blizzard stock/template,
Runtime/design-time, and Stand-in/synthesized. The Origins control changes canvas presentation and
pickability only. It does not touch `visible`, preview definitions, source XML, or serialization.
Each descendant is classified independently, so hiding the external Blizzard LFD shell does not
mechanically hide Native Hunts content merely because that content is parented beneath it.

Paint order remains WoW paint order. Hit-test ordering is separate: an unlocked project/runtime
component gets the first click over locked/background stock chrome, while repeat-click cycling and
explicit tree selection retain access to every overlapping element.
