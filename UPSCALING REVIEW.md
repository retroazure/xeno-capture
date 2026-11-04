# ARC Capture 0.3.0 — optional image upscaling

Choose **Output → Preview Upscaling → Enhanced · Adaptive detail**, then **Play fullscreen**. The preview fits the screen automatically, including 1440p and 4K displays. Use **Preview Sharpness** to adjust the effect. Standard remains the default for existing settings. Enhanced activates when the displayed picture is larger than the source; recording resolution is a separate setting.

### 🔬 1. Virtual Graph Discovery

- **Target Coordinates:** `./ArcCapture/Engine/D3DPreview.cs` > `Draw`, `SetGraphicsParameters`, `Dispose`; new `ArcCapture/Engine/D3DPreview.Nis.cs` > `EnsureNis`, `UpscaleNis`, `SetNisParameters`, `DisposeNis`; `ArcCapture/UI/Direct3DHost.cs` > `Upscaler`, `Run`; `ArcCapture/MainWindow.xaml` > `PreviewUpscaling`; `MainWindow.xaml.cs` > `UpscalingChanged`, `NewPreview`, `ApplyPreferences`, `CurrentPreferences`.
- **Call Chain Trajectory (final map):**
  - **Verified:** the preview selector -> both hosts' volatile upscaler setting; `NewPreview` inherits it when entering fullscreen or reconnecting. `Preferences.Upscaling` persists the choice. Older settings omit the new field and retain Standard; unknown mode values also select Standard.
  - **Verified:** `MainWindow.OnData` -> one retained pending `Packet` -> `Direct3DHost.Run` -> `D3DPreview.Draw`. Standard uses the established sampling/sharpening path. Enhanced uses the aspect-fitted pixel dimensions reported by the HWND host and activates only for enlargement.
  - **Verified:** Enhanced RGB32 -> NIS compute pass -> GPU display. Enhanced NV12 -> established native-size GPU color/range conversion -> NIS -> display. NIS includes sharpening, so the presentation pass does not sharpen its result again. Scaling above 2x uses bounded stages, with sharpening applied at the last stage.
  - **Verified:** `EnsureNis` creates device-owned compute shader, coefficient textures, constants and output surfaces. Native compute inputs/UAVs are unbound between passes; mode changes, resize and disposal release resources on the existing render thread. No frame-history buffer or new worker is added.
  - **Verified:** recoverable unsupported/resource errors fall back to Standard with a preview-status tooltip. The HRESULT check covers managed exception mappings such as ArgumentException/OutOfMemoryException. Device-loss errors still follow the established session-fenced failure/disconnect path.
  - **Verified tests:** `UpscalingDiagnostics.RunGpu` and `RunUi`; standard `Diagnostics.Run` includes them. `PresentationStressDiagnostics.Run` accepts an enhanced-mode soak. `RenderPerformanceDiagnostics.Run` verifies legacy pixels; existing `LifecycleDiagnostics.Run` exercises disconnect, audio and recorder cleanup.
  - **Unknown:** quality and timing on live Switch/Ripsaw input, other GPUs, physical hot-unplug and multi-hour sessions were not measured. This inspected graph is not exhaustive.
- **Data Contracts Involved:** existing RGB32/NV12 packet leases, source dimensions/timestamps and range/matrix flags. Preview controls add `PreviewUpscaler.Standard/Enhanced` and trailing optional `Preferences.Upscaling = 0`. NIS uses its documented 112-byte constant layout, FP32 coefficient textures and RGBA8 output, with source/output ratios between 0.5 and 1 per stage. Recording, snapshots, audio formats, capture negotiation and the one-pending-frame queue retain their behavior.
- **Source/permission boundary:** NVIDIA Image Scaling SDK v1.0.3, pinned to revision `35e13ba316c98eeecf16f37eae70ce88019911f6`. Source, license and hashes are in `ArcCapture/ThirdParty/NIS`. The 26,708-byte compute shader was compiled locally with the installed Windows SDK; coefficient resources are derived from the pinned header. `tools/Build-NisShader.ps1` reproduced all three resources byte-for-byte. Runtime resources are embedded, so normal builds need no extra SDK installation and playback makes no download or external data transfer. The MIT notice is embedded and included in the Desktop package.

