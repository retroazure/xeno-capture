using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XenoCapture.Engine;
using XenoCapture.UI;

namespace XenoCapture;
public partial class MainWindow : Window
{
    CaptureStream? video, audio;
    TestSignal? demo;
    Recorder? recorder;
    AudioMonitor? monitor;
    readonly object recorderGate = new(), monitorGate = new();
    Packet? latest;
    readonly object frameGate = new();
    readonly PreviewRendererHandoff previewRenderer = new();
    Direct3DHost embeddedPreview = null!;
    Direct3DHost? fullscreenPreview;
    volatile Direct3DHost? activePreview;
    PreviewWindow? fullscreen;
    long presentedFrames, previousPresented;
    long previousMetricTime = Clock.Now, previousGenerated;
    Direct3DHost? previousMetricHost;
    bool closing, mayClose, fullscreenTransition;
    int modeRequest;
    long receivedFrames, previousFrames, recordedAt;
    double peakL, peakR;
    long lastAudioAt;
    long lastFrameAt;
    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Xeno Capture");
    string? lastRecording;
    string? preferredVideo, preferredAudio;
    readonly DispatcherTimer metricsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool Connected => video != null || demo != null;
    readonly bool discoverHardware;
    readonly Func<CaptureDevice, VideoMode?, CaptureStream> captureFactory;
    public MainWindow(bool useDemo) : this(useDemo, true, null) { }
    internal MainWindow(bool useDemo, bool discoverHardware, Func<CaptureDevice, VideoMode?, CaptureStream>? factory)
    {
        this.discoverHardware = discoverHardware;
        captureFactory = factory ?? ((device, mode) => new CaptureStream(device, mode, nativeVideo: true));
        InitializeComponent();
        VersionLabel.Text = "v" + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        ApplyTheme(false);
        if (discoverHardware) LoadSettings();
        RefreshFullscreenDisplays();
        InitializeFeedback();
        embeddedPreview = NewPreview();
        VideoHost.Child = embeddedPreview;
        embeddedPreview.Active = true;
        activePreview = embeddedPreview;
        UpdateUpscalingStatus();
        metricsTimer.Tick += UpdateMetrics;
        metricsTimer.Start();
        Loaded += async (_, _) =>
        {
            if (discoverHardware) await FindDevices();
            if (useDemo && !closing) await StartDemo();
        };
    }
    void SetStatus(string message, bool error = false)
    {
        if (mayClose) return;
        Status.Text = message;
        Status.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "Muted");
    }
    void SetControls()
    {
        if (closing || mayClose) return;
        bool active = Connected && sessions.Active;
        VideoDevices.IsEnabled = AudioDevices.IsEnabled = RefreshButton.IsEnabled = !busy && !active;
        PhoneScreenButton.IsEnabled = !busy && !active;
        VideoModes.IsEnabled = !busy && !active && VideoModes.Items.Count > 0;
        ConnectButton.IsEnabled = !busy && (active || VideoModes.SelectedItem != null);
        ConnectButton.Content = active ? "Disconnect" : "Play";
        RecordButton.IsEnabled = !busy && active && !phoneWindowPreview;
        RecordButton.Content = recorder == null ? "●  Record" : "■  Stop recording";
        OutputResolution.IsEnabled = Bitrate.IsEnabled = FolderButton.IsEnabled = !busy && recorder == null;
        DemoButton.IsEnabled = !busy;
        FullscreenButton.IsEnabled = active && !busy && Interlocked.Read(ref lastFrameAt) != 0;
        MonitorToggle.IsEnabled = !busy && (audio != null || demo != null);
    }

    void ShowHelp(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Connect an HDMI / USB video device, select a format, and choose its audio endpoint or a microphone. " +
        "Windows camera and microphone permissions must allow desktop apps.\n\n" +
        "Preview uses one pending frame. Select Normal under Preview Upscaling to use NVIDIA Image Scaling when " +
        "fitting lower-resolution video to a 1440p / 4K screen. The Sharpness slider controls the selected preview " +
        "filter. Standard retains the existing fast preview. Normal uses spatial upscaling. RTX Video Super " +
        "Resolution runs NVIDIA’s AI video model directly when the preview is enlarged. AI Active confirms " +
        "successful SDK evaluation. It uses additional GPU resources, initializes on first use, and falls back to " +
        "Normal when unavailable. Keep the gpu folder beside the executable. The separate AI Video Super " +
        "Resolution switch defaults to Off and adds explicit Auto, 720p, 1080p, 1440p and 4K processing targets " +
        "with Performance, Balanced, High and Ultra SDK quality. While enabled, it takes priority over the " +
        "selected preview upscaler; turning it off restores that selection. It is preview-only and does not change " +
        "recordings. Higher-resolution inputs keep their size; aspect ratio is preserved. This is video upscaling " +
        "rather than game-engine DLSS. Optional 2×, 3× and 4× frame generation uses NVIDIA hardware optical flow " +
        "when available and software motion checks otherwise. Repeated images in a 60 FPS capture stream are " +
        "detected automatically, so 30 FPS games target 60, 90 or 120 FPS. The Force 30 FPS game cadence override " +
        "is available when automatic detection is unreliable. The display must support the selected target rate. " +
        "Generation adds one content frame of buffering and can create interpolation artifacts. Input is limited " +
        "to 1080p. The FPS display reports presented, actually generated, and input frames separately; static or " +
        "uncertain frames use fallback. Recording remains at the captured source rate. MP4 output uses its " +
        "separate resolution settings and high-quality bicubic resizing on the CPU.\n\n" +
        "Recordings use Windows H.264 / AAC encoders. Hardware encoding is requested when available; GPU use is " +
        "not guaranteed. 1440p / 4K support depends on the installed encoder. Heavy output settings can skip " +
        "recording frames without blocking the preview.\n\n" +
        "The FPS display separates frames presented by Direct3D, frames with synthesized pixels, and capture input " +
        "frames. This is not an end-to-end latency measurement. USB, the card and display still add delay.\n\n" +
        "Audio playback uses event-driven WASAPI with a minimum default shared buffer. Device buffering still adds " +
        "delay. Use headphones when monitoring a microphone. Audio/video alignment uses host timestamps; " +
        "long-session drift and device latency require hardware validation.\n\n" +
        "F11: fullscreen · Esc: exit fullscreen.\n\n" +
        "SDR, 8-bit video; one video + one audio input. No HDR, VRR, ASIO, streaming, game hooking or protected " +
        "HDMI input. Nothing is uploaded.",
        "About Xeno Capture", MessageBoxButton.OK, MessageBoxImage.Information);
}
