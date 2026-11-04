# ARC Capture 0.7.0 — GPU features repair

The old driver-request RTX path accepted the request without changing the tested output. The old software block matcher often rejected complex motion, and a synthesis attempt could be counted as interpolated even if every output pixel fell back. Normal cadence also followed the capture packet rate unless the manual 30 FPS mode was enabled. These are confirmed code/test findings; they do not establish the quality of every real game scene.

### 🔬 1. Virtual Graph Discovery

- **Target Coordinates:** `ArcCapture/MainWindow.xaml.cs > UpdateMetrics, UpdateUpscalingStatus`; `MainWindow.xaml > PreviewFrameGeneration, Capped30GameToggle, PreviewUpscaling`; `UI/Direct3DHost.cs > Submit, Run/Prepare/Publish, ContentPeriod`; `Engine/D3DPreview.Interpolation.cs > EnsureInterpolation, PrepareInterpolation, RenderIntermediate, DisposeInterpolation`; `Engine/D3DPreview.OpticalFlow.cs > EnsureOpticalFlow, AnalyzeHardwareFlow, DisposeOpticalFlow`; `Engine/D3DPreview.RtxVideo.cs > EnsureRtxVideo, TryUpscaleRtxVideo, DisposeRtxVideo`; `Engine/GpuSdk.cs > Api`; `Native/ArcGpuBridge.cpp > ArcOfCreate/ArcOfEvaluate/ArcOfDestroy, ArcVsrCreate/ArcVsrEvaluate/ArcVsrDestroy`; `Shaders/FrameInterpolation.hlsl > Hardware, Synthesize`; `Engine/FrameCadenceTracker.cs > Observe/Period`; `Engine/FramePresentation.cs > TooLate`; csproj > shader/resources and native sidecars.
- **Call Chain Trajectory — verified:** input callbacks reach the active host's Submit; the cadence sampler observes packets before the single latest-frame slot drops superseded packets. Run checks the content-rate target against the current monitor, prepares distinct-frame history, and schedules fractional output. Preparation calls the NVIDIA optical-flow bridge when available, otherwise existing software motion analysis. Synthesis renders to one reusable output texture, counts pixels different from both sources, and returns PresentationKind. Publish counts only accepted Direct3D presentations, with separate generated/repeated counters. MainWindow calculates FPS using actual elapsed time.
- **Call Chain Trajectory — verified:** both native and generated images reach D3DPreview.RenderSource. Enlarged RTX mode normalizes RGB/NV12 through the existing range/matrix conversion and evaluates NVIDIA VSR directly. Success is published as AI Active. Unsupported configurations or expected model/library failures release partial state and fall back to Normal/Standard. Frame-generation Off retains the latest-frame native path. Recording/audio still consume their original source callbacks.
- **Ownership — verified:** all GPU/session use and cleanup remain on the render-owner thread. Optical-flow resources are unregistered before input history is released. SDK operations save/restore D3D11 context state. Disconnect retains the existing asynchronous renderer shutdown and session ownership. Device-loss failures follow the existing renderer-failure cleanup route. No exhaustive compiler-generated call graph was produced; external consumers beyond the inspected app were not verified.
- **Data Contracts Involved:** optional saved upscaler/generation integer values and defaults remain compatible. The existing Capped30Game boolean now forces 30 FPS cadence; false uses automatic detection. Packet still carries BGR32/NV12, timestamps, duration, range and matrix flags. Cdecl x64 GPU exports match managed delegates: VSR success is 1; optical-flow success is 0. Flow input is BGRA8, vectors are signed S10.5 at a 4-pixel grid, and cost is 8-bit. VSR uses RGBA8 input/output with UAV output. Separate GPU statistics identify output with at least 0.1% pixels different from both source images before counting it as generated.
- **Tests — verified:** FrameGenerationDiagnostics GPU/UI/multiplier/high-rate checks; HardwareInterpolationDiagnostics rotation/zoom/foreground/local-motion oracles and software fallback; CappedGameDiagnostics automatic/forced cadence, static/cut, pause/resume, recording, fullscreen and disconnect; RtxVideoDiagnostics SDK bicubic A/B, 1440p/4K, RGB/NV12 range, fallback and combined generation; existing Diagnostics, PresentationStressDiagnostics, LifecycleDiagnostics, scheduling and rendering benchmarks. The diagnostic high-rate window explicitly chooses the fastest existing monitor without modifying display settings.

### 🛠️ 2. High-Efficiency Implementation

```diff
- RTX: request a driver video-processor extension and assume policy may activate AI
+ RTX: load the verified NVIDIA model and evaluate the VSR SDK directly
+ Preserve full D3D11 context state around SDK operations

- Frame generation: coarse software blocks and capture-rate pacing by default
+ Use NVIDIA hardware optical flow when available, with software fallback
+ Detect distinct content cadence before superseded packets can be dropped
+ Preserve stable pixels/HUD and reject inconsistent motion or scene cuts

- Mark every synthesis attempt as interpolated
+ Measure synthesized output pixels before reporting generated presentations
- FPS = counter delta * 2
+ FPS = counter delta / actual elapsed seconds

- Drop phases later than 2/3 of an output interval
+ Tolerate DXGI readiness within 9/10 of that interval, while retaining the
+ one-pending-packet / one-frame DXGI queue limits and bounded phase skipping

+ Ship the native bridge, NVIDIA model and license in the private gpu folder
```

