# FrameForge

FrameForge is a WoW UI frame layout editor. This repository contains a clean .NET 10 / Avalonia 12 desktop application that replaces the original Electron implementation.

## Current Version

v0.1.0 — geometry/editor foundation, read-only FrameXML import, real local-client assets, focused
stock template/font fidelity, and non-destructive design-time preview states.

## Architecture

- **FrameForge.Core**: UI-independent model, geometry, hierarchy, anchor resolution, visibility, paint order, viewport transforms, JSON serialization, the generic FrameXML importer, and Native Hunts example.
- **FrameForge.Desktop**: Avalonia desktop UI with one layered canvas (visual content, debug overlay,
  selection overlay), frame tree, inspector, diagnostics, and a live smoke self-check.

Dependency direction: `FrameForge.Desktop → FrameForge.Core`.

## WoW Coordinate Semantics

- WoW model uses a screen-centred origin with **+X right**, **+Y up**.
- Avalonia canvas uses **+Y down**; conversion is isolated to `Viewport`.
- Dragging updates **only offsetX/offsetY**. `Point`, `RelativeTo`, `RelativePoint` remain unchanged.
- Downward screen drags produce negative model Y.

## Features (v0.1)

- 9-anchor anchoring system with parent/sibling/descendant relationships
- Layout resolution with visibility inheritance, paint order, and cycle detection
- Visual editor with selection, zoom, fit-to-content, and inspector editing
- First-class **Debug**, **Preview**, and **Hybrid** canvas modes
- Canvas category/hidden/helper toggles and None/Selected/All label policies
- Tree structure/visual filters, case-insensitive search with ancestor context, direct canvas
  selection, overlap cycling, and Reveal Selection
- Parser-backed XML file/line provenance and a jumpable direct visual-composition Inspector with
  physical asset source, image dimensions, effective geometry, and cautious resize guidance
- Editor-only groups and element/group locks persisted separately from WoW layout semantics
- Default **DESIGN** workspace with friendly names, a concise property editor, basic Frame/Text/Image
  creation, and authored Lua-free state composition; **INSPECT** retains the complete source hierarchy
- New-project choices for a blank project or a protected conceptual Dungeon Finder framework resolved
  on demand from the user's validated local 3.3.5a build-12340 client (no bundled Blizzard resources)
- Presentation-only Project, Blizzard stock, Runtime/design-time, and Stand-in origin filters;
  editable project content is preferred over locked stock chrome during overlap selection
- Retained texture/text/button/status-bar metadata with honest Preview stand-ins
- Real TGA and verified WoW 3.3.5a BLP texture rendering from project assets, manual roots, or an
  application-managed cache populated on demand from the user's own client
- Focused build-12340 stock-definition resolution for Native Hunts' external parent, character-tab
  template, and six `GameFont*` styles, with machine-local Friz Quadrata loading and provenance
- Explicit design-time **XML Defaults**, **Idle**, **Standard Hunt**, **Elite Hunt**, and
  **Hunt Complete** states, with source/effective provenance and no Lua execution or source mutation
- Strict lossless `.fforge.json` project serialization (v1)
- Native Hunts example included
- **Read-only FrameXML import** (see below)

See `docs/DESIGN_WORKSPACE.md` for the progressive-disclosure model, stock-framework safety, and
the separation between authored design states and imported preview states.

## FrameXML Import

`Open` accepts a WoW 3.3.5a FrameXML file as well as a FrameForge project; the extension picks
the reader, so there is no import mode to choose.

Imported XML is **read-only source material**. FrameForge never writes XML, so:

- the project keeps no path, and `Save` routes through **Save As** as `.fforge.json`;
- any `.xml` save target is refused, not silently written;
- the file on disk is never modified. A read-only `Source` record is stored in the project so the
  origin survives a save/load round trip, and reopening a saved project never needs the XML.

The importer is generic: any `<Ui>` document, not a hard-coded file. It models `Frame`, `Button`,
`Texture`, `FontString` and `StatusBar` elements, and every layout-affecting construct that decides
where a widget ends up:

| Construct | Handling |
| --- | --- |
| omitted `relativeTo` | treated as the parent |
| omitted `relativePoint` | treated as the same value as `point` |
| `$parentName` | expanded against the enclosing frame |
| `setAllPoints` | fills its anchor target, ignoring size and offsets |
| `inherits` | always recorded; the focused Native Hunts build-12340 stock subset is resolved at presentation time, while other templates are reported unresolved |
| `hidden` | recorded and inherited by children |
| unnamed elements | given a canonical identity (`Texture#1`, `FontString#12`, …) |
| `Layers`/`Layer`, `Frames`, `Size`, `AbsDimension` | parsed |
| `<Scripts>`, `On*` | never executed; runtime behavior is reported |
| texture paths/coords/colors, literal text, font references, `<BarTexture>` | retained in the project; rendered where Phase 3 can do so honestly |

### Support levels and diagnostics

Full / Partial / Unsupported on import describes **declared project geometry fidelity only**.
Machine-local stock resolution is a separate presentation layer: when the user's client definitions
are cached, Native Hunts' six required `GameFont*` styles provide authoritative size, color,
justification, shadow, and local font metrics. Scripts are never executed.

