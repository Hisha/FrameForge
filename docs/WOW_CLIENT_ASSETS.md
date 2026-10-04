# Local WoW Client Assets

FrameForge can read required stock Interface textures from a user-owned World of Warcraft 3.3.5a
client. FrameForge distributes no Blizzard artwork, never changes the client or its MPQs, and does
not place extracted files in projects or releases.

## Normal workflow

1. Expand **WoW Client**, choose **Browse**, and select the directory containing `Wow.exe` and
   `Data`.
2. FrameForge validates exactly WoW 3.3.5a build 12340 and detects one locale.
3. Open FrameXML. Source-relative and manually configured assets resolve as usual.
4. FrameForge reports unresolved stock references. Choose **Resolve Missing Assets** to copy only
   those unique files into the managed cache, refresh resolution, and repaint Preview.
5. Use **Revalidate**, **Clear**, or **Clear Stock Cache** to refresh the client state, forget the
   selection, or remove only FrameForge-owned cached stock files.

Client configuration is optional. With no valid client, custom artwork and existing cached stock
art continue to render; uncached stock references use the normal explicit stand-in. FrameXML import
never depends on client configuration and works offline.

## Validation and locale discovery

A plausible client must have `Wow.exe`, `Data`, and one locale directory containing its matching
`locale-<locale>.MPQ`. FrameForge reads the executable's UTF-16 `FileVersion` resource directly,
without Windows APIs, so a Windows installation can be validated on Linux. Only version
`3.3.5.12340` is supported. Other readable versions are reported as unsupported rather than used.

Locale directory names use the WoW `llCC` form (for example `enUS`). The architecture does not
hardcode `enUS`. Zero matches is invalid; more than one plausible locale is ambiguous and requires
the installation to be disambiguated before extraction.

## Archive precedence

Archives are discovered case-insensitively and assigned this low-to-high precedence:

1. global base archives: `common`, `common-2`, `expansion`, `lichking`;
2. locale base archives: `locale-<locale>`, `expansion-locale-<locale>`,
   `lichking-locale-<locale>`;
3. global `patch`, `patch-2`, `patch-3`, ... in numeric order;
4. locale `patch-<locale>`, `patch-<locale>-2`, `patch-<locale>-3`, ... in numeric order.

Lookup searches this list from highest to lowest priority. Installer backup, speech, and unrelated
archives are not searched for Interface artwork. This deterministic order selects locale patches
over global patches and later numbered patches over earlier/base data.

## Provider and MPQ implementation

Client and archive work is isolated behind `IWoWClientAssetProvider`, `IWoWArchiveReader`, and
`IWoWClientBuildReader`; the existing resolver and decoders do not know about MPQs. The provider
uses two pure-managed, read-only paths:

- `Nmpq.Standard` reads the official WotLK version-1 archives used by the acceptance client.
- `War3Net.IO.Mpq` is a fallback when Nmpq rejects a classic version-0 archive, including local
  custom patch archives. It is not used as an editor and FrameForge exposes no MPQ write operation.

Real-client probes showed Nmpq reading the three required official entries byte-identically, while
War3Net alone found those encrypted/sectorized entries but could not decode them. Conversely,
War3Net successfully reads the client's classic-header custom patch archive that Nmpq rejects.
The composite adapter is therefore smaller and easier to distribute than native StormLib while
covering both archive forms actually present.

Native StormLib was also evaluated: it is mature and works in mod-content-manager, but would add
platform-specific binaries, native loading/error handling, and RID-specific license/package work.
Reusing mod-content-manager would copy that native integration rather than provide a reusable .NET
component. `smpq`, MPQEditor, and old native-wrapper packages were rejected as production
dependencies. FrameForge's managed approach packages the same way on Linux x64 and Windows x64.

See `THIRD_PARTY_NOTICES.md` for licenses and upstream attribution. Nmpq.Standard is Apache-2.0;
War3Net and SharpZipLib are MIT. No third-party source was copied into FrameForge.

## Local storage and precedence

The selected client path is stored in the platform application-settings directory:

- Linux: `$XDG_CONFIG_HOME/FrameForge/settings.json`, normally
  `~/.config/FrameForge/settings.json`;
- Windows: `%APPDATA%\FrameForge\settings.json`.

The managed cache uses the platform local-application-data directory:

- Linux: `$XDG_DATA_HOME/FrameForge/assets/wow-3.3.5a-12340`, normally
  `~/.local/share/FrameForge/assets/wow-3.3.5a-12340`;
- Windows: `%LOCALAPPDATA%\FrameForge\assets\wow-3.3.5a-12340`.

These are local state, not portable project data. Texture resolution remains:

1. source-relative project/content hierarchy;
2. explicit manual asset roots, in visible configured order;
3. FrameForge-managed stock cache.

Manual roots therefore remain useful for development, overrides, alternate extracted trees, and
troubleshooting without letting the managed cache shadow project-owned art.

## Materialization, provenance, and safety

Only safe archive-relative paths below `Interface` are accepted. Rooted paths, drive syntax,
traversal (`.`/`..`), empty segments, malformed/doubled separators, and cache escapes are rejected.
Archive lookup is case-insensitive and extensionless references try `.tga`, `.blp`, then `.png`.

For each request, FrameForge locates the effective archive entry, requires nonempty bytes, writes a
temporary file, flushes it, and atomically moves it into the owned cache while rejecting symlink
escapes. The MPQ is opened for reading only. A machine-local provenance manifest records the
requested reference, client/build, locale, source MPQ, size, SHA-256, time, and cache path. The
inspector shows available provenance. Cache hits avoid reopening/extracting the entry.

**Clear Stock Cache** refuses a symlink cache root and removes only the build-specific directory
owned by FrameForge. It never deletes the selected client, an MPQ, manual root, or project file.

## Advanced manual fallback

Manual roots remain supported if automatic reading fails for an unusual installation. With `smpq`
installed separately, a developer can extract a specific file into a private directory and add
that directory as an asset root, for example:

```bash
mkdir -p /tmp/frameforge-assets
cd /tmp/frameforge-assets
smpq -x /path/to/WoW/Data/enUS/patch-enUS-2.MPQ \
  'Interface/LFGFrame/UI-LFG-FRAME.blp'
```

Command syntax varies by `smpq` version. This is troubleshooting only: FrameForge never invokes or
packages `smpq`, and ordinary users do not need it.
