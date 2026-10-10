# FrameForge Phase 0 Baseline Verification

Verification date: 2026-10-10 (America/New_York)

## Status summary

**Baseline captured; Phase 0 exit criteria are not met.** The Release build succeeds and the focused V2 exporter and EPF fixture checks pass. The ordinary automated suite has one repeatable failure, and the module-hosted V2 export has not been loaded by a real WoW 3.3.5a client connected to an AzerothCore test realm. No claim of real-client compatibility is made by this report.

| Area | Result | Evidence |
|---|---|---|
| Release build | Pass | Four projects built; 0 warnings, 0 errors |
| Ordinary automated suite | Fail | 660 passed, 1 failed, 0 skipped, 661 total |
| Focused V2 golden/export tests | Pass | 16 passed, 0 failed |
| Deterministic EPF assembly | Pass | Rebuilt EPF is byte-identical to the checked-in EPF |
| Installed-client asset/template probe | Mixed | Build-12340 template registry passed; Native Hunts fixture-count assertion failed |
| Content Manager staging | Not run | No configured, running disposable Content Manager/AzerothCore environment was established |
| In-game V2 acceptance | Not run | No worldserver executable or running worldserver was found; no client session was launched |

## Repository revision and branch

- Repository: `/home/smithkt/git/FrameForge`
- Branch: `feature/frameforge-v2`
- Revision: `a8f4e392b8297bbd67ef4cdbc375a1c151847def`
- Commit subject: `V2 Takeover and Usability Completion`
- Commit author/committer date: `2026-10-09T09:16:21-04:00`
- Verification SDK: .NET SDK `10.0.112`; runtime `10.0.12`; Ubuntu 26.04 x64

The working tree was not clean before verification. It contained these pre-existing untracked documents:

```text
?? docs/FRAMEFORGE_DIRECTION.md
?? docs/FRAMEFORGE_ROADMAP.md
?? docs/FRAMEFORGE_TEST_STRATEGY.md
```

They were treated as user-owned inputs and were not modified. This report is the only repository file added by the Phase 0 work. No commit or push was performed.

## Build results

Command:

```bash
NUGET_HTTP_CACHE_PATH=/tmp/frameforge-phase0-nuget-http-cache \
  dotnet build FrameForge.slnx -c Release -m:1 --disable-build-servers
```

Result: **Pass**.

```text
FrameForge.Core          -> bin/Release/net10.0/FrameForge.Core.dll
FrameForge.Desktop       -> bin/Release/net10.0/FrameForge.dll
FrameForge.Core.Tests    -> bin/Release/net10.0/FrameForge.Core.Tests.dll
FrameForge.Desktop.Tests -> bin/Release/net10.0/FrameForge.Desktop.Tests.dll
Build succeeded. 0 Warning(s), 0 Error(s).
```

Two earlier attempts failed before compilation for environment reasons. With network denied, NuGet vulnerability data could not be fetched (`NU1900`). After network was enabled, NuGet reached the feed but could not write its default HTTP cache beneath `/home/smithkt/.local/share/NuGet` because that location is read-only. Redirecting only `NUGET_HTTP_CACHE_PATH` to `/tmp` produced the successful build above. These two attempts do not identify an application compile failure.

## Automated test results

Command:

```bash
NUGET_HTTP_CACHE_PATH=/tmp/frameforge-phase0-nuget-http-cache \
  dotnet test FrameForge.slnx -c Release --no-build --no-restore \
  -m:1 --disable-build-servers
```

Results:

| Assembly | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| `FrameForge.Core.Tests` | 432 | 0 | 0 | 432 |
| `FrameForge.Desktop.Tests` | 228 | 1 | 0 | 229 |
| **Total** | **660** | **1** | **0** | **661** |

The failure repeats when run alone:

```text
FrameForge.Desktop.Tests.StockTemplateResolverTests.
Known_stock_font_uses_diagnosed_fallback_metrics_without_client_definitions

Expected effective height: 32
Actual effective height:   100
Location: tests/FrameForge.Desktop.Tests/StockTemplateResolverTests.cs:176
```

The immediate mismatch is observable in the test setup: the constructed `FrameDef` leaves `Width` and `Height` at their model defaults of `100`, while `StockTemplateResolver.ApplyEffectiveGeometry` auto-sizes font strings only when width or height is non-positive. This report does not decide whether the contract, implementation, or test expectation should change; that requires defect triage and a separately authorized fix.

The repository's GUI smoke test was not run because `xvfb-run` is not installed in this environment. This is recorded as an unexecuted supplemental check, not as the cause of the automated-suite failure above.