Nothing layout-affecting is dropped silently. Every finding carries a stable code and a
severity, and the import banner lists them:

| Severity | Meaning |
| --- | --- |
| Info | context the user may want; never a correctness claim |
| Warning | layout-affecting input not represented exactly |
| Error | input that could not be represented or parsed |

### External frames

A file that references a frame it does not define (NativeHuntsFrame.xml references
`LFDParentFrame`, which its Lua sizes at 355x500) gets **one** clearly marked stand-in. The
widgets positioned against it are reported Partial, because their size depends on something the
XML does not say.

### Acceptance

`tests/FrameForge.Core.Tests/Fixtures/NativeHuntsFrame.xml` is an unmodified copy of the real
addon file (SHA-256 `42673d3f…a47c0`). The Core tests and the desktop smoke test both run
against it; the desktop smoke test also imports it from disk and hashes the file afterwards to
prove it was not modified.

The visual artifact roadmap is in [docs/VISUAL_RENDERING_PLAN.md](docs/VISUAL_RENDERING_PLAN.md).
The preview-state behavior, sample-data boundary, and extension architecture are documented in
[docs/PREVIEW_STATES.md](docs/PREVIEW_STATES.md).
Editor provenance, composition, groups, locks, and origin filtering are documented in
[docs/EDITOR_INSPECTION.md](docs/EDITOR_INSPECTION.md).

## Local WoW Client Assets

The normal stock-art workflow is deliberately short:

1. Open FrameForge.
2. Select your WoW 3.3.5a build 12340 client once in **WoW Client**.
3. Open FrameXML.
4. Choose **Resolve Missing Assets**.
5. FrameForge stores only the required preview assets, focused XML/Lua definitions, and font in its
   machine-local cache.

FrameForge ships **no Blizzard artwork** and never writes to the WoW installation or its MPQs.
The selected client path and extracted preview copies stay on the local machine and are never put
in `.fforge.json`. Opening and editing still works with no client configured: custom artwork and
existing cache entries render, while uncached stock artwork/templates/fonts keep honest stand-ins
and diagnostics. Manual asset roots remain available as an advanced/fallback feature.

See [docs/WOW_CLIENT_ASSETS.md](docs/WOW_CLIENT_ASSETS.md) for validation, storage locations,
archive precedence, security behavior, dependency rationale, template/font scope, and manual
extraction troubleshooting. The exact Native Hunts dependency evidence is in
[docs/NATIVE_HUNTS_TEMPLATE_INVENTORY.md](docs/NATIVE_HUNTS_TEMPLATE_INVENTORY.md).

```bash
dotnet restore FrameForge.slnx
dotnet build FrameForge.slnx
```

## Test

```bash
dotnet test FrameForge.slnx
```

## Run (Desktop)

```bash
dotnet run --project src/FrameForge.Desktop/FrameForge.Desktop.csproj
```

## Smoke Test (Headless Validation)

```bash
FRAMEFORGE_SMOKE_OUT=/tmp/frameforge-smoke.png dotnet run --project src/FrameForge.Desktop/FrameForge.Desktop.csproj -- --smoke
```

Returns `0` on SMOKE_PASS. Writes two screenshots: one for the built-in example, and
`frameforge-smoke-import.png` for the imported FrameXML layout.

## Self-Contained Publish (Linux x64)

```bash
bash scripts/publish-linux.sh
```

Produces `dist/FrameForge-0.1.0-linux-x64.zip` (self-contained, no .NET runtime required).

## Self-Contained Publish (Windows x64)

```bash
pwsh scripts/publish-win.ps1
# With installer:
pwsh scripts/publish-win.ps1 -Installer
```

Produces `dist/FrameForge-0.1.0-win-x64.zip` and `dist/FrameForge-Setup-0.1.0.exe` when installer is built.

## Release Process

Create and push a tag to trigger GitHub release:

```bash
git tag v0.1.0
git push origin v0.1.0
```

GitHub Actions builds, tests, publishes both platforms and creates the release with:
- `FrameForge-0.1.0-linux-x64.zip`
- `FrameForge-0.1.0-win-x64.zip`  
- `FrameForge-Setup-0.1.0.exe`

All official releases are **self-contained** and do not require installing the .NET runtime separately.

## Project Format

`.fforge.json` v1 — see `docs/PROJECT_FORMAT.md`. Native Hunts example: `examples/native-hunts.fforge.json`.

## Opening Files

Open reads both a `.fforge.json` project and a WoW FrameXML `.xml` document; the file-type filter
defaults to **All Supported Files** so both are visible as soon as you navigate to a directory.
Narrower filters are available in the dropdown. On Linux the dialog is the XDG desktop portal,
which receives the filter list once and then hands the dialog to a remote backend, so changing
the file-type dropdown only re-lists the current directory if the backend chooses to. See
`docs/OPEN_DIALOG.md`.

## Current Limitations

Preview uses restrained stand-ins when artwork is unresolved or unsupported and when fonts or
templates are unavailable. Explicit preview states can supply inspected Lua-driven presentation
values without executing Lua, mutating source XML, or emulating a WoW client. See the visual
rendering plan for the remaining work.

## License

MIT