### 🛡️ 3. Verification & Validation Report

- **Blast-Radius Check:** Release checks and Desktop package verification are recorded below when complete. No physical capture card or microphone is opened by these diagnostics. Synthetic audio is encoded/decoded and WASAPI playback uses silence. Existing source recording rates, preferences, input conversion, fallback, fullscreen and asynchronous disconnect/close are covered. Pixel fixtures prove changed/improved output on known motion, not artifact-free gameplay.
- **Baseline results:** initial full suite passed; all 4 legacy rendering workloads and 38 setting-transition hashes matched the prior baseline exactly. Generated camera rotation/zoom/object error was 0.52 versus 26.63 for repetition and 19.07 for a simple blend; localized object error was 3.46 versus 14.37 and 11.82. Static HUD differences were zero. Warm 1080p optical-flow analysis averaged approximately 0.75–0.98 ms; image surfaces totaled 30,787,200 bytes (29.4 MiB), excluding driver overhead. These are synthetic measurements on the RTX 5070 Ti.
- **Direct VSR evidence:** the app's 720p-to-1440p VSR-versus-SDK-bicubic comparison had absolute byte differences totaling 47,733,904. Model creation/evaluation, 4K resizing, flat colors, NV12 range/matrix conversion, Normal identity after toggling, fallback/retry and six fullscreen cycles with generation passed. A separate typed GPU probe measured the expected +8/-8 pixel vectors after correcting BGRA input during integration. The integration-format issue was caught before packaging; it was not the cause of the old software-only implementation's failures.
- **High-refresh correction evidence:** before the deadline correction, the 60 FPS/4× Normal-upscaling fixture on a 240 Hz display presented about 158 FPS. The corrected focused run presented 119.95 / 179.93 / 239.12 FPS at 2× / 3× / 4×, with 59.98 / 119.16 / 179.14 generated FPS. Source was synthetic 640×360, Normal upscaling enabled. This is accepted-presentation timing, not measured physical scanout or a 4K/240 performance promise.
- **Native dependency boundary:** the official NVIDIA SDK archive was checked against NVIDIA metadata (SHA256 ABF4F34E2B5A618E355B0D5A0365D8ECC3DB4396E756E4C850A867E1AE2ED69E). The bundled nvngx_vsr.dll has a valid NVIDIA signature and pinned SHA256 C3D88EEA5FF7A548EDEFA66414CF6E77464D0947277C904F324DD23ABF58A1ED, checked by the app before VSR initialization. nvofapi64.dll is loaded from System32 and comes from the installed driver. The bridge is compiled from local reviewed source. No driver/global security setting is changed, and no application upload, account action or message is implemented by this change. NVIDIA SDK licensing is bundled; the MIT header notice is retained. This is a scoped dependency/ownership check, not a full security audit.
- **Limits:** generation adds one content frame of buffering and may create artifacts or use fallback in difficult motion. It supports source input up to 1080p. RTX first use initializes the model and can pause output. Combining VSR with high target rates costs additional GPU work; 4K/high-refresh hardware/gameplay quality and button-to-screen latency remain unmeasured. Tests do not prove absence of all crashes/regressions. The older Desktop executable is preserved as a recovery option.
- **Token Efficiency Metric:** context-line accounting **not measured**. Extraction baselines/ranges were not recorded completely; no token-saving or exhaustive-graph claim is made.

Primary references: [NVIDIA RTX Video SDK](https://developer.nvidia.com/rtx-video-sdk), [NVIDIA optical-flow programming guide](https://docs.nvidia.com/video-technologies/optical-flow-sdk/nvofa-programming-guide/index.html), [official NVIDIA model metadata](https://api.ngc.nvidia.com/v2/models/nvidia/multimedia/dlpp/versions/1.5/files).

Release completion evidence (2026-11-03):

- **PASS:** final standalone --self-test (`artifacts/gpu-fix-release-suite/results.json`). Includes hardware/software interpolation, real generated-pixel counters, direct VSR, audio encode/decode/playback, recording, preferences, fullscreen, input failure and disconnect/close. Shutdown retained 0 pooled packet buffers and 0 Media Foundation workers.
- **PASS:** final standalone --capped-game-soak (`artifacts/gpu-fix-release-soak/results.json`), 20 fullscreen round trips. The 40 short phase buckets ranged from 59.83 to 61.99 FPS; RTX combined use, source-rate recording, static/missing-input behavior and disconnect also passed.
- **PASS:** final high-rate fixture on 240 Hz: 120.07, 179.69, 239.01 accepted FPS for 2×/3×/4×; 179.26 generated FPS at 4×. Draw-submission P95 at 4× was 0.273 ms, excluding preparation and physical display latency.
- **PASS:** four legacy workload pixel hashes plus 38 transition hashes matched `artifacts/rtx-video-compatibility/results.json` in `artifacts/gpu-fix-final-render/results.json`. The final deadline change does not modify native/Normal rendering.
- **Packaged:** Version 0.7.0 and its matching GPU files were verified with SHA-256. The model signature was valid.
- Executable SHA256: 8ACA9800D8FE6CF2E382BF598E7EEFB67AC810D3CEAB35825601C1D2ED4F5BAD. A per-file PACKAGE MANIFEST.json accompanies the Desktop package. Documentation and notices are copied with hash verification. The running user app was not killed or used to reopen capture hardware.