## Existing V2 export verification

The smallest checked-in module-hosted V2 sample is:

- Project: `examples/frameforge-v2-golden.fforge.json`
- Golden output: `examples/frameforge-v2-golden.Design.xml`
- Target: WoW 3.3.5a build 12340
- Composition root: `GoldenDesignRoot`
- External module host: `GoldenModuleHost`
- Authored nodes: four (`GoldenBackground`, `GoldenHeader`, `GoldenButton`, and `GoldenProgress`)

Focused command:

```bash
dotnet test tests/FrameForge.Core.Tests/FrameForge.Core.Tests.csproj \
  -c Release --no-build --no-restore --disable-build-servers \
  --filter "FullyQualifiedName~FrameForge.Core.Tests.V2FrameXmlExportTests|FullyQualifiedName=FrameForge.Core.Tests.V2TemplateIntegrationTests.Existing_golden_project_and_untemplated_export_remain_compatible"
```

Result: **Pass — 16 passed, 0 failed, 0 skipped.** This verifies the current exporter against the checked-in project and golden XML, including native ownership/layers, typed anchors, deterministic output, validation/fail-closed behavior, manifest/export behavior, and compatibility of the untemplated golden sample.

Artifact hashes:

```text
f6196677502cc0baacefe21f1f6a3fcae3fee9ba7e42fe45e948f76c83522d5b  examples/frameforge-v2-golden.fforge.json
afbd2eb10f27ef598b63b867afc786b580ee7e18ca74914a99cb3a515e989c6a  examples/frameforge-v2-golden.Design.xml
afbd2eb10f27ef598b63b867afc786b580ee7e18ca74914a99cb3a515e989c6a  tests/integration/frameforge-v2-golden/client/Interface/FrameXML/FrameForgeGoldenDesign.xml
```

The matching XML hashes establish byte identity between the exporter golden and the packaged design XML. They do **not** establish that build 12340 accepts or renders the XML.

## EPF integration findings

Fixture: `tests/integration/frameforge-v2-golden`

- The fixture is an isolated Content Manager schema-3 package named `frameforge-v2-golden-client-test`.
- It does not use the legacy `frameForgeWowUi` importer and contains no Native Hunts content.
- `FrameForgeGoldenHost.xml` creates the 800x600 `GoldenModuleHost` frame under `UIParent`.
- The V2 design XML loads after the host; the host loads after stock `Interface/FrameXML/LFDFrame.xml`.
- The fixture pins the stock 2,820-byte build-12340 `FrameXML.toc` SHA-256 to `3158bea13225ae51137a389f0f3ab8566e94b6be84196dd2c1fda27024677754`.
- The builder rejects a schema other than 3, rejects the legacy importer key, rejects duplicate archive members, and refuses to package a design XML that differs from the checked-in golden.

Rebuild command:

```bash
python3 tests/integration/frameforge-v2-golden/build_epf.py \
  --output /tmp/frameforge-phase0-v2-golden.epf
```

Result: **Pass**. The output contains exactly four files (`manifest.json`, host XML, design XML, and the pinned upstream TOC). The rebuilt and checked-in EPFs are byte-identical:

```text
9a998d8dd8fc8bb3d0464e2773d6a9713d8edcd1d2806220cd6444a033412dac  rebuilt EPF
9a998d8dd8fc8bb3d0464e2773d6a9713d8edcd1d2806220cd6444a033412dac  checked-in EPF
```

Content Manager staging was not run. A local `mod-content-manager` checkout exists at revision `eb911715599063123cdd061832177ba0ff729c63`, but no configured/running disposable server environment was established. Per the fixture instructions, only `.content scan` and `.content stage frameforge-v2-golden-client-test` are appropriate for this isolated test; `.content build` and `.content activate` must not be used for it.

## Installed-client probe

A client exists at `/home/smithkt/WoW-335a/Wow.exe`. The repository's opt-in checks were run read-only against it, with extracted cache data placed under `/tmp`.

- `Build12340TemplateRegistryIntegrationTests.Explicit_installed_client_verifies_approved_template_registry_when_requested`: **Pass**. This verifies client build 12340, the approved template/font source hashes, template resolution, required dependencies, and selected renderable state textures.
- `WoWClientAssetProviderTests.Explicit_real_client_acceptance_when_requested`: **Fail** at its final Native Hunts direct-texture-count assertion (`expected 19`, `actual 31`). The assertions before that point passed, including client validation as `enUS`, stock materialization, selected stock hashes, and template/font geometry.

