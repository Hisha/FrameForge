# FrameForge — WoW UI Designer Research and Architecture Review

Date: 2026-10-08  
Target: World of Warcraft 3.3.5a, build 12340  
Scope: research and architecture only; no implementation

## Evidence labels

- **Verified** — observed in FrameForge source, the installed build-12340 interface cache, a linked source repository, or an official project page.
- **Inference** — a technical conclusion drawn from verified evidence, but not directly stated by a source.
- **Recommendation** — a proposed FrameForge design decision.

## 1. Executive summary

**Recommendation:** adopt architecture **C, a hybrid**, but make its two workflows deliberately different:

1. A new-interface project uses a WoW-compatible semantic scene model with one explicit, module-owned composition root. Every authored object has an explicit owner and a typed anchor target. It generates new FrameXML.
2. An existing-FrameXML project keeps the original XML text/tree as its authority and exposes a semantic editing projection. It applies narrow, verified source-preserving edits instead of regenerating the document from a lossy model.

The two workflows should share parsers, template resolution, anchor math, rendering, validation, assets, identity rules, and diagnostics. They should not share the same persistence authority or export algorithm.

The immediate Native Hunts failure was not merely an XML-writer typo. It arose from four cooperating design choices:

1. `FrameDef` uses `Parent == null` plus `RelativeTo == null` to mean “anchor to the screen/UIParent” ([`FrameDef.cs`](../src/FrameForge.Core/Models/FrameDef.cs), lines 47–67 and 158–165).
2. the Design workspace creates every new object at the project level with a null parent ([`MainWindowViewModel.cs`](../src/FrameForge.Desktop/ViewModels/MainWindowViewModel.cs), `AddDesignObject`, lines 2139–2181);
3. `FunctionalDesignExporter.Rebase` converts every top-level object to absolute center offsets, then explicitly clears its parent and anchor target ([`FunctionalDesignExport.cs`](../src/FrameForge.Core/Export/FunctionalDesignExport.cs), lines 142–165); and
4. `Wow335Exporter.WriteGeometry` serializes a missing translated parent/anchor as `UIParent` ([`Wow335Export.cs`](../src/FrameForge.Core/Export/Wow335Export.cs), lines 630–640).

The generated root itself is correctly reparented to the functional host and set to fill it, but the 49 children retain explicit screen-relative anchors. In WoW, parenting affects ownership, visibility, scale, and frame hierarchy; an explicit `relativeTo="UIParent"` still positions a child in global UI coordinates. That is why the composition appeared centered on the screen and why the consuming module had to rewrite each anchor.

The correct long-term invariant is:

> In a host-bound export, the generated composition root is the sole boundary between module space and design space. All internal geometry is root-, parent-, or sibling-relative. `UIParent` is an explicit opt-in, never a null fallback.

No researched legacy tool provides code that should simply be dropped into FrameForge. WoW UI Designer contains useful historical ideas—schema-driven widgets, skins, template-aware loading—but its public release explicitly could not edit arbitrary existing UI files and no reusable source/license was located. AddOn Studio demonstrates that simultaneous XML/text/tree/designer views are feasible, but its current source and application license could not be verified. MoveAnything is a runtime override system rather than a document editor, and its code is All Rights Reserved. `wowless` and `wow-ui-sim` validate the value of a separate interpretation/testing layer, but neither is a 3.3.5a visual editor.

## 2. Existing WoW UI tool comparison

| Tool | Purpose and era | Representation / behavior | Import, export, templates, Lua | Relevance to FrameForge |
|---|---|---|---|---|
| WoW UI Designer | Windows C#/.NET 2 IDE; releases span early WoW through an MPQ update for WoW 4, with WoW 3 button rendering noted in 2008 | A renderer for `LayoutFrame` XML plus “skin files” describing available components, properties, and XML ↔ Windows Forms conversion | Loaded FrameXML, addons, dependencies, MPQs, templates, includes, and Lua. The release page says multiple inheritance loaded but was not correct. Critically, it says the designer could edit only files it created, not arbitrary existing UI files | Strong historical warning against pretending a visual model is a lossless XML model. Skin/provider concepts are useful; implementation is not available for reuse |
| AddOn Studio for WoW | Visual Studio Shell-based addon IDE, historically introduced in 2007 and still documented as a 2022 edition | Visual designer, FrameXML tree including virtual controls, property editors, snap lines, Lua editor, project/TOC management | Official documentation advertises switching between XML and designer views and immediate synchronization, resource browsing, addon load/deploy support. Internal implementation and exact round-trip guarantees could not be verified from source | The best product-level precedent for paired source and semantic views. It is evidence for workflow, not reusable implementation |
| MoveAnything | In-game addon for moving, scaling, hiding, and changing opacity of existing frames; modern CurseForge releases target later WoW clients | Discovers live frames and stores runtime overrides; source includes frame editors, profiles, virtual movers, hooks/reapplication | It does not parse/export FrameXML or provide round-trip source editing. Lua runs in the actual client, so it sees final runtime frames and can reapply mutations after other code changes them | Useful conceptual precedent for stable frame identity, override provenance, and runtime ownership. It is not an exporter architecture |
| wowless | Headless WoW client Lua and FrameXML interpreter intended for addon testing; current and pre-alpha | Emulates UI APIs and interprets Lua/FrameXML against generated product data | Source is available. It is a test/runtime project, not a visual authoring tool, and its supported products should not be assumed equivalent to 3.3.5a | Useful as an architectural example: runtime interpretation belongs in an isolated subsystem. Potential future test-oracle ideas only after version-compatibility study |
| WoW UI Simulator (`wow-ui-sim`) | External addon loader/renderer aimed at current WoW UI simulation | Builds a runtime-like frame tree and renders screenshots | Open source, but not a 3.3.5a authoring or round-trip editor | Demonstrates that faithful runtime emulation is a substantial product of its own. Do not hide such complexity inside export code |

### 2.1 WoW UI Designer

