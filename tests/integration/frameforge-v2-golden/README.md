# FrameForge v2 golden client-test package

This directory is an isolated Content Manager schema-3 test package. It is not
Native Hunts content and it does not use the legacy `frameForgeWowUi` importer.

Build the deterministic EPF from the repository root:

```bash
python3 tests/integration/frameforge-v2-golden/build_epf.py \
  --output /tmp/frameforge-v2-golden-client-test.epf
```

The builder fails unless the packaged `FrameForgeGoldenDesign.xml` is
byte-identical to `examples/frameforge-v2-golden.Design.xml`.

In an authorized disposable Content Manager environment, copy the EPF into a
configured discovery directory and run:

```text
.content scan
.content stage frameforge-v2-golden-client-test
```

`stage` creates a development test MPQ without installing the package or
creating, activating, or publishing a realm build. Do not run `.content build`
or `.content activate` for this fixture.
