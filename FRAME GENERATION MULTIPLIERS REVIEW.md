# ARC Capture 0.4.1 — optional 3x and 4x frame generation

Output → Frame Generation now offers Off, Adaptive 2×, Adaptive 3× and Adaptive 4×. Existing Off/2× preferences keep their numeric values; the default remains Off. Each mode inserts the selected factor minus one intermediate frames per captured interval, subject to the existing motion-confidence and timing fallbacks.

For 60 FPS input, 2× requires 120 Hz, 3× requires 180 Hz and 4× requires 240 Hz. For 30 FPS input, these targets are 60, 90 and 120 FPS. The app reads the screen's current refresh rate and retains source-rate playback with an explanatory status if the chosen target cannot be shown. No display settings are changed.

### 🔬 1. Virtual Graph Discovery

- **Target Coordinates:** `./ArcCapture/MainWindow.xaml` > `PreviewFrameGeneration`; `MainWindow.xaml.cs` > `SelectedFrameGeneration`, `ApplyPreferences`, help text; `Engine/FramePresentation.cs` > `FrameGenerationMode`, `FrameGenerationPolicy`; `UI/Direct3DHost.cs` > `Run`, `Prepare`, `PlanIntermediate`, `AdvanceIntermediate`; `Engine/D3DPreview.Interpolation.cs` > `FrameCompute`, `RenderIntermediate`.
- **Call Chain Trajectory — verified:** the XAML SelectionChanged handler updates both preview hosts and CurrentPreferences. NewPreview also applies the selected mode when a window is created. The render thread maps the mode to a factor, checks source timing/size/current refresh, retains two RGB source images, and schedules fractional presentations before the next real-frame boundary. Each source pair is analyzed once; RenderIntermediate passes `step / multiplier` through the existing 32-byte FlowParameters constant buffer to the embedded Synthesize shader. The GPU output surface and motion fields are reused for all intermediate fractions. RenderSource continues through the existing Standard/NIS preview path. Real recording/audio input consumers do not use generated preview pixels. Presentations retain the existing PresentedFrame event contract.
- **Data Contracts Involved — verified:** `FrameGenerationMode` has stable values Off=0, Adaptive2x=1, Adaptive3x=2, Adaptive4x=3. Preferences.Interpolation remains an optional integer defaulting to zero; invalid values restore Off. PresentedFrame contains Time, Duration and Kind. Generated timestamps use the selected fraction of the source timestamps; repeated fallbacks keep their real source timestamp. GPU constants retain their size/layout and now receive a variable Alpha. No shader binary, native ABI, package dependency, authentication, network or persistence destination was added.
- **Tests — verified:** `Engine/FrameGenerationDiagnostics.cs` > RunGpu checks known fractional images, distinct outputs, timestamps, static HUD, cut fallback, unchanged surface allocation, refresh/size policy and deadlines. RunUi and RunMultiplierUi check old/invalid/saved settings, real preview presentations, live factor changes, native fallback, fullscreen changes, recording and simulated input-failure/disconnect. `Engine/Diagnostics.cs` invokes the same checks alongside existing rendering, NIS, recording, audio, readiness, lifecycle and scheduler checks. Discovery used inspected source and bounded textual references; an exhaustive compiler call graph and external consumers were not verified.

### 🛠️ 2. High-Efficiency Implementation

```diff
- internal enum FrameGenerationMode { Off, Adaptive2x }
+ internal enum FrameGenerationMode { Off, Adaptive2x, Adaptive3x, Adaptive4x }

- midpointDue = lastRealTick + period / 2;
+ intermediateDue = FrameGenerationPolicy.PhaseTime(
+     lastRealTick, period, intermediateStep, multiplier);

- Publish(gpu.RenderMidpoint(Sharpness, Upscaler));
+ Publish(gpu.RenderIntermediate(intermediateStep, multiplier, Sharpness, Upscaler));
+ AdvanceIntermediate();

- float Alpha = .5f;
+ // Synthesis receives the requested fractional position through the existing buffer.
+ float alpha = (float)step / multiplier;
```

### 🛡️ 3. Verification & Validation Report

- **Release publish PASS:** standalone Windows x64 version 0.4.1 was published successfully. The first Release compilation reported zero warnings and errors; publish completed without reported warnings/errors. Normal builds reuse the embedded shaders; no SDK shader recompilation was required.
- **Targeted diagnostics PASS:** `artifacts/framegen-multipliers-check/results.json`. The known 3× images at 1/3 and 2/3, and 4× images at 1/4, 1/2 and 3/4, had zero mean absolute error within the controlled camera-motion comparison region. Each fraction produced distinct pixels. Static HUD pixels, scene-cut fallback, metadata and GPU surface reuse passed. These controlled patterns do not guarantee quality on arbitrary games.
- **Final standalone full suite PASS:** `artifacts/framegen-multipliers-full-check/results.json`. Live 20→60 (3×) and 15→60 (4×) tests exercised genuine motion-interpolated presentations on this screen. Each new factor completed two fullscreen round trips plus live Off/2×/selected-factor changes and simulated input-failure/disconnect cleanup. A conservative diagnostic 60 Hz cap tested rejection of 20→80 and native playback fallback; it did not change the physical display mode.
- **Recording PASS:** the short 3× and 4× tests retained their capture sample rates rather than encoding the generated preview rate. The existing 2× recording check and full recording/audio suite also passed.
- **Blast-Radius Check:** the standalone full suite also passed Standard/NIS rendering, source identity, static/cut fallback, resource limits, previous 2× preview pacing, original H.264/AAC and NV12 recording checks, silent WASAPI playback, preview readiness races, capture/preview/audio stop cycles, late-callback/reconnect/finalization and scheduler ownership checks. End-of-suite pooled buffers, Media Foundation workers and MMCSS registrations were all zero. The initial full suite already included the final production code; no subsequent production-code changes were made before delivery.
- **Resource behavior:** higher factors add synthesis and presentation work, and NIS scaling work when enabled. They do not add source-history or output image surfaces: the measured 1080p surface total remained 25,373,440 bytes (about 24.2 MiB), excluding driver overhead. Late intermediate deadlines are skipped rather than accumulated. Confidence, static/cut fallback, input-size limits and the existing analysis-budget cooldown remain active.
- **Limits / unrun checks:** physical 180/240 Hz scanout, live Switch/capture-card image quality, unplug behavior on actual hardware, multi-hour sessions, other GPUs and controller-to-screen/audio synchronization were not measured. High-rate policy and fractional scheduling were checked; real 3×/4× presentation used 20/15 FPS synthetic sources at approximately 60 FPS output. Buffering remains roughly one captured frame plus processing, and interpolation artifacts remain possible. No claim of universal superiority to DLSS or guaranteed zero latency/artifacts is made.
- **Recovery:** pre-change sources are preserved under `artifacts/framegen-multipliers-before-20261103-191047`. The previous Desktop executable is kept beside the new one as a timestamped backup. Existing older backup executables and notices are preserved.
- **Token Efficiency Metric:** context-line accounting was not measured. No token savings or exhaustive graph guarantee is claimed.

Measured final standalone results:

```json
{
  "result": "PASS",
  "input3xFps": 19.99378793009012,
  "output3xFps": 59.981363790270365,
  "generated3xFrames": 120,
  "input4xFps": 14.99815522690709,
  "output4xFps": 59.99262090762836,
  "generated4xFrames": 135,
  "sourceRateRecording3xSamples": 22,
  "sourceRateRecording4xSamples": 16,
  "higherFactorRefreshFallback": "pass",
  "surfaceBytesAt1080p": 25373440
}
```
