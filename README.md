# FrameForge

FrameForge is a WoW UI frame layout editor. This repository contains a clean .NET 10 / Avalonia 12 desktop application that replaces the original Electron implementation.

## Current Version

v0.1.0 — geometry and editor foundation, plus read-only FrameXML import.

## Architecture

- **FrameForge.Core**: UI-independent model, geometry, hierarchy, anchor resolution, visibility, paint order, viewport transforms, JSON serialization, the generic FrameXML importer, and Native Hunts example.
- **FrameForge.Desktop**: Avalonia desktop UI (editor window, layout canvas, frame tree, inspector, diagnostics, and headless smoke self-check).

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
- Strict lossless `.fforge.json` project serialization (v1)
- Native Hunts example included
- **Read-only FrameXML import** (see below)

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
| `inherits` | recorded; templates are **not** resolved, and that is reported |
| `hidden` | recorded and inherited by children |
| unnamed elements | given a canonical identity (`Texture#1`, `FontString#12`, …) |
| `Layers`/`Layer`, `Frames`, `Size`, `AbsDimension` | parsed |
| `<Scripts>`, `On*`, paint, font templates, `<BarTexture>` | skipped, silently or with a note |

### Support levels and diagnostics

Full / Partial / Unsupported describes **geometry fidelity only**. A `FontString` that inherits
`GameFontHighlightSmall` is fully supported: FrameForge reproduces where it sits and how big it
is, and separately reports that it does not measure fonts. Scripts are never executed.

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
addon file (SHA-256 `42673d3f…a47c0`). The 184 Core tests and the desktop smoke test both run
against it; the desktop smoke test also imports it from disk and hashes the file afterwards to
prove it was not modified.

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

This is an early geometry/editor foundation; advanced tooling features may be added in future releases.

## License

MIT
