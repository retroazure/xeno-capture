# ARC Capture 0.4.0 — optional video frame generation

Under **Output → Frame Generation**, choose **Adaptive 2× · Motion checked**. The preview targets twice the capture-stream rate: 30→60 or 60→120. The screen must support that rate; otherwise the status explains why the native path remains active. **Off · Lowest delay** is the default. Preview Upscaling remains independent and can be combined with generation.

Interpolation needs a lookahead image. This mode adds roughly one input frame of video buffering (about 33 ms at 30 FPS or 17 ms at 60 FPS), plus processing/presentation costs. It does not improve console simulation or controller response. Repeated/cut/uncertain content can use a real frame instead. No algorithm here is guaranteed artifact-free or universally more reliable than game-integrated DLSS.

### 🔬 1. Virtual Graph Discovery

- **Target Coordinates:** `./ArcCapture/Engine/D3DPreview.Interpolation.cs` > `PrepareInterpolation`, `FrameCompute`, `RenderCachedFrame`, `RenderMidpoint`, `DisposeInterpolation`; `ArcCapture/Shaders/FrameInterpolation.hlsl` > `Downsample`, `Compare`, `Motion`, `Synthesize`; `UI/Direct3DHost.cs` > `Run`, `FrameGeneration`, `Publish`; `Engine/FramePresentation.cs` > `PresentedFrame`, `FrameGenerationPolicy`, `FrameDeadlineWaiter`, `DisplayTiming`; `MainWindow.xaml` / `MainWindow.xaml.cs` > frame-generation selector and preferences.
- **Call Chain Trajectory (final map):**
  - **Verified:** captured packet -> host's bounded latest-frame lease -> render-thread scheduler. Off preserves the existing source upload/render/present path. Adaptive 2× checks source duration, input size and the current monitor's refresh rate before allocating history or enabling additional presentations.
  - **Verified:** upload -> source-resolution GPU RGB snapshot -> maximum 320×180 color analysis -> bidirectional block matching -> 32-byte GPU summary readback. Search radius, candidate grid, refinement count, tile size and image-surface memory are bounded. There is no learned model or additional runtime package.
  - **Verified:** matching uses parallel workgroups. Global confidence, scene-cut/static checks, forward/backward consistency, per-pixel color agreement, bounds and stable-edge checks restrict synthesis. An uncertain midpoint repeats the preceding real frame; this reduces invented content but does not prove absence of all visual artifacts.
  - **Verified:** cached real frame -> presentation -> prepare next pair -> timer-paced midpoint -> next real-frame boundary. The high-resolution wait is interruptible by source/control/stop signals. Late generated slots can be skipped. Acquired DXGI readiness stays valid until Present. Pausing, mode changes and teardown reset history on its render owner; the HWND remains alive until that owner finishes.
  - **Verified:** generated RGB frames enter the same optional NIS upscaling/sharpening pipeline as real frames. No generated frame is submitted to the recorder or audio chain. Source callbacks still publish original packets.
  - **Verified:** presentation observers now receive immutable `PresentedFrame(Time, Duration, Kind)` metadata rather than borrowed pixel buffers. The inspected consumers use counts/timing; generated and repeated presentations have distinct kinds. No extra CPU capture-packet history is retained.
  - **Verified tests:** `FrameGenerationDiagnostics.RunGpu` / `RunUi`; controlled translating images and HUD, static/source identity, scene-cut fallback, policy/resource limits, live 30→60 pacing, preferences, fullscreen toggles, recording and input-failure cleanup. Existing `Diagnostics`, `UpscalingDiagnostics`, `PresentationStressDiagnostics` and `LifecycleDiagnostics` still cover prior features. `--framegen-soak` adds 20 repeated fullscreen trips and mode changes.
  - **Unknown:** live console gameplay quality, physical 60→120 scanout on a 120 Hz display, other GPUs/drivers, physical unplug, multi-hour operation and end-to-end latency were not measured. The inspected dependency graph is not exhaustive.
- **Data Contracts Involved:** source `Packet` leases remain unchanged. New trailing preference `Interpolation = 0` preserves old settings and defaults Off. GPU snapshots are RGBA8, motion fields RGBA32F, shader constants 32 bytes and summaries eight uints. The lightweight input limit is 1920×1080 pixels; larger screen output still uses optional NIS. Native unsupported/resource failures retain source playback; device-loss errors retain the existing failure/disconnect path. Source-rate recording and audio processing retain their formats and timing; audio alignment with the added video buffering is unverified.

### 🛠️ 2. High-Efficiency Implementation

