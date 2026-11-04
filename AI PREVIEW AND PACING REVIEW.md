# ARC Capture 0.8.0 — AI preview filter and pacing

This update adds an independent AI video enhancement filter and optimizes its live-preview path. It uses the existing verified NVIDIA RTX Video SDK/model, with explicit output targets and four quality levels. It does not integrate the DLSS 4.5 transformer. NVIDIA documents that DLSS requires game-engine data while RTX Video accepts completed frames. The newer Maxine VFX stack was researched; this implementation keeps the existing native dependency rather than adding a CUDA/VFX installation. [NVIDIA RTX Video FAQ](https://nvidia.custhelp.com/app/answers/detail/a_id/5448), [DLSS 4.5 integration](https://developer.nvidia.com/blog/nvidia-dlss-4-5-delivers-super-resolution-upgrades-and-new-dynamic-multi-frame-generation/), [Maxine VSR modes](https://docs.nvidia.com/maxine/vfx/latest/Filters/VideoSuperResolution.html).

### 🔬 1. Virtual Graph Discovery

- **Target Coordinates:** ./ArcCapture/MainWindow.xaml > AI switch, target and quality controls; MainWindow.xaml.cs > SelectedAiVideo, AiVideoChanged, NewPreview, preference serialization, ToggleFullscreen; MainWindow.Diagnostics.cs > internal UI checks; Engine/AiVideoSettings.cs > Validate/Target; Engine/D3DPreview.AiVideo.cs > TryAiVideo, UploadAiFrame, SuspendEnhancements; Engine/D3DPreview.RtxVideo.cs > EnsureRtxVideo/TryUpscaleRtxVideo/DisposeRtxVideo; Engine/D3DPreview.cs > ImageSource/UploadFrame/Draw/RenderSource/Dispose; Engine/D3DPreview.Interpolation.cs > source-image revisions; UI/Direct3DHost.cs > Submit/Run/Publish/SuspendAsync; Engine/GpuSdk.cs > native exports; Native/ArcGpuBridge.cpp > VSR lifetime and synchronization.
- **Call Chain Trajectory — verified:** the new XAML events create immutable AiVideoSettings, update both hosts and save optional preferences. NewPreview inherits the selection. Host.Run applies configuration on its GPU-owner thread. RenderSource invokes the separate filter before the selected legacy scaler; success uses the explicit GPU output size. Off restores the legacy selection. Recording/audio continue to receive the original Packet callbacks.
- **Call Chain Trajectory — verified:** Draw compares retained, immutable capture packets byte-for-byte, with dimensions/layout/range/matrix checks. It excludes reusing the same mutable diagnostic buffer and invalidates after another upload. Each written GPU source has a content revision. The VSR result cache requires the exact source SRV/revision, dimensions and quality. This avoids upload/inference for identical captures without sample-based guesses. A changed pixel invalidates the cache.
- **Call Chain Trajectory — verified:** a target-size change synchronizes and reallocates input/output surfaces while retaining the loaded SDK feature. Quality changes synchronize before evaluation. Startup catch-up refreshes from the latest packet after a slow initialization. Frame-pacing phase correction uses the timestamp captured by Submit, rather than the completion time of GPU preparation.
- **Call Chain Trajectory — verified:** ToggleFullscreen awaits Direct3DHost.SuspendAsync. The old owner drains GPU work, releases SDK/history/cache resources and acknowledges completion before the next host starts. Window closure already awaits renderer stop before activating the embedded preview. A retained SDK handle that reports lost initialization gets one bounded recreation attempt. SDK calls are serialized; GPU completion precedes feature teardown. Input/output textures and SDK paths have native-owned lifetimes.
- **Data Contracts Involved:** existing upscaler and generation enum values remain unchanged. Preferences append AiEnabled=false, AiResolution=0 and AiQuality=3; invalid values normalize to Auto/High. Targets are Auto/720/1080/1440/2160, fitted to source aspect and never below input size. Quality maps to SDK levels 1/2/3/4. ImageSource gains a private ContentRevision, with no capture/recording schema change. ArcVsrSynchronize is a Cdecl x64 export returning 1 on success; the native library and managed delegate are updated together. Fullscreen diagnostic helpers now return Tasks and their callers await them.
- **Tests — verified:** AiVideoDiagnostics covers default-off migration, every resolution/quality, raw GPU output sizes, pixels, native size, 4:3, NV12/range, legacy restoration, failure/retry, one-pixel invalidation, no-input disable, source recording and six AI fullscreen round trips with generation. The full suite also exercises native/software frame generation, source encoders/audio, readiness races, asynchronous stop/close, preferences and scheduling. FrameGenerationDiagnostics records 120/180/240-target presentation-gap percentiles. No exhaustive compiler-generated call graph was produced; external consumers beyond this app were not verified.

### 🛠️ 2. High-Efficiency Implementation

```diff
+ AiVideoSettings(Enabled=false, OutputHeight=0, Quality=3)
+ Separate AI Video Super Resolution switch and Auto/720p/1080p/1440p/4K targets
+ Performance/Balanced/High/Ultra SDK quality, with actual output dimensions in status

- Recreate and reload the SDK feature when output dimensions change
+ Synchronize, resize only the textures, and retain the loaded feature
- Upload and infer every repeated capture packet
+ Cache byte-identical input and revision-matched GPU output

- Correct phase using the time after GPU preparation
+ Correct phase using packet arrival time
- Start the next preview while old SDK resources may remain active
+ Await owner-thread suspension and cleanup before fullscreen handoff

+ Serialize NGX calls; fence before teardown; keep SDK paths and GPU textures alive
+ Preserve selected legacy-filter pixels when the new switch is Off
+ Keep audio and recorded video on their existing source paths
```

### 🛡️ 3. Verification & Validation Report

- **Blast-Radius Check:** targeted checks passed before final packaging. Turning the filter off restored Standard, Normal and legacy RTX output pixel-for-pixel. Raw enhanced textures were verified at 1280×720, 1920×1080, 2560×1440 and 3840×2160. All four quality modes produced distinct hashes on the texture fixture. High 1440p differed from the SDK bicubic baseline by an absolute byte-difference total of 38,247,084; Ultra 4K differed from Standard in 22,350,274 bytes. These are output-activation checks, not a guarantee of superior subjective quality for all games.
- **Measured performance:** target switching initially cost approximately 1.7 seconds because each change reloaded the feature. With feature reuse, subsequent target changes took about 15–18 ms including diagnostic full-resolution readback and BGRA conversion. A 1080p High-quality workload averaged 3.37 ms at 1440p and 4.28 ms at 4K; its 30-in-60 duplicate workload averaged 1.71 / 2.18 ms and performed exactly 12 inferences for 24 packets. Benchmarks use synthetic motion, CPU upload and a final GPU readback; the final visible test surface is 1280×720. They do not measure monitor scanout or controller latency.
- **Failures found and handled:** development tests exposed a native driver fault in an unusually large diagnostic bicubic comparison, device removal while toggling GPU work, and lost initialization/overlapping lifetimes during fullscreen. The tests were retained in bounded representative form; teardown now fences queued work, SDK calls serialize, and fullscreen cleanup is awaited. Six AI fullscreen round trips, paused toggles, generation and source recording passed after those fixes. Source recording wrote 33 samples during an approximately 1.1-second, 30 FPS fixture. Exact driver internals were not established.
- **Dependency/security scope:** no new external model, installer, account, system setting or capture-data upload is added. The existing NVIDIA model hash/signature check remains in place; the rebuilt bridge is local reviewed source. All capture tests use synthetic frames and do not open a physical card or record a microphone. On an exceptional GPU completion timeout, the native bridge retains the outstanding session/texture references and blocks new SDK sessions until process exit rather than freeing GPU memory still in use. That timeout branch is not fault-injected in the current checks; it is not a claim of protection from every driver failure.
- **Limits:** first initialization on a new renderer still takes time. Higher quality/4K combined with high frame-generation rates may exceed GPU capacity. AI can alter texture/edge appearance; source detail identical to native 4K cannot be promised. Frame generation still adds buffering and can create artifacts. Physical capture latency, long-session gameplay quality and a complete GPU/driver matrix remain unmeasured. The new filter is preview-only and Off by default.
- **Token Efficiency Metric:** context-line accounting **not measured**; complete extraction baselines were not recorded. No token savings or exhaustive-graph guarantee is claimed.

Final release validation (2026-11-03):

- **PASS:** `artifacts/ai-pacing-final-suite/results.json`, standalone full suite. Includes the separate filter, six AI fullscreen round trips, all resolutions/qualities, native size/4:3/NV12, exact-cache invalidation, default-off migration, audio/encoders, source recording and disconnect/close. Shutdown retained 0 pooled buffers and 0 Media Foundation workers.
- **PASS:** `artifacts/ai-pacing-final-soak/results.json`, 20 round trips. The 40 short phase buckets ranged from 59.82 to 61.99 FPS. Automatic/forced 30-in-60 behavior, 2×/3×/4×, static and missing input, RTX, recording and failure/disconnect passed.
- **PASS:** four rendering workloads and 38 transition hashes matched 0.7.0 exactly in `artifacts/ai-pacing-release-render/results.json`. The final label invalidation and diagnostic fixture correction do not alter rendering.
- The duplicate-source soak assertion now checks capture packets versus distinct source images. The single-latest-frame slot may discard duplicate packets before GPU comparison; explicit GPU duplicate skip remains independently tested. A stale waiting label after resume was corrected and checked.

Final 60 FPS synthetic-input timing on the 240 Hz monitor, Normal upscaling, separate AI filter Off:

| Mode | Accepted FPS | Generated FPS | Gap P95 ms | Gap P99 ms |
| --- | ---: | ---: | ---: | ---: |
| 2× | 120.21 | 59.90 | 8.714 | 8.812 |
| 3× | 179.99 | 120.13 | 6.029 | 6.170 |
| 4× | 240.10 | 179.97 | 4.510 | 5.916 |

Final 1080p High AI synthetic processing, including upload and final readback, using a 1280×720 visible render surface:

| AI target | Distinct frames mean | 30-in-60 mean | Inference calls |
| --- | ---: | ---: | ---: |
| 1440p | 3.466 ms | 1.733 ms | 12 / 24 |
| 2160p | 4.407 ms | 2.246 ms | 12 / 24 |

First feature creation remains cold; subsequent target changes reuse it. These measurements establish the stated internal workloads, not physical input-to-display latency or a promise of 4K AI at 240 FPS.

Desktop release tested at version 0.8.0.
Executable SHA256: 7B81E052058C1B224849AE17FBD6A9B9A4496567A10272943924849C82765E95.
Files are copied with SHA256 verification and PACKAGE MANIFEST.json. The NVIDIA model signature remains valid. The user's running app is not killed or used to reopen capture hardware.