### 🛠️ 2. High-Efficiency Implementation

```diff
+ internal enum PreviewUpscaler { Standard, Enhanced }
- public void Draw(Packet frame, double sharpness)
+ public void Draw(Packet frame, double sharpness,
+                  PreviewUpscaler upscaler = PreviewUpscaler.Standard)

+ display = UpscaleNis(frame, outputWidth, outputHeight, sharpness);
+ parameters = new(outputWidth, outputHeight, 0, 0, 0, 0);

- sealed record Preferences(..., double Sharpness);
+ sealed record Preferences(..., double Sharpness, int Upscaling = 0);
```

### 🛡️ 3. Verification & Validation Report

- **Image checks PASS:** 1080p to 1440p and 4K produced enhanced output; the sharpness control responded. Flat colors, RGB channel order, NV12 full/limited range and BT.601/709 matrices passed. Odd output dimensions, letterboxing, four-stage large-ratio enlargement and no unnecessary enlargement at source size passed. Identical input remained stable over repeated draws and 120-frame batches. Returning to Standard produced identical pixels.
- **Legacy equivalence PASS:** four fixed RGB32/NV12 workloads at 1080p/4K plus 38 setting/layout/size transitions matched the prior 0.2.3 output hashes. Evidence: `artifacts/upscaling-standard-compatibility/results.json` compared with `artifacts/smoothness-optimized-check/results.json`.
- **Completed render batches:** packaged 1080p RGB32 input enlarged to 1440p averaged **0.4991 ms/frame**; 4K averaged **0.6424 ms/frame** in the synthetic scene. These 120-frame batch timings include a final GPU readback, not capture or monitor scanout. They are workload-specific and do not guarantee zero overhead or identical performance on other GPUs.
- **Settings/UI PASS:** old settings select Standard; Enhanced round-trips through the production preference model. Live switching updates windowed and fullscreen hosts, three interactive fullscreen round trips passed, and disconnect while upscaling passed. The interface and synthetic text/line comparison crops were visually inspected. Evidence: `artifacts/upscaling-package-check/upscaling/interface.png`, `standard-detail.png`, `enhanced-detail.png`.
- **Packaged fullscreen soak PASS:** **20** round trips with resizing, **96** one-second playback samples, **60.00 FPS average**, **59.02 minimum**, **3,240** accepted frame presentations through Enhanced and **zero DXGI wait timeouts**. Input was generated 1280x720 NV12 at 60 FPS so fullscreen genuinely enlarged it on this display. Windowed phases use the normal path when enlargement is unnecessary. Render-thread, media-worker, pooled-buffer and MMCSS-registration counts were zero after close. Evidence: `artifacts/upscaling-enhanced-soak/results.json`.
- **Blast-Radius Check:** standalone `--self-test` PASS, including the previous readiness-race fixes, GPU readback, 90 encoded/decoded video frames, 141 decoded AAC packets, native/scaled NV12 MP4, zero synthetic recording drops and 5,760 silent WASAPI playback frames. The existing 64 capture-stop, 12 preview-close and three audio-stop cycles passed, along with late callbacks, reconnect, startup cancellation and recording-finalization tests. Final pooled-buffer, media-worker and MMCSS-registration counts were zero. Source build and publish completed without reported compiler warnings/errors. Evidence: `artifacts/upscaling-package-check/results.json`.
- **Limits:** this is spatial image scaling, not DLSS or AI reconstruction; missing native detail cannot be recovered. Recording/snapshots use their existing source/output processing. Unsupported capability was simulated and native HRESULT mappings were checked; no physically unsupported GPU was used. Physical capture, unplug, long-session drift and end-to-end button latency remain unverified.
- **Recovery:** original modified application files are backed up under `artifacts/upscaling-before`; the previous Desktop executable is backed up during replacement. Verified standalone: `artifacts/upscaling-standalone/ArcCapture.exe`.
- **Token Efficiency Metric:** context-line accounting **not measured**. No token-savings or exhaustive-analysis claim.

[NVIDIA Image Scaling documentation and MIT source](https://github.com/NVIDIAGameWorks/NVIDIAImageScaling) describes its directional scaling, sharpening, resource bindings and Direct3D 11 integration. The pinned configuration implementation defines the per-stage scale limits and SDR parameters used here.
