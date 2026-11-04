# ARC Capture 0.6.0 — 30 FPS game mode

Select Output → Frame Generation → Adaptive 2×, then enable **30 FPS game mode**. This targets 60 FPS motion from a 30 FPS game, including games carried as repeated images in a 60 FPS HDMI stream. The same checkbox works with 3× (90 FPS) and 4× (120 FPS), provided the current screen supports that target. The setting is saved and defaults to off. Leave it off for ordinary 60 FPS games. Frame Generation Off remains Off even if the checkbox is saved on.

The reported capture FPS can remain 60 because the card still sends 60 packets. The new mode uses distinct game images for interpolation, so it can produce 30 real + 30 generated images per second rather than displaying the original A,A,B,B sequence. It is a GPU video-motion interpolation mode. It does not use NVIDIA DLSS/Frame Generation or NvFRUC SDK binaries and does not have access to the game's depth/motion buffers. Normal/NIS and optional RTX Video upscaling remain independent.

### 🔬 1. Virtual Graph Discovery

- **Target Coordinates:** `./ArcCapture/MainWindow.xaml` > Capped30GameToggle; `MainWindow.xaml.cs` > SelectedCapped30Game, Capped30GameChanged, NewPreview, Preferences/ApplyPreferences/CurrentPreferences; `UI/Direct3DHost.cs` > Capped30Game, Run/Prepare and content-30 pacing branch; `Engine/FramePresentation.cs` > Content30Period, GameCadenceCorrection, PhaseTime; `Engine/D3DPreview.Interpolation.cs` > PrepareInterpolation, FrameStatistics/ReadFrameStatistics, FrameCompute; `Shaders/FrameInterpolation.hlsl` > Motion and Flags bit zero. Diagnostics: `Engine/CappedGameDiagnostics.cs`, `FrameGenerationTestSource.cs`, `MainWindow.Diagnostics.cs`, `App.xaml.cs`, `Engine/Diagnostics.cs`.
- **Call Chain Trajectory — verified:** XAML Checked/Unchecked → Capped30GameChanged → both preview hosts plus saved preference. NewPreview copies the flag to new/recreated hosts. Direct3DHost takes the existing latest-packet lease on its renderer thread. With the option enabled and a generation factor selected, Content30Period maps common capture clocks to approximately 30/29.97 FPS game timing and applies the existing screen/input-size checks. PrepareInterpolation compares downsampled GPU images before committing history. Near-duplicates leave the last distinct image/timestamp intact and skip motion analysis. A distinct image forms a game-frame pair; the renderer schedules factor-minus-one intermediate images and the real image at even content-clock fractions. Pending pair phases retire before their scratch history slot is reused.
- **Call Chain Trajectory — verified motion/pacing:** the optional wider motion search retains all original small-motion candidates and adds larger offsets. It adds one refinement level and allows a lower global trusted-tile ratio in game mode; per-pixel forward/backward, image-agreement, bounds, static-edge/HUD and cut protections remain. The original mode still uses the original range, four refinements and confidence threshold. Content pacing uses the existing interruptible high-resolution waitable timer and DXGI readiness credit. A bounded, filtered correction follows distinct-frame arrivals to avoid nominal-clock rounding drift. Overdue synthetic work is dropped/resynchronized; stopped input releases interpolation history and waits instead of running an endless repeat loop. Mode/fullscreen/stop changes reset history on the owner thread.
- **Data Contracts Involved — verified:** FrameGenerationMode values Off/2×/3×/4× remain unchanged. Preferences adds optional `bool Capped30Game = false`. Packet contents, timestamps and Duration are not mutated by production filtering; the game duration is held in GPU presentation metadata. PresentedFrame retains Time/Duration/Kind. The 32-byte FlowParameters ABI is unchanged; Flags bit zero enables wider motion only for the new option. The source recorder/snapshot/audio consumers keep captured data and original processing. No SDK runtime, model, external endpoint, upload or global video/display/security setting was added. Inspected event call sites and COM/timer boundaries were verified; a compiler-generated exhaustive call graph and unrelated external consumers were not checked.
- **Tests — verified:** CappedGameDiagnostics.RunGpu covers A,A,B,B history/timestamps, 2×/3×/4× known fractional pixels, larger motion, static HUD, cuts, reset/memory, 29.97/30 clock ratios, a two-hour rounding simulation and refresh policy. RunUi uses a 60 FPS source with 30 unique images and measures actual generated presentations and interval distributions; it checks preferences, the conservative 60 Hz case, real-screen 3×/4× targets, static output, stalled-input idle/resume, fullscreen/mode changes, source-rate recording, RTX driver-path coexistence and simulated input-failure/disconnect. CLI routes are --capped-game-tests and --capped-game-soak. Existing full-suite tests remain active.

