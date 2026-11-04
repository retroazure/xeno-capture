# ARC Capture 0.5.0 — Normal and RTX Video Super Resolution

Under Output → Preview Upscaling, Normal · Adaptive detail is the existing NVIDIA Image Scaling filter (previously called Enhanced). RTX Video Super Resolution adds NVIDIA's driver video-processing path. Standard · Fast scaling remains available. Existing saved upscaling choices keep their numeric values; RTX is optional, value 2.

**Setup:** select RTX Video Super Resolution, enable Super Resolution under NVIDIA app → System → Video, and enlarge the preview beyond the source resolution. The selected preview adapter must support NVIDIA's video-driver extension. On hybrid systems, Windows Graphics settings may need to select the NVIDIA RTX adapter for ARC Capture. Driver quality/policy and GPU capacity govern enhancement; the app does not modify global NVIDIA/Windows graphics settings.

**Validation limit:** the RTX 5070 Ti driver accepted the request and processed 1440p/4K video, but the test output matched the same video-processor pipeline with the request disabled. Actual neural enhancement was **not confirmed** under the current driver configuration. The UI therefore says “Driver requested,” never claims confirmed AI activity, and explains the NVIDIA setting. This integration is RTX Video, not DLSS 4. An accepted request alone is not proof of AI enhancement. No image-quality or performance advantage is claimed from this run.

### 🔬 1. Virtual Graph Discovery

- **Target Coordinates:** `./ArcCapture/MainWindow.xaml` > PreviewUpscaling/RtxVideoHint; `MainWindow.xaml.cs` > SelectedUpscaler, UpscalingChanged, UpdateUpscalingStatus, ApplyPreferences, CurrentPreferences; `Engine/D3DPreview.Nis.cs` > PreviewUpscaler enum; `Engine/D3DPreview.cs` > RenderSource/Dispose; new `Engine/D3DPreview.RtxVideo.cs` > CheckRtxAdapter, EnsureRtxVideo, TryUpscaleRtxVideo, VideoBlt, ResetRtxVideo, DisposeRtxVideo; `UI/Direct3DHost.cs` > Publish and RtxVideoRequested; `App.xaml.cs` > --rtx-video-tests route; `Engine/Diagnostics.cs` > RTX test stages.
- **Call Chain Trajectory — verified:** XAML SelectionChanged → UpscalingChanged → both existing host Upscaler properties and saved preferences. NewPreview inherits SelectedUpscaler when opening/recreating a host. Capture supplies RGB32/NV12 Packet data; upload creates an ImageSource. Both source-frame Draw and generated-frame RenderIntermediate reach RenderSource. For RTX and an enlarged viewport, TryUpscaleRtxVideo normalizes captured color/range/matrix into a GPU RGB texture, uses D3D11 video processing to convert to limited-range BT.709 NV12, then requests NVIDIA's per-processor Super Resolution extension and processes to an output BGRA texture. RenderSource displays its SRV using the existing aspect/letterbox/sharpness path. Direct3DHost publishes the accepted-request flag/status to the UI. Normal retains NIS; Standard retains the original fast path.
- **Call Chain Trajectory — verified failure/lifetime edges:** non-NVIDIA/unsupported format/extension/resource/processing failures dispose partial RTX state and try Normal; if NIS is also unavailable, Standard is retained. Failed geometry is latched to avoid retrying every frame; changed geometry or mode selection allows retry. Device-loss error codes escape the feature fallback into the existing renderer-failure/input cleanup path. Mode changes/native-size rendering and renderer disposal release video views, processors, enumerators, surfaces and queried COM interfaces. All native operations stay on the renderer thread. No external API consumer was found in inspected application references; an exhaustive compiler call graph was not generated.
- **Data Contracts Involved — verified:** PreviewUpscaler remains Standard=0, Enhanced=1 (UI name Normal), with RtxVideo=2 appended. Preferences.Upscaling remains an optional integer with default zero; invalid values restore Standard. Existing settings and frame-generation multiplier values are preserved. Packet and PresentedFrame contracts remain unchanged. New D3D11 structure/vtable declarations were checked against the installed Windows SDK (d3d11.h/dxgi.h), including 40-byte content, 72-byte x64 stream and 304-byte adapter-description layouts. The NVIDIA extension uses a fixed GUID and 12-byte version/method/enable payload as seen in Chromium's implementation. No frame/model/capture data is uploaded. No additional native runtime, SDK binary, AI model, network endpoint or application package was added.
- **Tests — verified:** new `Engine/RtxVideoDiagnostics.cs` RunGpu/RunUi is invoked by --rtx-video-tests and the full Diagnostics suite. It checks driver requests, 1440p/4K processing, RGB/NV12 gray and flat colors, fallback pixel identity, partial allocation/processing failures, resize recovery, released surfaces, native-size idle, preference migration/restore, live Normal/RTX switches, six fullscreen round trips with 2×/3×/4× frame generation, source recording and simulated failure/disconnect. Existing rendering, NIS, generation, encoding, audio and lifecycle consumers were also exercised.

### 🛠️ 2. High-Efficiency Implementation

