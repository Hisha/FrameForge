# WoW visual landmark comparison

FrameForge includes a screenshot-independent normalization step for comparing an editor capture
with a WoW 3.3.5a capture. It removes only uniform scale and translation using stock Blizzard
landmarks, then reports every residual in WoW UI units. Independent X/Y scaling is intentionally
not allowed, because it would conceal aspect-ratio errors.

## Capture procedure

1. Open the unchanged project, select the same design and imported preview states used by the
   client, choose Preview, and capture the canvas at the zooms being checked.
2. Export, install the generated XML and BLP files, open the same LFD tab in build 12340, and take a
   lossless screenshot. Do not resize either image after capture.
3. In each image record the pixel centre of at least three unambiguous stock landmarks. For Native
   Hunts use the LFD frame's top-left and top-right chrome corners plus the left end of the chrome
   seam above the tabs. Express their known `uiX`/`uiY` positions relative to LFDParentFrame's
   top-left, with Y increasing down.
4. Record the same way the outer visible left/top edges of the Hunt Record and Idle artwork, the
   left ink edge and baseline of `Huntmaster's Seals:`, the seal icon centre, and at least one more
   stock landmark. Visible artwork edges are alpha landmarks; frame bounds are geometry landmarks.
   Label them accordingly in the landmark name so transparent padding is not mistaken for motion.
5. Save the observations as the JSON array below and run:

   `FrameForge --compare-wow-landmarks landmarks.json report.json`

```json
[
  { "name": "stock top-left chrome", "uiX": 0, "uiY": 0,
    "previewX": 100, "previewY": 80, "clientX": 90, "clientY": 70, "reference": true },
  { "name": "stock top-right chrome", "uiX": 355, "uiY": 0,
    "previewX": 455, "previewY": 80, "clientX": 445, "clientY": 70, "reference": true },
  { "name": "stock lower seam", "uiX": 0, "uiY": 410,
    "previewX": 100, "previewY": 490, "clientX": 90, "clientY": 480, "reference": true },
  { "name": "record border visible left", "uiX": 32, "uiY": 57,
    "previewX": 132, "previewY": 137, "clientX": 122, "clientY": 127 }
]
```

`previewErrorX/Y` and `clientErrorX/Y` measure each observation against its declared UI position.
`previewMinusClientX/Y` is the normalized FrameForge-to-client discrepancy. Repeat the same file at
two or more editor zooms; the preview residuals should remain unchanged apart from sub-pixel
rasterization. A border bound that agrees while its visible alpha edge differs identifies texture
padding/cropping, while a label-only residual identifies font shaping or rasterization.

The tool does not claim that an XML round trip or a passing unit test is a client comparison. The
client observations must come from a real build-12340 capture after installing the newly exported
assets.