```diff
+ internal enum FrameGenerationMode { Off, Adaptive2x }
+ internal enum PresentationKind { Source, Interpolated, Repeated }
+ internal readonly record struct PresentedFrame(long Time, long Duration,
+     PresentationKind Kind = PresentationKind.Source);

- public event Action<Packet>? FramePresented;
+ public event Action<PresentedFrame>? FramePresented;

+ if (gpu.PrepareInterpolation(packet))
+     midpointDue = FrameGenerationPolicy.Midpoint(lastRealTick, period);

+ Publish(gpu.RenderMidpoint(Sharpness, Upscaler));

- Preferences(..., int Upscaling = 0)
+ Preferences(..., int Upscaling = 0, int Interpolation = 0)
```

### 🛡️ 3. Verification & Validation Report

- **Controlled-image tests PASS:** known camera translation reconstructed the midpoint with zero mean absolute error in the checked interior, compared with 42.70 byte levels for repeating the source frame. Static HUD differences were zero. Static input and real-frame presentation remained pixel-identical. Scene cuts repeated the real frame without blending unrelated scenes. Sixteen consecutive camera-motion pairs and ten 1080p motion pairs were accepted. These finite fixtures do not establish artifact-free arbitrary gameplay.
- **Resource/cost checks:** added image surfaces at 1080p totaled **25,373,440 bytes (~24.2 MiB)** per active renderer, plus small buffers/shader objects/driver overhead. Warm upload/normalization and motion analysis averaged **0.8245 ms** in the packaged 1080p fixture. That number excludes midpoint synthesis and final display/upscaling costs. Rendering twice as many output frames also increases GPU work. Repeated budget overruns pause motion analysis temporarily and use real-frame fallback. No neural weights or CUDA/AI runtime was added. Four embedded shader objects total 67,764 bytes; shader source and `tools/Build-FrameShaders.ps1` are included for reproducible local compilation.
- **Live pacing PASS:** synthetic source **29.997 FPS**, accepted output presentations **59.993 FPS**, including **120 motion-interpolated presentations** over the measured four-second interval. This tests actual HWND presentation calls, not physical monitor scanout or button latency. Source 60→120 eligibility and fractional refresh rounding were checked; physical 120 Hz playback was not exercised.
- **Packaged soak PASS:** **20** fullscreen round trips with mode toggles and NIS enabled; sampled output averaged **60.03 FPS**, minimum **58.98 FPS**. **1,204** motion-interpolated frames were presented during the sampled phases. Recording wrote **33 original-source video samples** over its short test instead of adopting the preview's doubled rate. Live toggling, fullscreen transition, simulated input failure and repeated disconnect completed. Evidence: `artifacts/framegen-package-soak/results.json`.
- **Legacy compatibility PASS:** four fixed RGB32/NV12 rendering workloads and 38 setting/layout/size transitions matched 0.3.0 hashes with generation Off. Evidence: `artifacts/framegen-compatibility/results.json` versus `artifacts/upscaling-standard-compatibility/results.json`.
- **Blast-Radius Check:** packaged `--self-test` PASS, including NIS 1440p/4K image checks, color/range/matrix, sharpness, snapshots, preference migration and live switching. The original 90-frame H.264 / 141-packet AAC round trip, native/scaled NV12 recording, zero synthetic recording drops and 5,760-frame silent WASAPI playback checks passed. The existing 64 capture-stop, 12 preview-close and three audio-stop cycles, readiness starvation checks, late callbacks, reconnect and finalization tests passed. Final pooled-buffer, media-worker and MMCSS registration counts were zero. Release compilation/publish reported no compiler warnings/errors. Evidence: `artifacts/framegen-package-check/results.json`.
- **Visual QA:** the selector, saved preference, runtime status and 60-shown/30-input metric were inspected in `artifacts/framegen-package-check/frame-generation/interface.png`. Generated/expected synthetic images are saved in the same folder.
- **Limits:** complex motion, occlusion, transparency, motion blur and fine moving detail can still expose interpolation artifacts or trigger fallback. Output FPS counts accepted presentations, including repeated fallback images; it is not the console's internal simulation/render rate. Audio monitoring timing is unchanged and may require synchronization work for some devices. No claim of zero added latency, complete crash protection or universal DLSS superiority is made.
- **Recovery:** original application sources are backed up in `artifacts/framegen-before`; the preceding Desktop executable is backed up during delivery. Verified standalone: `artifacts/framegen-standalone/ArcCapture.exe`.
- **Token Efficiency Metric:** context-line accounting **not measured**. No token-savings or exhaustive-analysis claim.

[Microsoft EnumDisplaySettings](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaysettingsw) documents the read-only current display-mode/refresh query used here. [NVIDIA's video frame-conversion guide](https://docs.nvidia.com/video-technologies/optical-flow-sdk/nvfruc-programming-guide/index.html) discusses two-frame interpolation and forward/backward validation; this app uses its own compact shader implementation and does not bundle that library.