### 🛠️ 2. High-Efficiency Implementation

```diff
- Preferences(..., int Interpolation = 0)
+ Preferences(..., int Interpolation = 0, bool Capped30Game = false)

- gpu.PrepareInterpolation(packet)
+ gpu.PrepareInterpolation(packet, content30 ? period : 0, content30)

+ // Duplicate comparison preserves the current distinct image and timestamp.
+ if (nearDuplicate) { FrameWasDuplicate = true; return true; }

+ // The new branch paces from content timing and preserves pending pair history.
+ Publish(gpu.RenderIntermediate(cadenceStep, multiplier, Sharpness, Upscaler));
+ cadenceDue = FrameGenerationPolicy.PhaseTime(cadenceEpoch, period, cadencePhase, multiplier);
```

### 🛡️ 3. Verification & Validation Report

- **Confirmed cause:** the previous path used capture packet Duration and rotated history for duplicates. A 60 FPS card can carry only 30 distinct game images; doubling the transport rate does not fix repeated-image cadence and also requires a 120 Hz display. The new option processes distinct images using 30 FPS content timing and can target smooth 60 FPS presentations on a 60 Hz screen.
- **Known-image PASS:** final 2× midpoint, 3× third-frames, 4× quarter-frames, and the larger-motion midpoint all had zero mean absolute error inside the controlled comparison region. Static HUD pixels and cut fallback passed. This is controlled-pattern evidence, not an artifact-free guarantee for arbitrary games.
- **Full standalone regression PASS:** `artifacts/capped-game-package-check/results.json`. It included the new mode along with Standard/NIS/RTX driver upscaling, existing 2×/3×/4× generation, source/snapshot/recording/color paths, H.264/AAC and NV12 encoding, silent WASAPI playback, readiness races, capture/audio/preview stop cycles, late callbacks, reconnect, finalization and scheduling ownership. Pooled buffers, Media Foundation workers and MMCSS registrations were zero at shutdown. The later bounded phase-correction change was checked by the final standalone GPU/clock/UI soak.
- **Final standalone soak PASS:** `artifacts/capped-game-final-soak/results.json`. Twenty fullscreen round trips plus live toggles completed. Static input kept paced output without invented motion; missing input stopped GPU presentations and resumed correctly. Optional RTX driver processing coexisted with game cadence. A short source recording wrote 66 captured samples over roughly 1.1 seconds; the mode did not reduce recording to its 30 FPS game cadence. Simulated input failure and repeated disconnect completed.
- **Long-clock simulation PASS:** 216,000 game frames modeled two hours of nominal-rate rounding; final phase residual was 0.003 ms. This was a deterministic clock simulation, not a two-hour hardware soak. Corrections are bounded to 0.2 ms per distinct-frame update and do not pull large timestamp jumps into the phase lock.
- **Legacy pixels PASS:** `artifacts/capped-game-compatibility/results.json` versus `artifacts/rtx-video-compatibility/results.json`: all four RGB/NV12 1080p/4K workloads and 38 setting/layout/size transition hashes were identical. Downsample, Compare and Synthesize bytecode stayed identical; Motion gained the optional wider-search branch. Existing mode image/cadence tests passed in the full suite.
- **Performance / resources:** two source RGB histories, two small analysis images, motion fields and one synthesis surface are reused. No additional full-frame history surface is added; 1080p image surfaces remained 25,373,440 bytes (about 24.2 MiB), excluding driver overhead. Warm 1080p preparation/analysis averaged about 0.76 ms in the full-suite run; that is not total rendering, driver-upscaling or end-to-end latency. Duplicate checks still have upload/downsample/summary cost, but skip the wider motion search. Normal frame queue depth, recording flow and multimedia scheduling settings remain unchanged.
- **Visual QA:** the checkbox, saved selection, 30→60 status, Normal filter and recording controls were inspected in `artifacts/capped-game-package-soak/interface.png`; the final soak also produced `interface.png`. Native video is absent from WPF RenderTargetBitmap captures, so GPU readbacks separately verify generated images.
- **Blast-Radius Check / limits:** the new mode is an explicit 30 FPS content assumption, not automatic engine-FPS detection. Near-duplicate thresholds can miss very subtle changes or be affected by compression noise. Motion interpolation can distort objects/occlusions and is not NVIDIA DLSS. It needs lookahead buffering plus GPU processing and does not improve controller responsiveness. The current display reported 240 Hz; the 60 Hz case also used a conservative diagnostic refresh cap, and measured 90/120 FPS cases used the actual display mode. Presentation timestamps/counts do not measure physical scanout or end-to-end latency. Real Switch/capture-card quality, physical unplug, other GPUs/drivers, long hardware sessions, variable-rate games and audio alignment with added video delay were not measured. RTX Video neural activation remains unconfirmed as documented in RTX VIDEO REVIEW.md; this work does not change that driver setting.
- **Recovery:** original sources and shader objects are preserved in `artifacts/capped-game-before-20261103-194555`. The final tested executable is `artifacts/capped-game-final-standalone/ArcCapture.exe`; Desktop delivery preserves the previous 0.5.0 executable as a timestamped backup and verifies the installed SHA-256.
- **Token Efficiency Metric:** context-line accounting was not measured. No token savings or exhaustive graph guarantee is claimed.

