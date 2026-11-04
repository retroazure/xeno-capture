# ARC Capture 0.2.3 — smoothness improvements

### 🔬 1. Virtual Graph Discovery

- **Target Coordinates:** `./ArcCapture/Engine/D3DPreview.cs` > constructor, `Compile`, `Shader`, `Draw`; `ArcCapture/Engine/MultimediaScheduling.cs` > `Enter`, `Dispose`; existing worker entry points in `ArcCapture/Engine/Capture.cs` > `CaptureStream.Run`, `ArcCapture/UI/Direct3DHost.cs` > `Run`, and `ArcCapture/Engine/AudioMonitor.cs` > `Run`.
- **Call Chain Trajectory (final map):**
  - **Verified:** `MainWindow.OnData` -> `Direct3DHost.Submit` -> one pending retained packet -> render-thread `Run` -> `D3DPreview.Draw` and `Present`. Fullscreen creates another host/device, so its constructor previously recompiled the same shaders on every entry.
  - **Verified:** `D3DPreview` constructor -> process-local, thread-safe `Lazy<byte[]>` caches -> `Compile` copies compiled bytecode and releases the compiler COM blobs -> each device creates its own shader objects. GPU objects and immediate contexts are not shared across render threads.
  - **Verified:** `Draw` -> upload current RGB32 or NV12 image -> update its 32-byte shader constants when parameters change -> preserve aspect-ratio viewport -> draw. Full-coverage targets omit the redundant clear; letterboxed/pillarboxed targets still clear every back buffer. Source dimensions, layout, sharpness, range and matrix all participate in constant invalidation.
  - **Verified:** capture, video preview and audio-output workers -> `MultimediaScheduling.Enter` with the Windows Capture, Playback and Audio profiles -> owner-thread disposal -> `AvRevertMmThreadCharacteristics`. Registration failure preserves ordinary scheduling. Native imports resolve in System32. Only these application threads are registered; no registry, process-priority, timer-resolution or system power settings are changed by normal playback.
  - **Verified tests:** `RenderPerformanceDiagnostics.Run` measures RGB32/NV12 rendering and output hashes; `SchedulingDiagnostics.Run` compares registered/unregistered wake-ups under bounded CPU contention; `CheckLifecycle` exercises all three profiles and repeated disposal/fallback. `PreviewDiagnostics.Run`, `Diagnostics.Run`, `PresentationStressDiagnostics.Run` and `LifecycleDiagnostics.Run` cover pixels, settings changes, fullscreen, audio, recording and disconnect races. `App.OnStartup` dispatches the diagnostic commands.
  - **Unknown:** Live Switch/Ripsaw gameplay and other devices/GPUs have not been measured in this change. Local benchmarks establish behavior under their stated conditions, not a universal improvement in monitor scanout or end-to-end latency. The inspected graph is not claimed exhaustive.
- **Data Contracts Involved:** existing `Packet` leases, RGB32/NV12 layouts, source timestamps, full/limited range, BT.601/709, sharpness and aspect ratio. Eight floats retain the existing 32-byte HLSL constant layout. Shader bytecode stays private and immutable after initialization; GPU shader/context ownership stays per renderer. MMCSS handles have a single synchronous worker owner. Recording options, audio formats, device selection, settings schema, the one-frame pending queue and prior disconnect/readiness fixes retain their behavior. No third-party application dependency or external data transfer was added.

### 🛠️ 2. High-Efficiency Implementation

```diff
+ // At the existing synchronous worker entry points:
+ using var scheduling = MultimediaScheduling.Enter("Capture");
+ using var scheduling = MultimediaScheduling.Enter("Playback");
+ using var scheduling = MultimediaScheduling.Enter("Audio");

- using var vs = Compile(VertexSource, "vs_4_0");
- using var ps = Compile(PixelSource, "ps_4_0");
+ static readonly Lazy<byte[]> VertexBytecode = new(() => Compile(VertexSource, "vs_4_0"));
+ static readonly Lazy<byte[]> PixelBytecode = new(() => Compile(PixelSource, "ps_4_0"));
+ vertexShader = Shader(VertexBytecode.Value, 12);
+ pixelShader = Shader(PixelBytecode.Value, 15);

+ // Existing constant-buffer map/write/unmap is guarded by:
+ if (!parametersValid || parameters != previousParameters)

+ // Existing back-buffer clear is guarded by:
+ if ((long)width * inputHeight != (long)height * inputWidth)
```