The failing Native Hunts probe read `/home/smithkt/git/mod-native-hunts/content/client/Interface/FrameXML/NativeHuntsFrame.xml`. That external checkout was already dirty (`NativeHuntsFrame.xml` and `mod-native-hunts.epf` modified) at revision `1b29b61b275ba557118e3efb4d6c9987185b8f01`. The count mismatch therefore records drift between the opt-in assertion and the current external fixture; it is not evidence about the V2 golden export's in-game behavior.

## Known failures and limitations

1. One ordinary desktop test fails repeatably with effective font-string height `100` instead of `32`.
2. The opt-in Native Hunts installed-client check expects 19 direct textures but the currently modified external fixture contains 31.
3. The V2 golden XML has not been parsed or rendered by an actual WoW process.
4. The V2 EPF has not been discovered or staged by a running Content Manager instance, and no generated test MPQ/parity artifact was inspected.
5. A local AzerothCore source checkout exists at revision `7f12e89ee5f467a50e62eba1d525eac7dc953d03`, but no `worldserver` executable or running worldserver was found in the inspected locations. Realm/module/configuration readiness is therefore unverified.
6. No protected FrameXML-capable client deployment, Portalkeeper requirement selection, server login, UI error capture, screenshot, or fresh-session repeat was performed.
7. Automated XML comparison, exporter unit tests, installed-client archive reads, and deterministic EPF assembly are not substitutes for real-client acceptance.

## Real-client verification checklist

Perform the following in an authorized, disposable WoW/AzerothCore test environment. Preserve exact paths, revisions, hashes, logs, and screenshots in the acceptance record.

### 1. Freeze and record inputs

- Record the FrameForge branch and revision from this report and confirm the working-tree state.
- Record the AzerothCore, Content Manager, module/host, and Portalkeeper revisions.
- Record the WoW executable identity, client version/build `3.3.5a (12340)`, and locale.
- Record the project, golden design XML, and EPF SHA-256 values above.
- Confirm the realm/client supports the Content Manager `protected-framexml` requirement and uses the required generation-2 executable. Do not use a production realm or production client installation.

### 2. Reproduce and stage the package

- Rebuild the EPF with `build_epf.py` and require hash `9a998d8dd8fc8bb3d0464e2773d6a9713d8edcd1d2806220cd6444a033412dac`.
- Copy that EPF into a configured Content Manager discovery directory.
- In the disposable realm, run `.content scan` and confirm exactly one valid package named `frameforge-v2-golden-client-test` version `1.0.0` is discovered.
- Run `.content stage frameforge-v2-golden-client-test` only. Confirm staging succeeds without installing the package or creating/activating a realm build.
- Record the generated `<OutputDirectory>/frameforge-v2-golden-client-test-test.mpq` path and SHA-256, staging log, file count, composed `FrameXML.toc`, and parity/ownership evidence.
- Confirm the composed load order is stock `LFDFrame.xml`, then `FrameForgeGoldenHost.xml`, then `FrameForgeGoldenDesign.xml`.
- Make the staged MPQ available to the disposable client through that environment's normal test-deployment procedure; record the exact deployed filename, location, and hash. Do not overwrite or activate production content.

### 3. Launch and observe

- Start the matching disposable AzerothCore realm with the required modules/configuration, then launch the protected build-12340 client and connect to that realm.
- Enable/capture client FrameXML and Lua errors before login. Preserve the client log and server/Content Manager log.
- Log in with a test character and confirm there are no XML parse errors, duplicate-name errors, missing-parent errors, protected-content errors, or load-order errors referring to either golden XML file.
- Confirm `GoldenModuleHost` exists and the exported `GoldenDesignRoot` is parented to it and fills it.
- Confirm the host is centered at 800x600 and visibly contains:
  - a dark background filling the root;
  - `GoldenHeader`, text `FrameForge Export`, 300x24, 24 pixels below the top;
  - `GoldenButton`, 160x32, centered 16 pixels below the header;
  - `GoldenProgress`, 240x18, centered 110 pixels below the top, with range 0..100 and value 35.
- Use the client's frame inspection facility, if available, to record the runtime names and parent chain. Capture a screenshot at a known resolution/UI scale.
- Run `/reloadui` and repeat the observations. Then exit and repeat from a fresh client launch to exclude state carried by the first session.

### 4. Record the verdict

