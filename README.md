# Xeno Capture 0.9.8

Xeno Capture is an open-source Windows x64 app for viewing and recording HDMI video from a capture card. Play in a window or fullscreen on a monitor you choose, listen to capture audio on your PC, and save local MP4 recordings or PNG snapshots. The desktop app uses WPF and Direct3D 11.

**Frame generation and NVIDIA RTX Video AI upscaling are experimental preview features.** They process the captured image on the PC, are off by default, and leave recordings and snapshots on their existing paths. Use the standard preview to see the source cadence with the least processing.

## Scope

The supported workflow starts with an unprotected SDR HDMI signal and ends with local preview, audio monitoring, or files. A 1080p60 signal with stereo audio is a good starting point for console capture. Console rendering and controller input stay on the console. Game emulation, live streaming, HDR, VRR, and protected HDMI capture are outside the app's scope.

Xeno Capture sees the finished video frames from the capture card. The capture feed supplies no game-engine motion vectors, depth, or separate HUD layers. The optional filters estimate motion and detail from those finished frames, and the actual results depend on the source, GPU, capture card, and display.

See the [Quick Start](QUICK%20START.txt) for setup and the [license section](#license) for source and third-party terms.

## Appearance

Use the appearance button in the top bar to switch between light and dark mode. Xeno Capture saves the selection with its other preferences. On first launch it imports settings from the previous app name when a Xeno settings file is absent.

Use **Feedback** in the top bar to control interface sounds and animations. Both are on by default. Motion follows the Windows animation setting. Entering fullscreen expands the rounded window with a soft rebound. Esc and F11 guide the last prepared image into the embedded preview, including when fullscreen uses another monitor. Interface sounds are paused during recording. A saved default recording folder with the previous app name moves to Xeno Capture under the same parent.

## Experimental AI preview upscaling

**Output → AI Video Super Resolution** is a separate optional filter, **Off by default**. Enable it, choose Auto / 720p / 1080p / **1440p** / **4K**, and select Performance, Balanced, High or Ultra. High is the initial quality choice. The target controls the GPU processing resolution even in a smaller preview window. Aspect ratio is preserved, and an input larger than the target keeps its resolution. The status reports the actual processed size. At native size it can enhance the image without enlarging it. The preview integrates more of the AI output when fitting a large target into a smaller window, and preserves more local contrast when enlarging a smaller AI target.

Both AI upscaling controls use NVIDIA's RTX Video Super Resolution model for finished video frames. NVIDIA DLSS runs inside supported games and needs game-engine inputs outside the HDMI capture feed. [NVIDIA's explanation](https://nvidia.custhelp.com/app/answers/detail/a_id/5448)

While enabled, the separate filter takes priority over the saved preview-upscaler selection. Turning it off restores Standard, Normal or RTX Video. Recordings and audio keep their existing source paths. No new runtime/model download is added. High/Ultra and larger targets cost more GPU time; choose a lower quality if the selected frame-generation rate cannot be sustained. A 4K processing target on a lower-resolution monitor is fitted back to that monitor; it does not change Windows display resolution.

Loaded AI features are reused when target dimensions change. Byte-identical retained capture packets reuse upload and inference output; changed pixels invalidate the cache. Pacing follows packet arrival times rather than GPU completion times. Fullscreen handoff keeps the loaded device and AI feature, then attaches a new presentation surface after the outgoing render thread releases it. Disconnect fences and releases the GPU resources. For Auto AI sizing, fullscreen briefly waits for its initial layout to settle before attaching the presentation surface. First use on a new renderer still initializes the SDK; this is not an end-to-end latency guarantee. See **AI PREVIEW AND PACING REVIEW.md** for measured results and limits.

## Experimental frame generation

Choose **Output → Frame Generation → Adaptive 2×, 3× or 4×**. Supported NVIDIA GPUs use their hardware optical-flow engine; other supported configurations retain the software motion fallback. The app automatically detects repeated images in a 60 FPS feed from a 30 FPS game. Its targets are then 60, 90 and 120 FPS. For 60 FPS content, the corresponding targets are 120, 180 and 240 FPS. Leave **Force 30 FPS game cadence** unchecked for automatic detection; the saved override is available when needed. The screen must support the selected target rate. Input is limited to 1080p for generation.

Frame generation adds one content frame of buffering. For 30 FPS games, NVIDIA optical flow checks whether a motion estimate points to matching pixels in both source frames. The preview can use a single verified warp at an occlusion or hold pixels from the nearer source when the match is uncertain. The hardware path also checks an individual flow vector at rejected high-contrast boundaries in faster captures. It uses the recovered motion only when both endpoints agree. All generation modes hold the previous source frame until a scene cut is due. The shader checks that motion agrees across both endpoints and rejects frame pairs with too much unsupported high-contrast motion. Complex motion can still produce artifacts or repeats. The extra frames can make motion look smoother. Controller response still follows the console and capture path. **Off** shows the latest capture without interpolation. In fullscreen, early source frames can wait up to 2 ms to smooth irregular delivery. Late frames and timing discontinuities go straight to the next available presentation slot. The FPS display separates accepted presentations, frames containing synthesized pixels, and capture input frames. These are software measurements, not physical display scanout or button-to-screen latency.

**Preview Upscaling → RTX Video Super Resolution** runs the same model when enlarging the preview. **AI Active** appears after successful model evaluation. This mode requires a compatible NVIDIA RTX GPU, driver, and matching GPU files. First use initializes the model and can pause output temporarily. **Normal** uses NVIDIA Image Scaling and **Standard** uses fast scaling. Both are non-AI options. If RTX Video is unavailable, the app falls back to Normal, then Standard.

Keep the matching **gpu** folder beside **XenoCapture.exe**. Xeno Capture verifies the bridge and NVIDIA model before loading them and uses the existing preview fallbacks if verification fails. It contains the compiled bridge, NVIDIA's verified model DLL and its license. No separate .NET installation is needed for the standalone package. Frame generation and enhancement apply to preview; recording retains its own source-rate and resolution settings.

Frame generation adapts its scheduling margin to the measured GPU work and skips expired output phases. On high-refresh displays, Adaptive 4× can switch to evenly spaced 3× output when presentation repeatedly misses the 4× rate. The status shows the active rate. Changing generation mode, refresh rate, or the selected upscaler rechecks the rate. Byte-identical capture repeats bypass motion analysis and reuse completed Normal or RTX upscaling output. For advertised NV12 capture modes, preview uploads the native format directly. An incompatible negotiation or first frame falls back to RGB conversion.

## Start playing

1. Connect **Switch 2 dock HDMI OUT → capture card HDMI IN**.
2. Connect the capture card's USB cable to a USB 3 port on your PC. Your monitor stays connected to your PC. The capture card's HDMI OUT does not need to be connected to the monitor.
3. Open **XenoCapture.exe**. Select your capture card and a **1920 × 1080 / 60 fps** format.
4. Select the capture card's HDMI audio input. A matching device is selected automatically when Windows reports the same name. Choose another input manually if needed.
5. Click **Play** to start in the embedded preview. Choose **Output → Fullscreen display** to use a specific monitor, then press **Fullscreen** or **F11**. If the saved display is unavailable, the preview opens on the app's display. Matching capture-card audio plays through the PC's default output automatically. **Listen on this PC** controls this manually.
6. Press **Esc** or **F11** to return to the controls. **Disconnect** releases the inputs. Keep your controller connected to the Switch 2.

Start with a 1080p60 SDR signal and stereo audio from the console. On Switch 2, TV resolution, HDR and 120 Hz output are controlled under System Settings → Display. Use a wired headphone/speaker output if responsiveness matters; Bluetooth introduces its own audio delay.

The generated test signal uses no camera or microphone. Its audio is a quiet 440 Hz tone and is only audible when audio playback is enabled.

## Included features

- Media Foundation video-device and native-format enumeration.
- Separate capture-card/microphone audio input (16-bit PCM, mono/stereo, 44.1 or 48 kHz).
- Latest-frame preview: at most one pending frame instead of a growing playback queue.
- Direct3D 11 display scaling and optional single-pass GPU sharpening. The render thread runs independently of the interface; the hidden preview pauses during fullscreen playback.
- Event-driven WASAPI audio playback using the default shared-mode engine buffer and a bounded queue. No exclusive device lock.
- Borderless fullscreen on the selected monitor, with aspect-preserving scaling. **Smooth jagged edges in fullscreen** is on by default and works at native size and with each upscaler.
- Optional local MP4 recording with H.264 video and AAC audio; selectable 20/40/80/120 Mbps.
- Match-input, 1080p, 1440p and 4K recording sizes, with aspect-preserving bicubic resizing.
- Stereo audio meters, input-resolution PNG snapshots and a local recording folder selector.
- Local preferences under `%LOCALAPPDATA%\XenoCapture\settings.json`. Inputs connect only after you click Play.

## Performance and quality limits

Capture-card buffering, USB, Windows composition, the monitor and audio device add delay. Button-to-screen latency still needs hardware measurement. A 4K processing target produces a larger preview image from the same source. Edge smoothing and AI upscaling can reduce visible jagged edges or improve local contrast, but missing source detail has to be estimated. Synthetic tests check these behaviors on controlled inputs. Live results vary with the capture setup and content.

The app keeps at most one pending capture packet. Optional interpolation keeps two source images and one reusable generated image on the GPU. At 1080p the NVIDIA flow path uses about 29.4 MiB of additional image surfaces, excluding driver overhead. A 4× setting reuses these surfaces. Motion analysis has a processing-budget fallback. VSR adds GPU work and its own surfaces/model resources; combining it with high output rates can be more demanding.

Video is SDR and 8-bit, with one video input and one mono or stereo audio input. Audio monitoring uses shared-mode WASAPI. Recording uses Windows H.264/AAC encoders, requesting hardware encoding when available. Encoder support for high resolutions/frame rates varies. Recording resizing uses the CPU; heavy settings may skip recording frames while preview remains independent. Audio/video timestamps use the host clock; device latency and long-session drift still need hardware validation.

## Build and diagnostics

Install .NET SDK 10.0.403 or later and use `./build.ps1 -Standalone`; the framework-dependent option is `./build.ps1` and requires .NET Desktop Runtime 10.0.14 or later. Copy the executable together with its gpu folder. There are no external application NuGet packages. Publishing may download official .NET runtime packs.

For a source build, use MSVC and the Windows SDK versions configured in `tools/Build-GpuBridge.ps1` and obtain the official [NVIDIA RTX Video SDK v1.1.0](https://developer.nvidia.com/rtx-video-sdk). Before running `build.ps1`, prepare the native files expected by the project:

1. Run `./tools/Build-GpuBridge.ps1 -SdkRoot <sdk-directory> -OutputDirectory ./XenoCapture/Native/bin` from the project root to build the bridge. The bridge links the SDK's NGX library and uses NVIDIA's MIT-licensed Optical Flow headers. The NVIDIA driver supplies `nvofapi64.dll`.
2. Place the SDK's `bin/Windows/x64/rel/nvngx_vsr.dll` and `NVIDIA_RTX_Video_SDK_License.pdf` from the SDK root in `XenoCapture/ThirdParty/RTXVideo`.

Use `tools/Build-FrameShaders.ps1` to rebuild preview and compute bytecode after shader edits.

Synthetic diagnostics do not open a camera/capture card or record a microphone:

- `XenoCapture.exe --ai-video-tests <folder>`: explicit output sizes, quality, exact-frame cache, native/range/fallback, recording independence and six AI fullscreen round trips.
- `XenoCapture.exe --self-test <folder>`: complete encoding, audio, GPU, UI and lifecycle suite.
- `XenoCapture.exe --framegen-tests <folder>`: pixel oracles, hardware/software paths, cadence and 2×/3×/4× UI checks.
- `XenoCapture.exe --capped-game-soak <folder>`: automatic/forced 30 FPS pacing and 20 fullscreen round trips.
- `XenoCapture.exe --rtx-video-tests <folder>`: model-versus-bicubic pixel comparison, RGB/NV12 range, 1440p/4K, fallback and combined generation.

Current AI-filter and pacing evidence is in **AI PREVIEW AND PACING REVIEW.md**; **GPU FEATURES FIX REVIEW.md** describes the preceding 0.7.0 repair. Earlier review documents describe the versions named in those documents and are retained as history.

## License

Xeno Capture's original source code and documentation are licensed under the [MIT License](LICENSE).

NVIDIA components have their own terms. The NVIDIA Image Scaling and Optical Flow headers carry their respective MIT notices. The RTX Video SDK and model are covered by NVIDIA's separate license, included as `gpu/NVIDIA_RTX_Video_SDK_License.pdf` with the build. See [Third-party notices](THIRD-PARTY%20NOTICES.txt).