NVIDIA's [DLSS Frame Generation integration guide](https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideDLSS_G.md) describes its required game-engine buffers. NVIDIA also documents a separate [video-only frame-rate conversion approach](https://docs.nvidia.com/video-technologies/optical-flow-sdk/nvfruc-programming-guide/index.html); ARC's new mode uses its own GPU motion/pacing implementation and does not bundle that library.

Final measured results:

```json
{
  "result": "PASS",
  "displayHz": 240,
  "game2x": {
    "captureFps": 59.993571688793544,
    "distinctGameFps": 29.996785844396772,
    "outputFps": 59.993571688793544,
    "generatedFps": 29.996785844396772,
    "presentationGapP95Ms": 17.1259,
    "skippedCaptureDuplicates": 129
  },
  "game3x": {
    "captureFps": 59.994888435505295,
    "distinctGameFps": 29.997444217752648,
    "outputFps": 89.99233265325795,
    "generatedFps": 59.994888435505295,
    "presentationGapP95Ms": 13.4634,
    "skippedCaptureDuplicates": 197
  },
  "game4x": {
    "captureFps": 59.99270788635641,
    "distinctGameFps": 29.996353943178207,
    "outputFps": 119.98541577271283,
    "generatedFps": 89.98906182953462,
    "presentationGapP95Ms": 9.956,
    "skippedCaptureDuplicates": 266
  },
  "fullscreenTrips": 20,
  "fullscreenMinimumFps": 59.83625211246902,
  "fullscreenAverageFps": 60.59270288267212,
  "sourceRecordingSamples": 66,
  "clockSimulationResidualMs": 0.003
}
```