### 🛡️ 3. Verification & Validation Report

- **CPU scheduling result:** controlled comparison with three normal-priority CPU workers contending on one core, restricted to the diagnostic process. Capture wake-up overshoot at the 95th percentile fell from **94.2463 ms** without MMCSS to **0.4839 ms** with MMCSS; 120 measured wake-ups per case. Over-four-millisecond late wakes fell from 120 to zero. The preview thread was already AboveNormal; its 95th-percentile overshoot was 0.5020 ms without MMCSS and 0.4782 ms with it. Process affinity was restored, all test threads stopped, and registration/revert checks passed. This is a scheduling stress test, not gameplay latency. Evidence: `artifacts/smoothness-scheduling-check/results.json`.
- **Pixel equivalence:** all **four** RGB32/NV12 workloads at 1080p and 4K produced exactly matching SHA-256 hashes before/after optimization. All **38** hashes across color-range, matrix, sharpening, layout and size transitions matched. Evidence: `artifacts/smoothness-comparison.json`, with the original data in `smoothness-baseline-check` and `smoothness-optimized-check`.
- **Repeated-work reduction:** the steady 900-draw workloads now perform one constant upload and zero target clears for full-coverage output. Shader compilation occurs twice per process across all tested renderer constructions. Warm construction averaged 122.35 ms before and 114.84 ms afterward in this run. Median completed-batch time changed from 0.4172 to 0.4164 ms for RGB32 1080p, 0.4844 to 0.4857 ms for RGB32 4K, 0.2575 to 0.2675 ms for NV12 1080p, and 0.3782 to 0.3751 ms for NV12 4K. These small differences do not establish a consistent average GPU-time gain; removing redundant calls and improving scheduling under load are the supported changes.
- **Blast-Radius Check:** final standalone `--self-test` **PASS**. Live sharpness, full/limited range and matrix changes passed; scaling, letterboxing, RGB/NV12 colors and snapshot conversion passed. Native/scaled NV12 MP4 and H.264/AAC round trips passed: 90 video frames, 141 decoded AAC packets and zero synthetic recording drops. WASAPI accepted 5760 silent playback frames. The prior readiness-race checks and 64 capture-stop / 12 preview-close / three audio-stop cycles passed, including late callbacks, reconnect and recording finalization. Capture, Playback and Audio profiles registered on this PC; missing-profile fallback and repeated owner-thread cleanup passed. Final pooled-buffer, media-worker and MMCSS-registration counts were zero, with zero failed reverts. Evidence: `artifacts/smoothness-final-package-check/results.json`.
- **Final standalone transition soak:** 20 fullscreen round trips with resizing; 96 one-second playback samples, **59.94 FPS average**, **59.03 FPS minimum**, zero DXGI wait timeouts. Source interval at the 95th percentile was 17.06 ms. After close there were zero render threads, pooled buffers, media workers or MMCSS registrations. Evidence: `artifacts/smoothness-final-soak/results.json`.
- **Test-source correction:** an initial coarse-timer synthetic source sometimes generated approximately 30 ms gaps. The final soak uses a high-resolution waitable timer and a separate cancellation event for that diagnostic source. Production capture still follows the device's frame arrivals. No global timer-resolution change was introduced.
- **Limits:** actual HDMI capture/audio, physical hot-unplug, multi-hour sessions, other CPU/GPU/display combinations and end-to-end latency remain unverified. Missing task profiles were exercised; disabled-service/missing-library fallback paths were inspected but not forced on this Windows installation. A clean finite suite does not guarantee absence of all regressions.
- **Recovery:** backups of modified original files are in `artifacts/smoothness-before`. The prior Desktop executable is backed up during delivery. Tested output: `artifacts/smoothness-final-standalone/ArcCapture.exe`.
- **Token Efficiency Metric:** context-line accounting **not measured**. No token-savings or exhaustive-analysis claim.

The Windows [multimedia scheduler documentation](https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service) describes the task profiles and CPU scheduling behavior. [AvRevertMmThreadCharacteristics](https://learn.microsoft.com/en-us/windows/win32/api/avrt/nf-avrt-avrevertmmthreadcharacteristics) requires release on the registering thread, which these synchronous scopes preserve. Microsoft also recommends [updating buffers only when their data changes](https://learn.microsoft.com/en-us/windows/win32/direct3dgetstarted/understand-the-directx-11-2-graphics-pipeline). The cached bytecode is passed to each device's [CreatePixelShader](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11device-createpixelshader) call.
