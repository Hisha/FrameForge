# FrameForge v0.3.0 — Native BLP2 Export

FrameForge v0.3.0 moves the project from visual design and preview into verified in-game export
for a concrete WoW 3.3.5a integration.

## Highlights

- Native BLP2 type-1 export with managed, cross-platform DXT5/BC3 compression, eight-bit alpha,
  and complete mip chains.
- Usage-based texture sizing targets two source texels per largest displayed UI pixel, preserves
  aspect ratio, and never upscales.
- Optional source-resolution preservation for assets with runtime uses that are not represented
  in the authored layout.
- Corrected FrameXML ownership hierarchy, parent-relative anchoring, root rebasing, deterministic
  draw order, and initial visibility for state-specific owners.
- Deterministic XML, manifests, source packaging, and generated texture payloads.
- Independent BLP2 container, mip, bounds, and decoded-base-mip validation before export is
  published.
- Legacy deterministic TGA export remains available from both the desktop menu and CLI.
- Existing `.fforge.json` format-version 1 projects remain supported without migration.

## Native Hunts acceptance result

| Metric                | Previous TGA                  | v0.3.0 BLP2           |
| --------------------- | ----------------------------- | --------------------- |
| Native Hunts textures | 11                            | 11                    |
| Total size            | 160 MiB                       | 3.54 MiB              |
| Largest texture       | 4096×1024                     | 1024×1024             |
| WoW PTR result        | Crashed when displaying Hunts | Rendered successfully |

The successful WoW 3.3.5a PTR test used the default optimized BLP2 textures, not the
source-resolution-preservation option. This validates the Native Hunts layout and asset set used
for that test; it does not imply that every possible FrameXML layout or client environment has
been validated in-game.

## Export choices

- Desktop: **Export → WoW 3.3.5a — BLP2/DXT5…** (preferred), the source-resolution variant,
  or **Legacy TGA…**.
- CLI: `FrameForge --export-wow335 <project.fforge.json> <directory>` defaults to BLP2/DXT5.
  Add `--preserve-source-resolution`, or select legacy output with `--texture-format tga`.

See `docs/WOW_335_EXPORT.md` for the full output contract, validation rules, and Content Manager
integration boundary.
