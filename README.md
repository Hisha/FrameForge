# FrameForge

FrameForge is a WoW UI frame layout editor. This repository contains a clean .NET 10 / Avalonia 12 desktop application that replaces the original Electron implementation.

## Current Version

v0.1.0 — geometry and editor foundation.

## Architecture

- **FrameForge.Core**: UI-independent model, geometry, hierarchy, anchor resolution, visibility, paint order, viewport transforms, JSON serialization, and Native Hunts example.
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

## Build

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

Returns `0` on SMOKE_PASS; writes a screenshot.

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

## Current Limitations

This is an early geometry/editor foundation; advanced tooling features may be added in future releases.

## License

MIT