Create an acceptance record containing date/time, tester, every revision, WoW executable/build/locale, module host identity, load order, all source/EPF/MPQ hashes, exact steps, expected and observed results, screenshots, client/server logs, and a Pass/Fail/Blocked verdict. Any visual or runtime discrepancy is a failure to triage at the earliest authoritative layer; do not edit the golden merely to match an unexplained client result.

## Requirements before Phase 1 begins

Phase 1 should not begin until all of the following are true:

1. Triage and resolve the repeatable `StockTemplateResolverTests` failure, then rerun the complete Release suite with 0 failures.
2. Reconcile the opt-in Native Hunts texture-count expectation with a clean, pinned external fixture, or explicitly replace that evidence with an approved current acceptance fixture.
3. Stage the deterministic V2 golden EPF successfully in an authorized disposable Content Manager environment and retain the staging/MPQ/parity evidence.
4. Complete the real-client checklist against WoW build 12340 and the intended AzerothCore/module host, with recorded screenshots, logs, revisions, load order, and hashes.
5. Obtain an explicit in-game Pass verdict for the module-hosted V2 golden. Generated XML, unit tests, deterministic packaging, and read-only archive validation alone are insufficient.

## Follow-up verification — automated test failure resolution

Follow-up date: 2026-10-10 (America/New_York)

This section preserves the original observations above and records the narrowly scoped follow-up performed at the same audited revision, `a8f4e392b8297bbd67ef4cdbc375a1c151847def`, plus the uncommitted test correction described below.

### Investigation findings and root cause

The failing `Known_stock_font_uses_diagnosed_fallback_metrics_without_client_definitions` test had incorrect setup.

- `FrameDef.Width` and `FrameDef.Height` have defaulted to `100` since the initial implementation (`abb494f7faceca33e323b43c0adc50319afe9b23`).
- `StockTemplateResolver.ApplyEffectiveGeometry` has auto-sized FontStrings only when width or height is non-positive since stock template/font rendering was introduced (`46ebe54940f81de0b37d64e409b847fec5decb92`). An explicitly positive authored dimension is preserved.
- The failing fallback-font test was added later in `e7c9c74729ef6df9e2eb9e210d7b46a0d8418492` without specifying dimensions, so it unintentionally exercised preservation of an authored 100x100 size rather than fallback-font auto-sizing.
- The adjacent `Font_auto_size_uses_effective_style_without_mutating_declared_project` test explicitly uses `Width = 0, Height = 0`, confirming the established auto-size contract.

Classification: **incorrect test setup**, not an application defect, changed product contract, or incorrect expectation. The expected height of 32 remains correct for a two-line string using the diagnosed 16-pixel fallback font when height is unspecified.

### Correction applied

Only the test arrangement was changed:

```csharp
Name = "Label", Kind = FrameKind.FONTSTRING, Width = 0, Height = 0,
```

No production implementation, legacy resolver behavior, V2 semantic model, exporter, ownership/anchor rule, runtime naming rule, manifest, or Content Manager integration was changed. The corrected test passes when run alone (1 passed, 0 failed).

### Updated Release build and automated-suite results

Release build command:

```bash
NUGET_HTTP_CACHE_PATH=/tmp/frameforge-phase0-followup-nuget-http-cache \
  dotnet build FrameForge.slnx -c Release -m:1 --disable-build-servers
```

Result: **Pass — 0 warnings, 0 errors.** All four solution projects built successfully.

Complete-suite command:

```bash
NUGET_HTTP_CACHE_PATH=/tmp/frameforge-phase0-followup-nuget-http-cache \
  dotnet test FrameForge.slnx -c Release --no-build --no-restore \
  -m:1 --disable-build-servers
```

| Assembly | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| `FrameForge.Core.Tests` | 432 | 0 | 0 | 432 |
| `FrameForge.Desktop.Tests` | 229 | 0 | 0 | 229 |
| **Total** | **661** | **0** | **0** | **661** |

The ordinary automated baseline is therefore clean. The opt-in installed-client methods return early when their environment variables are absent, so their appearance in the 661 passing test cases is not evidence that their real-client branches executed. Their explicit execution is recorded separately below.

### V2 exporter compatibility recheck

The same focused V2 command documented in the original report was rerun after the correction.

Result: **Pass — 16 passed, 0 failed, 0 skipped.** The V2 golden/export behavior remains unchanged. No V2 files were modified.

### Native Hunts fixture drift investigation

The `19` assertion is historically tied to an older, immutable Native Hunts fixture, but the opt-in test accepts an arbitrary path through `FRAMEFORGE_NATIVE_HUNTS_XML` and was run against an actively developed sibling checkout.

