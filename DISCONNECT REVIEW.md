# ARC Capture 0.2.1 — disconnect review

Scope: normal disconnect, input removal/failure callbacks, close during startup, fullscreen exit, audio playback shutdown, recording finalization and native-object lifetime. This is a targeted lifecycle review, not a complete application or driver security audit. Only generated media and simulated capture devices were used for the new regression cases; no camera or microphone was recorded.

### 🔬 1. Virtual Graph Discovery

All paths below are under `./ArcCapture/`.

| Target coordinates | Verified callers and downstream behavior | Contracts and test coverage |
| --- | --- | --- |
| `MainWindow.xaml.cs > ConnectSelected, OnData, StartOrStopRecording, OnClosing` | UI handlers and callbacks call `MainWindow.Lifecycle.cs > DisconnectCore, StopSources, StopRecordingCore, StopMonitor` | Session generation, startup completion, pending versus published inputs/encoders; exercised through the production handlers by `Engine/LifecycleDiagnostics.cs` and `MainWindow.Diagnostics.cs`. |
| `MainWindow.Lifecycle.cs > DisconnectCore` | Stops capture, audio and recording, awaits rendering shutdown, then closes/disposes preview windows | `SharedOperation` coalesces competing requests; `OperationCounter` keeps controls disabled through nested cleanup. |
| `Engine/Capture.cs > CaptureStream.Start, Run, StopCore` | Worker invokes `ICaptureBackend.Open/Read/Shutdown/Dispose`; `Engine/CaptureBackend.cs > MediaCaptureBackend` is the production implementation | Startup cancellation, one shared stop, bounded driver wait, worker-owned release; synthetic backend covers blocked reads, delayed startup, removal, observer faults and late completion. |
| `Engine/CaptureBackend.cs > Open, Shutdown, Dispose` | Creates a Source Reader with implicit media-source shutdown disabled; one explicit source shutdown completes before COM release | Native Media Foundation calls inspected against Windows SDK interfaces and Microsoft documentation. Physical USB-removal behavior remains unverified. |
| `UI/PreviewWindow.cs > OnClosing, OnClosed` | Awaits `UI/Direct3DHost.cs > StopAsync`; rendering releases Direct3D objects before HWND disposal | Tests close windows repeatedly while producers continue submitting frames. Close handlers yield before calling Close again. |
| `Engine/AudioMonitor.cs > BeginStart, Ready, Stop, Dispose` | Audio initializes on its worker; stops are shared; signal disposal remains owned by the completed worker | Silent WASAPI playback, concurrent stop, late submission and synchronous disposal on the UI thread were checked. |
| `Engine/Recorder.cs > Start, Submit, StopCore` | Detached pending/active encoder drains queued samples and finalizes MP4 | Normal RGB/AAC recording, native/scaled NV12 output, repeated stops, disconnect, close during recording and stale encoder callbacks were checked. |
| `Engine/Capture.cs > Packet`; `Engine/Native.cs > Com.Dispose` | Per-consumer packet leases share one pooled buffer; COM wrappers release their owned reference atomically once | Repeated lease disposal, parallel retention/release, rejection of disposed frames, and concurrent COM-wrapper disposal were checked. |
| `Engine/Lifecycle.cs > RuntimeLifetime`; `App.xaml.cs > OnExit` | Rejects queued media jobs after exit starts and permits global MFShutdown only with no active worker | Idle and active runtime-shutdown ownership cases are covered. |

Dynamic event callbacks are guarded by their session and owning recorder/preview, rather than treated as a complete static call graph. No external application consumers were found or assumed. Real driver crashes, GPU resets, forced process termination and physical cable removal were not tested.

Confirmed issues corrected:

- Exceptions during cleanup could escape async UI handlers.
- An old input/encoder callback could affect a newer session.
- A nested stop could re-enable controls before disconnect finished.
- Stop during startup could leave a continuation opening audio or publishing an encoder after the session ended.
- Source Reader release and explicit source shutdown could compete.
- Repeated stop/dispose could reach an already-disposed signal or native reference.
- Immediate recursive Close could execute while the original Closing event was still active.
- Blocking window teardown could wait on a renderer that needed the UI message pump.

One refactor regression was found during testing: synchronous audio disposal waited on a UI-thread continuation. Library stop continuations now use ConfigureAwait(false); the full UI-thread disposal check passes.

### 🛠️ 2. High-Efficiency Implementation

```csharp
public Task Stop() => stop.Run(StopCore);

sessions.End();
activePreview = null;
await Task.WhenAll(StopSources(), StopRecording());
await playHost.StopAsync();
await playWindow.CloseAsync();
```

### 🛡️ 3. Verification & Validation Report

**Blast-Radius Check:** Release compilation passed with zero warnings and errors. The complete built-in self-test passed. Results are in `artifacts/disconnect-full-check/results.json`; the final standalone executable also passed the complete suite before Desktop delivery (`artifacts/disconnect-final-package-check/results.json`).

- 64 capture start/stop cycles, each issuing 16 competing stop requests.
- 12 preview close cycles while a producer continued submitting frames.
- 4 repeated idle-window close cycles.
- 3 silent audio playback/concurrent-stop cycles.
- Stop-before-start, disconnect-during-startup, close-before-audio-start and simulated hot unplug.
- A stalled driver timed out without releasing resources still owned by its worker; late completion released them once.
- Reconnect rejected old frame and failure callbacks. Publication and session retirement share a gate, including in-flight audio and video callbacks; that serialization check passed.
- Input failure during encoder startup left no active recording.
- A retired encoder callback did not stop a newer recording.
- Recordings finalized after disconnect and window close, and their video samples and timestamps could be read back.
- 90 synthetic video frames and 141 AAC packets round-tripped; no frames were dropped in that encoding check.
- GPU color, orientation, range, letterboxing, scaling, snapshots, and native/scaled NV12 recording checks passed.
- The presentation test rendered all 181 submitted frames at approximately 60.3 fps, including 18 frames during a 300 ms UI pause. This is a synthetic presentation test, not a button-to-screen latency measurement.
- No pooled frame buffers or Media Foundation workers remained after the tests.

No new application package dependencies, external service permissions, uploads or security-setting changes were introduced. Preferences are not loaded or written by the synthetic MainWindow tests.

No regressions were observed in these checks. This does not guarantee that every capture-card driver or physical disconnect behaves correctly. A manual hot-unplug test with the console sending video is still needed; an OS/driver failure or forced process exit can still prevent complete recording finalization.

**Token Efficiency Metric:** Context-line accounting was not measured. No token-saving or exhaustive-coverage claim is made.

Primary references: [Media-source shutdown ownership](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-source-reader-disconnect-mediasource-on-shutdown), [DXGI thread/message-pump considerations](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/dxgi-best-practices).