**Verified:** the official release page describes a C# 2005/.NET 2 Windows application with XML and Lua editors, Blizzard `UI.xsd` validation, MPQ access, a visual renderer, and skin files that define component properties and XML conversion. The change log records support for includes, project load order, named parents, templates, addon dependencies, and attempts to rewrite XML close to its input. It also records incorrect multiple-inheritance results. Most importantly, the public release warns that the editor only handles files it created, not existing UI files. See [WoW UI Designer’s official release page](https://www.wowinterface.com/downloads/info4222-WoWUIDesigner.html).

**Inference:** its architecture likely had at least three representations: XML, a renderer/runtime-like object graph, and Windows Forms/property descriptors supplied by skins. The feature history shows that inheritance and faithful rewrite were persistent edge cases, not solved incidentally by using XML.

**Recommendation:** copy the separation of a versioned component/template catalog from the editing model, but not the assumption that an editor-owned model can safely rewrite arbitrary source.

### 2.2 AddOn Studio for World of Warcraft

**Verified:** official pages describe a Visual Studio-based IDE with a visual FrameXML designer, snap lines, a tree including virtual controls, Lua editing, XML editing, WoW resource browsing, project management, source control, and deployment. The product page says the user can switch between designer and XML views and see changes reflected between them. See [AddOn Studio overview](https://www.addonstudio.org/wiki/AddOn_Studio), [2022 edition](https://addonstudio.org/wiki/AddOn_Studio_2022_for_WoW), and [documentation](https://www.addonstudio.org/wiki/AddOn_Studio_for_World_of_Warcraft_documentation).

**Verified with limitation:** a 2007 report says the historical project published source on CodePlex, but this review could not retrieve a verifiable surviving source tree or a license for the current application. The site’s licensing page applies to wiki/site content and says content is All Rights Reserved unless otherwise noted; it must not be treated as an application source-code license. See [the historical InfoQ report](https://www.infoq.com/news/2007/12/Warcraft-Studio/) and [wiki licensing](https://addonstudio.org/wiki/AddOn_Studio_Wiki%3ALicensing).

**Inference:** AddOn Studio probably used Visual Studio’s document/designer serialization services or an equivalent XML-backed designer. Feature descriptions are insufficient to establish whether unsupported XML and scripts round-trip byte-for-byte.

**Recommendation:** treat it as workflow evidence only. Do not reuse binaries or inferred internals without locating authoritative source and license terms.

### 2.3 MoveAnything

**Verified:** MoveAnything changes live frames at runtime and stores user settings for position, scale, visibility, and opacity. The visible fork contains Lua/XML source and explicitly states that the addon is All Rights Reserved and that the fork is not the maintainer. See [CurseForge](https://www.curseforge.com/wow/addons/move-anything) and [the source-visible fork](https://github.com/Bowbee/MoveAnything).

**Inference:** its strong results come from operating on the client’s realized object graph after template expansion and Lua execution. That bypasses the hardest static-editor problem rather than solving source fidelity.

**Recommendation:** use its concepts—named-frame lookup, a separately stored override, reapplication, and explicit runtime ownership—as design inspiration. Do not copy its code, and do not use runtime patching to compensate for invalid generated FrameXML.

### 2.4 Other relevant open-source projects

**Verified:** [`wowless`](https://github.com/wowless/wowless) is a headless Lua and FrameXML interpreter intended for addon tests. Its own code is MIT licensed, while vendored components retain separate licenses ([license](https://raw.githubusercontent.com/wowless/wowless/main/LICENSE)).

**Verified:** [`wow-ui-sim`](https://github.com/Osso/wow-ui-sim) loads and renders addons outside WoW and is GPL-3.0 licensed ([license](https://raw.githubusercontent.com/Osso/wow-ui-sim/master/LICENSE)).

**Recommendation:** FrameForge may study both, but direct `wow-ui-sim` code reuse would introduce GPL obligations into the MIT FrameForge project. `wowless` code reuse is legally more compatible, but only after a technical check for 3.3.5a data/API semantics. Prefer black-box test ideas and independently implemented interfaces until that check exists.

## 3. Verified source-code availability and licensing

| Project/material | Source status | License status | Reuse decision |
|---|---|---|---|
| FrameForge | **Verified:** local source | **Verified:** MIT (`LICENSE`) | Native codebase |
| WoW UI Designer | **Not located:** downloadable executable and detailed release notes, but no authoritative public repository/source archive found | **Not located:** no software license granting reuse found | Do not reuse code or redistribute binaries; ideas only |
| AddOn Studio, current | **Not verified:** current downloads/issues exist; no authoritative current source tree was established | **Not verified:** wiki licensing is not an application license | Do not reuse code; architecture/product comparison only |
| AddOn Studio, historical | **Reported, not retrieved:** 2007 source on CodePlex | Not verified during this review | No reuse basis until the exact archive and license are recovered |
| MoveAnything | **Verified:** source-visible GitHub forks | **Verified:** All Rights Reserved on CurseForge/fork notice | Do not copy code; behavioral study only |
| wowless | **Verified:** GitHub source | **Verified:** MIT for project-owned code; vendored code separate | Potentially reusable after version and dependency review |
| wow-ui-sim | **Verified:** GitHub source | **Verified:** GPL-3.0 | Study externally; avoid direct incorporation unless FrameForge intentionally accepts GPL compatibility consequences |
| Extracted Blizzard 3.3.5a FrameXML | **Verified:** installed extracted build-12340 cache and public mirror | The public mirror exposes no license grant; files originate from Blizzard’s client | Reference/compatibility evidence only; never copy Blizzard implementation into FrameForge output |

This is an engineering reuse assessment, not legal advice. Absence of a license is not permission.

## 4. Native WoW 3.3.5a FrameXML findings

The strongest evidence is the installed cache at:

`~/.local/share/FrameForge/assets/wow-3.3.5a-12340/Interface/FrameXML/`

The public [3.3.5 interface-file mirror](https://github.com/wowgaming/3.3.5-interface-files) provides linkable copies. FrameForge’s existing [`NATIVE_HUNTS_TEMPLATE_INVENTORY.md`](NATIVE_HUNTS_TEMPLATE_INVENTORY.md) records the local build/cache provenance.

### 4.1 Ownership is not positioning

**Verified:** XML nesting creates an owner/parent relationship for nested frames and regions; a `parent` attribute can name a different owner. In the native [`LFDFrame.xml`](https://github.com/wowgaming/3.3.5-interface-files/blob/main/LFDFrame.xml), `LFDParentFrame` is a hidden top-level child of `UIParent`, and `LFDQueueFrame` is a nested child with `setAllPoints="true"`.

**Verified:** anchors determine geometry independently. An anchor’s `relativeTo` names its target. If `relativeTo` is omitted, the natural reference is the owner/parent; a root ultimately resolves against `UIParent`. Therefore, a child can be owned by frame A but explicitly anchored to `UIParent`.

**Inference:** the exported Native Hunts root correctly inherited show/hide/scale from its host, but its explicitly `UIParent`-anchored descendants used global coordinates. Parenting alone could not relocate them into the PvE content area.

**Recommendation:** represent ownership and anchoring as separate, mandatory semantic relationships. Never infer a global anchor merely because a string is null.

### 4.2 Coordinate and scale domains

**Verified:** `UIParent` is the global UI coordinate root. Anchor offsets are interpreted in the target frame’s effective UI coordinate/scale context. Parent scale and visibility propagate to children, while anchors specify positions.

**Recommendation:** a FrameForge composition must declare its design-space root dimensions or fill behavior. Export should validate that every internal anchor resolves within that root’s ownership domain. Cross-domain anchors should be explicit external references and produce a warning or error in a host-bound package.

### 4.3 `$parent`, templates, and inheritance

**Verified:** [`CharacterFrameTemplates.xml`](https://github.com/wowgaming/3.3.5-interface-files/blob/main/CharacterFrameTemplates.xml) declares `CharacterFrameTabButtonTemplate` as `virtual="true"`. Its nested textures use names such as `$parentLeftDisabled`, and sibling anchors refer to expanded `$parent...` names. A virtual template is a definition, not a concrete runtime control.

**Verified:** the installed native files use `inherits` to apply templates and support comma-separated inheritance in 3.3.5-era definitions. Template descendants, scripts, attributes, and `$parent` substitutions are materialized in the instance context.

**Recommendation:** use a versioned template registry loaded in TOC/XML order. Keep declared values, inherited effective values, and provenance separate. Do not flatten template children into the authored document. Resolve `$parent` only in an instance/effective view, never destructively in the source representation.

### 4.4 Layers, strata, levels, and regions

**Verified:** textures and font strings are regions placed inside `<Layers><Layer level="...">`. Draw-layer levels such as `BACKGROUND`, `BORDER`, `ARTWORK`, `OVERLAY`, and `HIGHLIGHT` order regions within a frame. Frame `frameStrata` and `frameLevel` order frames. These are distinct dimensions.

**Verified:** buttons and status bars are frames with behavior/regions; textures and font strings are not interchangeable frame children. Native templates commonly add button textures and font objects through inheritance.

**Recommendation:** model `DrawLayer` only on regions and `FrameStrata`/`FrameLevel` only on frames. The renderer may combine them into a paint key, but import/export must not convert one into the other.

### 4.5 Script and file load order

**Verified:** XML is processed in load order. `<Script file="...">` and `<Include file="...">` participate in that order, and frame `OnLoad` scripts run as instances are created. A script can subsequently call `SetPoint`, `SetSize`, `Show`, or `Hide`, overriding static XML. The native LFD document loads `LFDFrame.lua` and then instantiates frames that call named functions.

**Verified:** Blizzard’s 3.3.5 [`UIPanelTemplates.lua`](https://github.com/wowgaming/3.3.5-interface-files/blob/main/UIPanelTemplates.lua) shows a concrete initialization contract: `PanelTemplates_SetTab` calls `PanelTemplates_UpdateTabs`, which loops from `1` to `frame.numTabs`; `PanelTemplates_SetNumTabs` is the API that assigns the numeric field. This demonstrates why source/load order and runtime initialization cannot be treated as decorative metadata.

**Recommendation:** preserve exact source order in imported documents. In generated packages, produce and validate an explicit load plan. The module’s controller functions must load before XML handlers that call them, while any controller initialization that accesses generated globals must occur after the design XML instantiates them.

### 4.6 Blizzard-owned versus module-owned controls

**Verified:** client-owned frames and templates already exist at runtime. An addon/module may parent to or inherit from them, but redeclaring their global identities risks collisions and altered load behavior.

**Recommendation:** export references and declared dependencies, not copies, for Blizzard frames/templates/assets. A module owns its host, controller, and generated composition root. FrameForge owns generated visual descendants and a manifest. Neither should mutate Blizzard FrameXML on disk.

### 4.7 3.3.5a versus modern WoW

**Recommendation:** treat build 12340 as a separate product profile. Do not infer support from Retail/Classic tools or current API documentation. Pin schema, native definitions, templates, Lua APIs, texture formats, and validation fixtures to the installed client’s build identity. Modern projects are architectural comparisons only.

## 5. FrameForge architectural audit

### 5.1 Project document model

**Verified:** `Project` schema v1 is an ordered, flat `IReadOnlyList<FrameDef>` plus screen, source, functional-export, and editor metadata ([`Project.cs`](../src/FrameForge.Core/Models/Project.cs), lines 3–40). Hierarchy is reconstructed from string `Parent` names. `FrameDef` contains one flattened primary anchor plus extra anchors, nullable string parent/target references, unresolved `Inherits`, and a combined visual payload ([`FrameDef.cs`](../src/FrameForge.Core/Models/FrameDef.cs), lines 44–185).

Strengths:

- immutable records and pure transformations are easy to test;
- explicit source names/locations and placeholders retain useful provenance;
- parent and `relativeTo` are already recognized as different concepts;
- extra anchors are retained instead of silently dropped;
- editor-only grouping/states are separated from core geometry.

Architectural limits:

- null simultaneously means an omitted XML attribute, implicit parent reference, project root, and global `UIParent`, depending on context;
- names are untyped strings, so local, external, parent-relative, `$parent`-derived, and unresolved references are indistinguishable;
- hierarchy is not structurally owned by a document/composition root;
- it cannot faithfully represent XML order, comments, unknown nodes/attributes, scripts, includes, template definitions, namespaces, or lexical form;
- draw layers and frame strata are not fully separated;
- imported declared state and inherited/effective state are not first-class parallel views.

**Inference:** correcting only the current exporter would remove the immediate defect, but the same ambiguity would return in drag/reparent, template expansion, multi-root export, or source import.

### 5.2 Visual design surface and geometry

**Verified:** `LayoutResolver` is a pure absolute-layout engine. It resolves `UIParent` to a centered rectangle, supports parent/sibling targets, `setAllPoints`, and a deliberately limited multiple-anchor solver ([`LayoutResolver.cs`](../src/FrameForge.Core/Geometry/LayoutResolver.cs), lines 5–72 and 84–240). `LayoutCanvas` and the rendering layers consume that result for drawing, hit testing, selection, and guides.

**Verified:** `AddDesignObject` creates new design controls with `Parent = null` and center anchors ([`MainWindowViewModel.cs`](../src/FrameForge.Desktop/ViewModels/MainWindowViewModel.cs), lines 2139–2181). That makes screen-rooted geometry normal editor state.

**Recommendation:** retain the pure geometry engine and canvas interaction, but change the semantic input. A new project should always contain a composition root; new objects should default to that root or the selected container. The geometry result may remain absolute for rendering, while the authored relationship remains explicit and local.

### 5.3 FrameXML import and existing-XML editing

**Verified:** `FrameXmlImporter` intentionally supports only a subset—Frame, Button/CheckButton, FontString, Texture, and StatusBar—and reports that it does not execute Lua or fully resolve external templates/includes/scripts ([`FrameXmlImporter.cs`](../src/FrameForge.Core/Import/FrameXmlImporter.cs)). It maps region layer values `ARTWORK` and `OVERLAY` into frame strata for presentation (`MapStratum`, lines 194–215), which is lossy semantics.

**Verified:** the layout-only path is substantially safer. `FunctionalLayoutExporter` reopens the authoritative source, verifies its SHA-256 identity, reimports a baseline, builds a constrained patch, rejects unsupported structural/non-layout changes, detects some Lua geometry conflicts, and returns patched text. `LayoutPatchApplier` checks source location/baseline and verifies that text outside edit spans did not drift ([`FunctionalLayoutExport.cs`](../src/FrameForge.Core/Export/FunctionalLayoutExport.cs) and [`LayoutPatch.cs`](../src/FrameForge.Core/Export/LayoutPatch.cs)).

**Recommendation:** retain and generalize this source-preserving idea. Replace location-only patching with a lossless syntax tree/token map plus a supported semantic projection. Existing XML must never be regenerated from `Project.Frames`.

### 5.4 Generated XML and functional composition

**Verified:** `Wow335ExportBuilder.Build` selects design objects, allocates deterministic runtime/wrapper names, validates bindings/assets/states, and translates references between authored objects. `Wow335Exporter.WriteXml` creates one root frame, parents it to a stock runtime frame, fills that parent, and emits wrappers for Texture/FontString regions ([`Wow335Export.cs`](../src/FrameForge.Core/Export/Wow335Export.cs), especially lines 541–640).

**Verified:** functional composition takes that generated root and inserts it before the source document’s closing `</Ui>`. It sets the root parent to `FunctionalExport.HostFrameName` and `setAllPoints="true"`. Before insertion, however, `Rebase` turns top-level authored objects into center offsets relative to a conceptual “stock foundation” and clears their parent/target. The writer then emits `UIParent` for each missing target.

#### Exact 49-control root cause

1. The 49 design objects were legitimate top-level design records because new objects default to `Parent = null`.
2. The design was previewed against a stock-foundation rectangle in global model space.
3. `Rebase` computed each object’s absolute rectangle and converted it to a center offset from the foundation.
4. `Rebase` set `Parent = null`, `RelativeTo = null`, `ParentRuntimeName = null`, and `RelativeRuntimeName = null`.
5. `WriteGeometry` used `item.RelativeRuntimeName ?? item.ParentRuntimeName ?? "UIParent"`.
6. Functional composition reparented only the generated root. Explicit child anchors continued to target `UIParent`.

**Root-cause allocation:**

- **Project model:** primary contributor—ambiguous null references and no explicit composition root.
- **Editor creation workflow:** contributor—creates screen-rooted controls by default.
- **Layout engine:** not intrinsically wrong, but it normalizes the ambiguous model into convincing absolute rectangles that can be misused as export data.
- **Functional exporter:** direct defect—destructively rebases relationships into absolute coordinates and discards ownership/target semantics.
- **XML writer:** direct defect—silently maps missing references to `UIParent` rather than rejecting an invalid host-bound model.

**Recommendation:** remove `Rebase` from the future export architecture. If a legacy screen-authored design is imported once, perform a single project migration that computes local root-relative anchors, save those relationships, show a migration report, and never repeat conversion at export time.

### 5.5 Template handling

**Verified:** `StockTemplateResolver` is explicitly “the deliberately small Native Hunts dependency set” and hard-codes `CharacterFrameTabButtonTemplate`, `LFDParentFrame`, and a fixed resource list ([`StockTemplateResolver.cs`](../src/FrameForge.Desktop/Templates/StockTemplateResolver.cs), lines 74–156). It provides useful provenance, diagnostics, font measurement, and cached asset materialization.

**Recommendation:** retain its provider interfaces/provenance patterns, but replace the implementation with a generic build-profile template registry. It must index arbitrary loaded files in order, represent virtual definitions and inheritance chains, expand `$parent` in an effective instance view, and report unsupported/ambiguous merges.

### 5.6 Assets

**Verified:** FrameForge already has managed build-12340 asset caching, MPQ extraction, path normalization, BLP/TGA decoding/materialization, stock font handling, and project-owned artwork copying. Existing inventories and tests verify provenance and hashes.

**Recommendation:** retain this subsystem. Move target-specific path rules into a build profile, and make export manifests list logical WoW paths, source project assets, output hashes, and conversions. Do not embed machine paths.

### 5.7 States and Lua behavior

**Verified:** editor metadata supports design states and per-object membership. Preview behavior also contains a Native Hunts-specific catalog keyed to concrete names ([`PreviewStates.cs`](../src/FrameForge.Desktop/Preview/PreviewStates.cs), `NativeHuntsPreviewStates`, lines 171–315). Functional export can generate an `OnUpdate` polling bridge from state/value mappings ([`FunctionalDesignExport.cs`](../src/FrameForge.Core/Export/FunctionalDesignExport.cs), `AddRuntimeBridge`/`BuildBridge`, lines 198 onward).

**Recommendation:** keep generic authored state membership and preview overrides; move project-specific preview catalogs out of core product code. Export state membership as declarative metadata/manifest. The consuming module’s Lua owns when a state is active and applies visibility/value changes. FrameForge should not synthesize polling behavior by inspecting another module’s controls.

### 5.8 Identity and tests

**Verified:** deterministic runtime names, wrapper collision checks, manifests, source hashes, and source-preservation tests are strong. The real Native Hunts functional-export regression asserts 49 objects and selected names, but does not assert that every top-level generated child is root-relative. Synthetic parentage testing covers only a child whose parent is another authored object. Thus the defect fell between inventory tests and relationship tests.

**Recommendation:** make graph invariants the primary export tests:

- exactly one composition root;
- every emitted control is owned by that root or a descendant;
- every internal anchor target resolves to parent/root/sibling;
- no implicit `UIParent` in host-bound output;
- external references are declared in the manifest;
- all source IDs map one-to-one to runtime IDs;
- XML load order satisfies referenced functions/templates/frames;
- generated XML parses under the build-12340 schema subset and a runtime smoke harness;
- source-preserving mode changes only authorized spans.

## 6. Architectural alternatives

Scoring: 1 = poor, 3 = workable, 5 = strongest.

| Criterion | A. Current model + corrected exporter | B. Native XML primary for everything | C. Hybrid semantic model + XML-backed edit mode |
|---|---:|---:|---:|
| Initial implementation cost | 4 | 2 | 2 |
| New-interface visual UX | 4 | 2 | 5 |
| 3.3.5a semantic reliability | 2 | 4 | 5 |
| Arbitrary XML round trip | 1 | 5 | 5 |
| Templates/inheritance | 2 | 4 | 5 |
| Unsupported XML preservation | 1 | 5 | 5 |
| Maintainability | 2 | 3 | 4 |
| Extensibility | 2 | 3 | 5 |
| Testability | 3 | 3 | 5 |
| Low risk of repeating current defect | 1 | 4 | 5 |
| Migration simplicity | 5 | 2 | 3 |

### A. Current project model with corrected exporter

**Approach:** add a root, modify `Rebase`, and default missing generated references to that root.

Advantages:

- fastest route to repairing the current package;
- preserves most UI, serialization, geometry, and tests;
- low short-term migration cost.

Disadvantages:

- nullable strings remain semantically overloaded;
- the flat model remains unable to preserve source order, scripts, includes, unknown XML, template definitions, and lexical details;
- imported and editor-authored objects still inhabit one lossy type;
- later work on template inheritance and external references will add special cases;
- source round-trip remains impossible beyond constrained patches.

**Conclusion:** acceptable only as a short-lived stabilization branch, not the long-term architecture requested.

### B. Native FrameXML as the primary document model

**Approach:** the editor directly manipulates a lossless XML document; generated interfaces are also stored as XML.

Advantages:

- strongest structural and lexical round-trip potential;
- hierarchy, order, scripts, includes, template definitions, and unsupported nodes remain present;
- no model-to-XML impedance mismatch for imported files.

Disadvantages:

- XML is a poor home for editor-only grouping, constraints, state membership, asset provenance, and stable identity for anonymous nodes;
- effective values require a template/load-order/runtime projection anyway;
- every drag could require careful source surgery and conflict handling;
- starting from a blank visual project becomes coupled to XML syntax decisions;
- a raw DOM does not by itself encode 3.3.5 semantics or prevent invalid references.

**Conclusion:** best authority for existing-source editing, but unnecessarily awkward as the sole authoring model.

### C. Hybrid model

**Approach:** define one WoW-compatible semantic graph used by layout/render/validation. For new interfaces, persist that graph plus editor metadata and generate XML. For imported interfaces, keep a lossless XML syntax document as authority and build the semantic graph as a projection with source bindings.

Advantages:

- explicit root/ownership/typed anchors prevent accidental global geometry;
- visual authoring stays ergonomic;
- imported source remains lossless;
- template expansion and provenance can be shared;
- runtime-dependent/unsupported content remains visible but protected;
- tests can target syntax preservation, semantic graph invariants, and export independently.

Costs:

- two persistence/export paths must be maintained honestly;
- source projection needs stable node IDs, incremental reparse, and conflict diagnostics;
- template resolution is meaningful new work;
- v1 projects need migration or retirement.

**Conclusion:** highest long-term reliability and the only option that serves both scenarios without making one subordinate to the other.

## 7. Recommended architecture and workflow

### 7.1 Shared semantic core

**Recommendation:** introduce a target-neutral editor shell around a build-specific `WowUiProfile`, with these concepts:

- `UiDocument`: target profile, files/load plan, dependencies, identities, diagnostics.
- `CompositionRoot`: exactly one generated root for a new-interface export; declared host contract and local design extent.
- `UiNode`: frame or region with stable internal ID, optional runtime/global name, declared owner, type, ordered children, and source binding.
- `Anchor`: ordered anchors with `Point`, `RelativePoint`, offsets, and a typed `AnchorTarget` (`Parent`, `CompositionRoot`, `LocalNode(id)`, `ExternalGlobal(name)`, or `Unresolved(sourceText)`).
- `DeclaredProperties` and `EffectiveProperties`: inheritance never overwrites authored facts.
- `TemplateReference`: ordered template names plus versioned registry provenance.
- `FramePaint`: strata/level/backdrop; `RegionPaint`: draw layer/sublevel/texture/font.
- `ScriptReference`/`ScriptBlock`: retained and ordered, but not executed by the visual editor.
- `EditorMetadata`: groups, guides, state definitions/membership, selection, friendly names, asset provenance.

Invalid or unresolved source must remain representable. Export of a new project, however, should fail closed on unresolved owners, anchors, templates, identity collisions, and load-order dependencies.

### 7.2 Scenario A — create a new interface

1. Create a project from blank, a Blizzard template reference, or a reusable FrameForge component.
2. Declare a composition-root contract: root runtime name, expected module host name, fill/explicit-size behavior, and target build.
3. Add controls under the root or selected container. New anchors default to `Parent`, never `UIParent`.
4. Preview using the build-12340 template registry and asset provider. Effective inherited controls are visible but clearly distinguished from authored nodes.
5. Define visual states and semantic binding keys without module-specific Lua expressions.
6. Validate the graph and export `Design.xml`, artwork, and a manifest. The module supplies behavior and load order.

The semantic project is authoritative; generated XML is reproducible output.

### 7.3 Scenario B — modify existing FrameXML

1. Open the source set with its TOC/include/script load plan.
2. Preserve original bytes/tokens and build a bound semantic projection.
3. Allow only edits whose source mapping and runtime effect are understood. Mark inherited, Lua-owned, unresolved, or unsupported properties read-only with provenance.
4. Apply token/node patches to the original documents. Never serialize the entire document from the semantic graph.
5. Reparse, compare semantic intent, and prove untouched spans/files are identical.

The source document is authoritative; the semantic model is an editor projection.

### 7.4 Why two exporters are desirable

Generated documents benefit from canonical, deterministic formatting and strict ownership rules. Existing documents benefit from minimal lexical change and preservation of constructs FrameForge does not understand. Combining those goals in one serializer is the exact failure mode seen in older visual tools and in FrameForge’s current import/composition split.

## 8. Proposed module integration contract

The contract must be generic and versioned; examples below are illustrative, not Native Hunts-specific.

### 8.1 Package contents

- `Design.xml`: generated visual composition only.
- `frameforge.manifest.json`: schema version, target build, root, host requirement, controls, states, bindings, assets, templates, external references, and load constraints.
- `Artwork/...`: converted/copied project-owned assets at declared logical WoW paths.

FrameForge should not generate gameplay/server logic or copy Blizzard FrameXML.

### 8.2 Root and host

- The module declares a globally named host frame, for example `MyModuleContentHost`.
- FrameForge emits exactly one globally named root, for example `MyModuleDesignRoot`, with `parent="MyModuleContentHost"` and either `setAllPoints="true"` or one explicit host-relative root anchor/size.
- Every generated descendant is lexically/structurally owned by the root or a generated descendant.
- Internal anchors use `Parent`, generated local identities, or the composition root.
- `UIParent` and other external globals are allowed only when explicitly authored and listed as external references. A host-bound export with accidental external anchors fails validation.

This makes show/hide, movement, scale, strata constraints, and lifetime flow from host to composition without per-control repairs.

### 8.3 Identities and Lua access

- Every authored object has an immutable internal ID independent of its label.
- Export assigns a deterministic, valid, collision-checked runtime name.
- The manifest maps internal ID, authored label, exported name, widget type, owner path, state membership, and Lua access expression.
- Wrapper frames are manifest-visible implementation details and have deterministic reserved names.
- Renames are explicit breaking changes or include a generated migration map; no fuzzy lookup.

### 8.4 Artwork

- The manifest maps source asset ID to logical client path, output path, format, dimensions, and SHA-256.
- Generated XML contains only client-relative paths.
- Stock assets/templates are dependencies, not copied content.
- Conversion is deterministic and reports unsupported dimensions/formats before export.

### 8.5 Visual states and values

- FrameForge exports state IDs and the per-control visual overrides/membership needed to render each state.
- It exports semantic binding keys such as `quest.progress`, not Lua snippets that probe another control.
- The module owns state selection and data retrieval. A small module-owned adapter applies state and values to exported controls.
- No generated polling `OnUpdate` bridge is required. Event-driven module code is the default.

### 8.6 Load and initialization order

The package manifest declares this required sequence:

1. Blizzard dependencies/templates are available under build 12340.
2. module controller Lua is loaded if XML event handlers reference its functions;
3. module host FrameXML is instantiated;
4. generated `Design.xml` is included/loaded, creating the composition root and descendants;
5. module initialization that reads generated globals runs after step 4.

The consuming module should express this through TOC/XML order. FrameForge validates the declared plan and emits no hidden initializer frame. If an optional ready notification is needed later, define one versioned adapter callback in the contract; do not infer arbitrary source controls or generate polling.

### 8.7 Template dependencies

- Each external template/global lists name, expected source file, target build, and whether it is required or optional.
- Export does not reproduce a Blizzard template’s children.
- Validation runs against the same versioned registry used by preview.
- Missing or version-mismatched dependencies fail package validation.

## 9. Retain, refactor, or replace

| Component | Decision | Reason / target state |
|---|---|---|
| Avalonia shell, canvas interaction, selection, guides, tree UX | **Retain/refactor** | Valuable editor investment; bind it to semantic nodes and explicit roots |
| `LayoutResolver` anchor math | **Retain/refactor** | Pure and well tested; consume typed targets and report complete constraint provenance |
| `FrameHierarchy` utilities | **Refactor** | Build from structural ownership, not nullable name strings |
| Project/editor immutable records and codec discipline | **Retain pattern** | Strong testability; introduce schema v2 rather than stretching v1 |
| v1 `Project.Frames`/`FrameDef` as universal document | **Replace** | Too lossy and ambiguous for source and native semantics |
| `FrameXmlImporter` semantic subset | **Refactor** | Keep parsing/diagnostic knowledge as a projection builder; add a lossless syntax authority |
| `LayoutPatchBuilder`/`LayoutPatchApplier` | **Retain and generalize** | Correct preservation philosophy; expand to syntax-node bindings and multi-file edits |
| `Wow335ExportBuilder` validation, naming, manifest, asset inventory | **Retain/refactor** | Good deterministic mechanics; feed from new semantic graph |
| `FunctionalDesignExporter.Rebase` | **Replace** | It destroys relationships and caused global anchors |
| silent XML fallback to `UIParent` | **Replace with validation error** | Missing target is not equivalent to an intentional global target |
| generated `OnUpdate` functional bridge | **Replace** | Module behavior belongs in an explicit event-driven adapter contract |
| `StockTemplateResolver` | **Replace implementation; retain provider/provenance ideas** | Currently Native Hunts-specific; needs generic ordered build registry |
| MPQ/cache/asset resolution, BLP/TGA/font support | **Retain** | Target-specific assets are already a strong subsystem |
| generic design states/membership | **Retain/refactor** | Useful authoring metadata; export declaratively |
| `NativeHuntsPreviewStates` in product code | **Remove from core architecture** | Module-specific behavior violates product independence; use project fixtures/plugins/examples |
| source hashes, manifests, collision checks, deterministic output | **Retain** | Essential safety and reproducibility |
| current inventory-only functional tests | **Refactor/add graph assertions** | Object count does not prove correct ownership/anchors |

## 10. Migration strategy

**Recommendation:** create project schema v2 and avoid a permanent v1 compatibility layer.

1. Keep v1 reading for one bounded migration release, but do not preserve v1 export semantics.
2. For a simple v1 design, create a composition root and convert screen/foundation absolute rectangles once into root-relative anchors. Report every conversion and require review when an object refers outside the design set.
3. Reject automatic conversion for ambiguous multiple roots, unresolved references, Lua-owned geometry, cross-domain multi-anchors, or conflated layer/stratum data. Offer “open read-only” and recreate/copy supported visuals.
4. Existing imported XML projects should be reopened from their authoritative source into the new XML-backed workflow, not converted from their lossy `Frames` snapshot.
5. The current 49-object Native Hunts design need not be migrated. Use it as a regression fixture for the new root invariant, then rebuild if that is simpler.
6. Remove the old functional composition path after v2 package export and module-contract tests pass. Do not maintain both indefinitely.

This favors a small, explainable migration over compatibility fields that contaminate the new model.

## 11. Proposed implementation phases

### Phase 0 — executable semantics and fixtures

- freeze representative build-12340 fixtures and provenance;
- write behavior tests for ownership, `UIParent`, relative anchors, `$parent`, virtual templates, layers/strata, file order, and tab initialization;
- add a generated-package graph validator and failing regression for the 49 global anchors.

Exit: current behavior and desired invariants are executable before model replacement.

### Phase 1 — semantic graph and target profile

- introduce typed identities, node ownership, anchor targets, composition root, declared/effective properties, and `Wow335Profile`;
- adapt `LayoutResolver` and canvas through a compatibility projection;
- separate frame strata from region draw layers.

Exit: the canvas can render a v2 in-memory project with no nullable global fallback.

### Phase 2 — generic template registry

- index installed XML in declared load order;
- represent virtual templates and inheritance chains;
- implement `$parent` expansion in effective views;
- preserve provenance and unresolved merges;
- replace hard-coded Native Hunts dependencies with manifest-driven requirements.

Exit: stock tabs/fonts/frames render through generic resolution and build-pinned tests.

### Phase 3 — new-interface authoring and package export

- add root/host contract UI and parent-by-default creation;
- generate deterministic XML, artwork, manifest, and load plan;
- fail on unresolved/global anchor leakage;
- add a small generic module adapter specification and fixture.

Exit: a blank project can produce a host-bound package that requires no geometry repair.

### Phase 4 — lossless existing-XML workspace

- add token-preserving XML documents, stable node/source bindings, include/TOC graph, and protected unsupported nodes;
- generalize patching to supported properties and multiple files;
- expose declared/effective/runtime-unknown provenance in the editor.

Exit: a representative native/module XML set can be edited with byte-identical unsupported content.

### Phase 5 — states, bindings, and module SDK

- formalize manifest state/value schema;
- provide module-side event-driven adapter examples/tests;
- remove source-probing `OnUpdate` generation and module-specific preview catalogs.

Exit: modules consume the same versioned contract without FrameForge knowing their domain.

### Phase 6 — migration and retirement

- ship bounded v1 migration/read-only support;
- rebuild or migrate selected examples;
- delete `Rebase`, implicit `UIParent` fallback, and obsolete functional profile paths;
- document compatibility boundaries.

Exit: one supported v2 architecture per workflow, with no silent legacy behavior.

## 12. Technical risks and unresolved questions

1. **Exact 3.3.5 inheritance merge rules.** Multiple inheritance ordering, child merging, script combination, and attribute precedence need fixture-based confirmation from build-12340 behavior. The old WoW UI Designer explicitly reported incorrect results here.
2. **Schema versus runtime permissiveness.** Blizzard XML sometimes relies on behavior not fully captured by `UI.xsd`. Validation must distinguish schema errors, known client behavior, and FrameForge support limits.
3. **Lua-owned geometry.** Static analysis can identify obvious setters but cannot prove final runtime state. The UI needs an “unknown/runtime-owned” status rather than a false preview guarantee.
4. **Cross-frame multi-anchor constraints.** The present solver intentionally declines some anchors targeting different frames. Decide whether to implement a dependency/constraint solver or keep them visible but read-only.
5. **Anonymous identity stability.** Source-backed anonymous nodes need IDs resilient to nearby edits without writing synthetic attributes into user XML.
6. **Lossless XML editing.** Namespace prefixes, entity/CDATA form, comments, whitespace, newline style, attribute order, and encoding must survive. A standard DOM serializer is insufficient.
7. **Load graph discovery.** Addon TOCs, XML includes/scripts, Blizzard FrameXML order, and module core patches may not form one simple file list. The project must record which load environment is authoritative.
8. **Host sizing and scale.** The contract needs an explicit rule for fill versus fixed design extents and how preview handles non-1 UI scale/aspect ratio. Parent-relative anchors alone do not define responsive behavior.
9. **Clipping expectations.** 3.3.5 frame ownership does not imply arbitrary child clipping. If designs require clipping, it must be represented by supported native constructs, not assumed from desktop UI frameworks.
10. **Protected frames/combat restrictions.** Some Blizzard controls are secure/protected. Build-12340 rules should be profiled separately; generated packages must not claim that all live mutations are legal.
11. **Global names and collisions.** 3.3.5 XML commonly creates globals. Namespacing, maximum lengths/allowed characters, wrapper reservations, and collisions across separately loaded packages need formal validation.
12. **External project reuse.** wowless is MIT but not automatically 3.3.5-compatible; `wow-ui-sim` is GPL-3.0. Any reuse requires both a version fit analysis and a dependency/license decision.
13. **Module adapter boundary.** Decide whether FrameForge ships only a manifest specification, a small Lua 5.1 reference adapter, or generated adapter stubs. The adapter must remain optional and generic.
14. **Project-specific preview data.** Determine a plugin/fixture mechanism so domain samples can enrich previews without entering FrameForge core.

## 13. Primary sources and local evidence

### Native WoW 3.3.5a

- [3.3.5 interface files mirror](https://github.com/wowgaming/3.3.5-interface-files)
- [`UIPanelTemplates.lua`](https://github.com/wowgaming/3.3.5-interface-files/blob/main/UIPanelTemplates.lua) — tab-count contract and tab state
- [`LFDFrame.xml`](https://github.com/wowgaming/3.3.5-interface-files/blob/main/LFDFrame.xml) — `LFDParentFrame`, nested ownership, `setAllPoints`, layers, Lua handlers
- [`LFDFrame.lua`](https://github.com/wowgaming/3.3.5-interface-files/blob/main/LFDFrame.lua) — runtime state and geometry behavior
- [`CharacterFrameTemplates.xml`](https://github.com/wowgaming/3.3.5-interface-files/blob/main/CharacterFrameTemplates.xml) — virtual tab template, `$parent`, sibling anchors
- [`RuneFrame.xml`](https://github.com/wowgaming/3.3.5-interface-files/blob/main/RuneFrame.xml) — additional native template/anchor examples
- Installed build-12340 cache under `~/.local/share/FrameForge/assets/wow-3.3.5a-12340/Interface/FrameXML/`
- Local provenance: [`NATIVE_HUNTS_TEMPLATE_INVENTORY.md`](NATIVE_HUNTS_TEMPLATE_INVENTORY.md)

### Tools

- [WoW UI Designer official release page and change log](https://www.wowinterface.com/downloads/info4222-WoWUIDesigner.html)
- [AddOn Studio overview](https://www.addonstudio.org/wiki/AddOn_Studio)
- [AddOn Studio 2022](https://addonstudio.org/wiki/AddOn_Studio_2022_for_WoW)
- [AddOn Studio documentation](https://www.addonstudio.org/wiki/AddOn_Studio_for_World_of_Warcraft_documentation)
- [AddOn Studio wiki licensing](https://addonstudio.org/wiki/AddOn_Studio_Wiki%3ALicensing)
- [Historical AddOn Studio/CodePlex report](https://www.infoq.com/news/2007/12/Warcraft-Studio/)
- [MoveAnything on CurseForge](https://www.curseforge.com/wow/addons/move-anything)
- [MoveAnything source-visible fork and license warning](https://github.com/Bowbee/MoveAnything)
- [wowless source](https://github.com/wowless/wowless) and [MIT license](https://raw.githubusercontent.com/wowless/wowless/main/LICENSE)
- [WoW UI Simulator source](https://github.com/Osso/wow-ui-sim) and [GPL-3.0 license](https://raw.githubusercontent.com/Osso/wow-ui-sim/master/LICENSE)

### FrameForge source reviewed

- [`Project.cs`](../src/FrameForge.Core/Models/Project.cs) and [`FrameDef.cs`](../src/FrameForge.Core/Models/FrameDef.cs)
- [`LayoutResolver.cs`](../src/FrameForge.Core/Geometry/LayoutResolver.cs) and `FrameHierarchy.cs`
- [`FrameXmlImporter.cs`](../src/FrameForge.Core/Import/FrameXmlImporter.cs)
- [`FunctionalLayoutExport.cs`](../src/FrameForge.Core/Export/FunctionalLayoutExport.cs) and [`LayoutPatch.cs`](../src/FrameForge.Core/Export/LayoutPatch.cs)
- [`Wow335Export.cs`](../src/FrameForge.Core/Export/Wow335Export.cs)
- [`FunctionalDesignExport.cs`](../src/FrameForge.Core/Export/FunctionalDesignExport.cs)
- [`StockTemplateResolver.cs`](../src/FrameForge.Desktop/Templates/StockTemplateResolver.cs)
- [`MainWindowViewModel.cs`](../src/FrameForge.Desktop/ViewModels/MainWindowViewModel.cs)
- [`LayoutCanvas.cs`](../src/FrameForge.Desktop/Controls/LayoutCanvas.cs) and rendering layers
- [`PreviewStates.cs`](../src/FrameForge.Desktop/Preview/PreviewStates.cs)
- [`FunctionalDesignExportTests.cs`](../tests/FrameForge.Core.Tests/FunctionalDesignExportTests.cs), import/layout/template/state/asset tests, and the real Native Hunts fixtures

## Final decision

**Recommendation:** redesign FrameForge around a build-pinned semantic graph with explicit ownership and typed anchors, plus a separate lossless source-backed workspace for existing XML. Retain the canvas, geometry math, asset pipeline, deterministic naming/manifests, and source-preserving patch philosophy. Replace the v1 universal frame model, hard-coded template resolver, absolute `Rebase`, implicit `UIParent` fallback, generated polling bridge, and product-level Native Hunts behavior.

The success criterion is not merely “49 controls export.” It is that the exported graph has one declared integration boundary, every control’s ownership and anchor domain are provable before writing XML, and a consuming module never has to repair the geometry or infer how to initialize the result.
