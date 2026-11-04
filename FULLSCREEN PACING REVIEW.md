# ARC Capture 0.2.2 — fullscreen pacing investigation

### 🔬 1. Virtual Graph Discovery

- **Target Coordinates:** `./ArcCapture/UI/Direct3DHost.cs` > `Run`, `Active`, `Submit`; `ArcCapture/Engine/D3DPreview.cs` > `WaitForFrameSlot`, `Present`, `Resize`.
- **Call Chain Trajectory (final map):**
  - **Verified:** `ArcCapture/MainWindow.xaml.cs:296`, `ToggleFullscreen` -> embedded `Active=false` and collapsed video host -> a new fullscreen `Direct3DHost`. The closed callback reactivates the retained embedded host and submits the latest retained packet.
  - **Verified:** `MainWindow.OnData` -> active host `Submit` -> replace one pending `Packet` lease and set an `AutoResetEvent`. The notification does not own a particular packet.
  - **Verified:** `Direct3DHost.Run` -> check for an active pending packet -> `D3DPreview.WaitForFrameSlot` -> late-latch newest packet -> `Resize`, `Draw`, `Present` -> `FramePresented` only for S_OK. Immediate-context calls, readiness credit and COM disposal remain on the render thread. Native failures still reach the existing session-fenced failure callback.
  - **Verified:** `D3DPreview.WaitForFrameSlot` -> Win32 `WaitForSingleObject`. A signaled wait acquires one readiness credit; `Present` consumes it after S_OK. Pausing or finding no packet preserves it. A timeout returns without drawing/presenting; the host checks stop/active state again before retrying.
  - **Verified tests:** `ArcCapture/Engine/PresentationStressDiagnostics.cs`, `Run` -> actual HWND/Direct3D regression checks plus production `MainWindow.ToggleFullscreen` / `PreviewWindow.CloseAsync` through diagnostic entry points. `ArcCapture/Engine/Diagnostics.cs`, `Run` includes the short race checks. Existing `PresentationDiagnostics.Run` and `LifecycleDiagnostics.Run` cover UI stalls, GPU pixels, audio, recording, disconnection and shutdown races.
  - **Unknown:** The user's live Switch/capture-card session was not traced. The code defect is reproduced, and its old 100 ms waits explain a route to roughly 10 FPS; additional driver or USB stalls are not ruled out.
- **Data Contracts Involved:** per-consumer disposable `Packet` leases, one newest pending packet, source `Time`/`Duration`, DXGI wait result and Present HRESULT, asynchronous `StopAsync`, `FramePresented` callback. `WaitForFrameSlot` and `Present` now return `bool`; the inspected host is their sole application caller. No recording format, audio-input, settings schema, dependency, network destination, authentication or authorization boundary changed. The diagnostic backend supplies generated 1920x1080 NV12 frames at 60 FPS. Tests bypass physical device discovery and preference writes; logs contain numeric timings and synthetic test paths.

### 🛠️ 2. High-Efficiency Implementation

```diff
- signal.WaitOne();
- gpu.WaitForFrameSlot(); // consumes readiness even when latest is absent
+ // Read pending/active state under the existing gate first.
+ if (!pending) { signal.WaitOne(); continue; }
+ if (!gpu.WaitForFrameSlot()) { /* count timeout and retry */ continue; }
  // Late-latch latest, preserving acquired readiness if paused or absent.
  gpu.Draw(frame, Sharpness);
- gpu.Present(); FramePresented?.Invoke(frame);
+ if (gpu.Present()) FramePresented?.Invoke(frame);

- WaitForSingleObject(frameReady, 100); // result ignored
+ if (frameSlotReady || frameReady == 0) return true;
+ uint result = WaitForSingleObject(frameReady, 20);
+ if (result == 0) { frameSlotReady = true; return true; }
+ if (result == 258) return false;
+ throw new Win32Exception(...);

+ // After an S_OK Present, release the acquired credit:
+ frameSlotReady = false;
```

### 🛡️ 3. Verification & Validation Report

- **Confirmed reproduction:** A deliberately redundant renderer wake with no packet consumed the native DXGI event in the instrumented old loop. The next frame took **117.842 ms**, including one wait timeout. Evidence: `artifacts/fullscreen-baseline-check/results.json` and its expected `failure.txt`. This reproduces the stale notification that can remain when a source arrival during the wait is taken by the same iteration. A fullscreen pause can also discard the pending packet after readiness is acquired.
- **Fixed standalone race checks:** redundant notification **11.3223 ms**, pause after readiness **5.2222 ms**, replacement during the wait followed by another frame **5.0244 ms**, **zero wait timeouts**, newest replacement packet verified. Evidence: `artifacts/fullscreen-package-check/presentation-races/results.json`.
- **Transition soak:** generated 1080p60 video through the real capture-session delivery and production fullscreen handlers; **20 round trips**, alternating window dimensions, **96 one-second samples**, **59.9845 FPS average**, **58.9949 minimum**. Maximum sampled transition-to-first-frame wait **59.452 ms**. **Zero** DXGI wait timeouts. Maximum **two** live render threads (retained embedded plus fullscreen); **zero** after close, **zero** retained pooled buffers and **zero** media workers. Handle counts were 1157 before cycling and 962 afterward; this bounded run does not prove absence of every resource leak. Evidence: `artifacts/fullscreen-soak-final/results.json`. The production pacing code in this soak matches the packaged version; later changes added a test assertion, standard-suite integration and version labels.
- **Blast-Radius Check:** standalone 0.2.2 `--self-test` **PASS**. GPU RGB/NV12 colors, scaling, letterboxing and snapshot conversion passed; native and scaled NV12 MP4 passed; **90** video frames and **141** AAC packets decoded, **zero** synthetic recording drops, **5760** silent WASAPI frames accepted. Steady presentation **60.30 FPS**, including **18** frames during a 300 ms UI pause. The existing **64** capture-stop cycles, **12** preview-close cycles, **three** audio-stop cycles and late callback/startup/reconnect/recording-finalization checks passed. Final pooled-buffer and media-worker counts were zero. Build/publish succeeded without reported compiler warnings or errors. Evidence: `artifacts/fullscreen-package-check/results.json`.
- **Limits:** Synthetic source and simulated hot-unplug only. Physical Switch/Ripsaw capture, real unplug/driver hangs, extended multi-hour operation, other GPUs/displays, audio/video drift and button-to-screen latency remain unverified. Positive Present statuses are not counted as successful displayed submissions; occlusion is not claimed as the reproduced cause, and flip-model swap chains ordinarily do not report DXGI_STATUS_OCCLUDED.
- **Recovery:** pre-edit source files are under `artifacts/fullscreen-before`; the previous Desktop executable is backed up when the new package replaces it. The standalone output is `artifacts/fullscreen-standalone/ArcCapture.exe`.
- **Token Efficiency Metric:** context-line accounting **not measured**. No token-savings or exhaustive-call-graph claim.

Microsoft documents waiting before rendering each frame and closing the wait handle when finished in [GetFrameLatencyWaitableObject](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-getframelatencywaitableobject). The renderer now preserves that acquired readiness across skipped work. [Present](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-present) documents the HRESULT results and possible waits on the message-pump thread; asynchronous stop-before-HWND-destruction remains intact. [DXGI status codes](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-status) distinguishes occlusion from success and notes that flip-model swap chains do not report that status.