Evidence:

- The fixed `Assert.Equal(19, direct.Length)` was introduced in FrameForge commit `f635700cc3eddcd88e40b1c80c130e6fdb58e267` on 2026-10-03.
- FrameForge's checked-in immutable fixture is byte-identical to Native Hunts commit `74184deb79cdbe9f90967e803070c6b3ab9ad3b1` and contains 19 direct texture references:

  ```text
  42673d3fc3f2046315de8cb1cb630ad018769734cf383e5c5b0bdb9eafda47c0
  ```

- Native Hunts commit `fd308e77a9dcb923ca74c82bc4beb7bc66a0ff2c` on 2026-10-08 legitimately expanded the XML from 19 to 31 direct texture references by retaining the previous functional UI and adding a generated `FrameForge_Dungeon_Finder_UI` visual subtree.
- The external checkout's current `HEAD`, `1b29b61b275ba557118e3efb4d6c9987185b8f01`, also contains 31 direct references. Its committed XML SHA-256 is `b20b7c0eb2c07220c4890da28bac262c8baabbd6d15e3627b0947380ba34534a`.
- The external working copy is modified and has XML SHA-256 `9868a59ac59dff111592e087f46ada22e6f553e46f08efeb39ba9ca5c99080bc`, but its changes adjust geometry and font sizes; it still contains 31 direct references. The 19-to-31 mismatch is therefore not caused by the current uncommitted edits.

Conclusion: the external fixture legitimately evolved, the fixed assertion is outdated for that external branch, and the opt-in test has an unstable external-project dependency. No evidence found indicates a FrameForge asset-provider regression. Changing `19` to `31` would merely pin the test to a different moving target and would weaken the meaning of the established 19-texture regression corpus, so no such change was made. `mod-native-hunts` was not modified.

Recommended fixture correction for a separate scoped task:

1. Keep the existing immutable XML fixture and its SHA-256 assertion.
2. Check in or otherwise immutably pin the corresponding eight custom TGA inputs, with source revision and hashes, so installed-client validation is self-contained.
3. Run the exact historical assertions against that frozen corpus: 21 Texture elements, 19 direct references, 8 custom TGA declarations, 11 stock BLP declarations, and all declarations renderable.
4. Treat an explicitly requested live sibling checkout as a separate compatibility probe. Record its repository revision and XML hash, derive its inventory before asserting resolution, and do not substitute that moving inventory for the frozen regression contract.

This preserves meaningful asset validation instead of relaxing it to accommodate unexplained or unpinned changes.

### Explicit installed-client follow-up

The two relevant opt-in tests were explicitly run against `/home/smithkt/WoW-335a` with a temporary cache:

| Test | Result | Finding |
|---|---|---|
| `Build12340TemplateRegistryIntegrationTests.Explicit_installed_client_verifies_approved_template_registry_when_requested` | Pass | Build 12340 client validation, approved template/font hashes, dependencies, and selected texture decoding remain valid. |
| `WoWClientAssetProviderTests.Explicit_real_client_acceptance_when_requested` | Fail | Reaches the inventory assertion after its preceding client, stock-materialization, hash, template, font, and geometry checks pass; expected 19 direct textures, current external fixture has 31. |

Explicit installed-client summary: **1 passed, 1 failed, 0 skipped, 2 total.** This failure is kept visible and is not reported as a pass.

### Environment limitations

- Initial follow-up test attempts encountered sandbox restrictions on MSBuild stream creation and VSTest's local TCP socket. Single-node execution plus temporary permission for VSTest loopback produced the completed results above; those initial aborted attempts are not counted as test failures or passes.
- NuGet's HTTP cache remained redirected to `/tmp` because the default home cache is read-only in this environment.
- `xvfb-run` remains unavailable, so the separate application GUI smoke was not executed.
- No AzerothCore server was started, built, configured, or modified, as required by this assignment.
- No in-game V2 verification or Content Manager staging was performed.

### Current Phase 0 status

**Automated baseline: clean.** The complete ordinary Release suite passes 661/661, the focused V2 exporter set passes 16/16, and the only code change is a justified test-fixture correction.

**Overall Phase 0 roadmap exit: not yet complete.** The external Native Hunts opt-in probe still requires a frozen asset corpus to become reproducible, and the original roadmap still requires operational EPF staging plus recorded in-game module-hosted V2 evidence. Those limitations do not prevent completion of this automated-test-resolution assignment, but they must not be described as verified or passing before Phase 1 begins.