```diff
- internal enum PreviewUpscaler { Standard, Enhanced }
+ internal enum PreviewUpscaler { Standard, Enhanced, RtxVideo }

+ if (upscaler == PreviewUpscaler.RtxVideo && enlarge)
+ {
+     var rtxDisplay = TryUpscaleRtxVideo(source, outputWidth, outputHeight);
+     // Display the driver result when available; otherwise retain Normal/Standard.
+ }

- Enhanced · Adaptive detail
+ Normal · Adaptive detail
+ RTX Video Super Resolution

+ public bool RtxVideoRequested => Volatile.Read(ref rtxVideoRequested);
```

### 🛡️ 3. Verification & Validation Report

- **Full regression PASS:** `artifacts/rtx-video-package-check/results.json`. The suite covered GPU preview/color/sharpness/letterbox, NIS 1440p/4K, RTX GPU/UI and forced failure paths, 2×/3×/4× generation, recording/snapshots, H.264/AAC and NV12 paths, silent WASAPI playback, presentation readiness, late callbacks, reconnect/finalization, capture/preview/audio stop cycles and scheduling ownership. Pooled buffers, Media Foundation workers and MMCSS registrations were zero at shutdown. The final native-size status/string-bound/copy changes were then checked by the targeted final standalone run.
- **Final standalone targeted PASS:** `artifacts/rtx-video-final-check/results.json`. The NVIDIA RTX 5070 Ti accepted the per-processor request. 1440p/4K resize, flat RGB colors, NV12 range/matrix gray checks, fallback/resource release and retry after changed geometry passed. Six fullscreen round trips exercised 2×, 3× and 4× presentation with a 15 FPS synthetic source. Recording retained the source rate. Saved setting/live switches and simulated input-failure/disconnect passed.
- **Legacy pixel compatibility PASS:** `artifacts/rtx-video-compatibility/results.json` versus `artifacts/framegen-compatibility/results.json`: all four RGB/NV12 1080p/4K workloads and 38 setting/layout/size transition hashes were identical. The unchanged Normal/NIS output also matched before/after RTX toggles in the new GPU tests.
- **Visual QA:** the selector, NVIDIA setup hint, version, frame-generation choice/status and recording controls were inspected in `artifacts/rtx-video-check-2/interface.png`. Native HwndHost video is absent from WPF RenderTargetBitmap screenshots, so GPU readback images in the diagnostic folders verify processed pixels separately. The final targeted run also produced an updated interface image.
- **Blast-Radius Check:** no recording/audio source processing, capture queues, frame-generation multiplier values, display modes, global GPU/video settings, driver installations, security settings, authentication or external accounts were changed. Existing NIS/shader assets and notices remain. Native/unsupported fallbacks preserve playback; device loss is not converted into a claimed successful filter. This is checked behavior, not a guarantee against every crash or regression.
- **Resource behavior:** production processing is GPU-resident with no CPU video readback; readback is diagnostic only. No app-side frame-history queue is added by RTX mode. It does add RGB/NV12/output surfaces and driver processing/model costs. At 720p→1440p the measured app image surfaces used 19,814,400 bytes (about 18.9 MiB), excluding driver model allocations/overhead. Even-sized input from 360p through 1440p, width up to 2560, is supported by this integration; unsupported input returns to Normal. Driver processing and AI can add GPU time/latency; zero overhead and zero latency are not claimed.
- **Confirmed limitation:** request-enabled/disabled output hashes matched on this machine. Actual neural enhancement, quality improvement and AI throughput were not confirmed. If the driver accepts the request while its enhancement policy is inactive, the driver still supplies ordinary video processing; the UI does not label that state as confirmed AI. Enable the NVIDIA video setting and verify activity in NVIDIA app. Physical capture-card/console image quality, unplug on real hardware, other GPU/driver families, long-session behavior and end-to-end input/audio latency were not exercised.
- **Recovery:** original sources are preserved in `artifacts/rtx-video-before-20261103-192532`. Desktop delivery keeps the previous 0.4.1 executable as a timestamped backup and verifies the installed executable against the final tested standalone by SHA-256.
- **Token Efficiency Metric:** context-line accounting was not measured; no token savings or exhaustive graph guarantee is claimed.

The request-only distinction follows [NVIDIA's RTX Video FAQ](https://nvidia.custhelp.com/app/answers/detail/a_id/5448) and the [DLSS required-input guide](https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideDLSS.md). The driver protocol was checked against [Chromium's implementation at a pinned revision](https://chromium.googlesource.com/chromium/src/+/34c45dcd9760e331337881af570dd474ca3678cf/ui/gl/swap_chain_presenter.cc), using Microsoft's [video stream extension API](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11videocontext-videoprocessorsetstreamextension) and [color-space definition](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_video_processor_color_space). NVIDIA describes the technology on its [RTX Video developer page](https://developer.nvidia.com/rtx-video-sdk).

Final targeted results:

```json
{
  "result": "PASS",
  "adapter": "NVIDIA GeForce RTX 5070 Ti",
  "driverRequestAccepted": true,
  "driverRequestVsDisabledPixelDifference": 0,
  "aiActivationConfirmed": false,
  "fullscreenRoundTrips": 6,
  "outputFpsDuringGeneratedPhases": [
    30.014923419924386,
    29.997966137895848,
    44.99894252485067,
    44.99559493125623,
    60.936345893080095,
    59.97149554816596,
    30.983358837968126,
    29.973075186559914,
    44.99469962438425,
    44.96457690631321,
    59.98106997431611,
    59.962870990282816
  ],
  "sourceRecordingSamples": 16,
  "geometryRetry": "pass"
}
```
