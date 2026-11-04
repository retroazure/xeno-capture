using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XenoCapture.Engine;
using XenoCapture.UI;
using Microsoft.Win32;

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
    long previousMetricTime=Clock.Now,previousGenerated;Direct3DHost? previousMetricHost;
    bool closing, mayClose,fullscreenTransition;
    int modeRequest;
    long receivedFrames, previousFrames, recordedAt;
    double peakL, peakR;
    long lastAudioAt;
    long lastFrameAt;
    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Xeno Capture");
    string? lastRecording;
    string? preferredVideo, preferredAudio;
    readonly DispatcherTimer metricsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XenoCapture", "settings.json");
    static string LegacySettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcCapture", "settings.json");
    bool Connected => video != null || demo != null;
    readonly bool discoverHardware;
    readonly Func<CaptureDevice, VideoMode?, CaptureStream> captureFactory;
    public MainWindow(bool useDemo) : this(useDemo, true, null) { }
    internal MainWindow(bool useDemo, bool discoverHardware, Func<CaptureDevice, VideoMode?, CaptureStream>? factory)
    {
        this.discoverHardware = discoverHardware; captureFactory = factory ?? ((device, mode) => new CaptureStream(device, mode, nativeVideo: true));
        InitializeComponent();
        VersionLabel.Text = "v" + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        ApplyTheme(false);
        if (discoverHardware) LoadSettings();
        RefreshFullscreenDisplays();
        InitializeFeedback();
        embeddedPreview = NewPreview(); VideoHost.Child = embeddedPreview;
        embeddedPreview.Active = true; activePreview = embeddedPreview;
        UpdateUpscalingStatus();
        metricsTimer.Tick += UpdateMetrics; metricsTimer.Start();
        Loaded += async (_, _) => { if (discoverHardware) await FindDevices(); if (useDemo && !closing) await StartDemo(); };
    }
    void SetStatus(string message, bool error = false)
    {
        if (mayClose) return;
        Status.Text = message;
        Status.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "Muted");
    }
    async void RefreshDevices(object sender, RoutedEventArgs e) => await FindDevices();
    async Task FindDevices()
    {
        if (busy || Connected) return;
        using var operation = BusyScope();
        try
        {
            var devices = await Task.Run(() => (CaptureDevices.List(false), CaptureDevices.List(true)));
            if (closing) return;
            VideoDevices.ItemsSource = devices.Item1;
            AudioDevices.Items.Clear(); AudioDevices.Items.Add("No audio · choose an input");
            foreach (var d in devices.Item2) AudioDevices.Items.Add(d);
            AudioDevices.SelectedItem = devices.Item2.FirstOrDefault(d => d.Id == preferredAudio);
            if (AudioDevices.SelectedItem == null) AudioDevices.SelectedIndex = 0;
            VideoDevices.SelectedItem = devices.Item1.FirstOrDefault(d => d.Id == preferredVideo) ?? devices.Item1.FirstOrDefault();
            InputHint.Text = devices.Item1.Count == 0 ? "No video devices found. Plug in a capture card, then refresh." : "Use a USB 3 port and a progressive format for best performance.";
            SetStatus(devices.Item1.Count == 0 ? "No capture card detected. You can try the generated test signal." : $"{devices.Item1.Count} video and {devices.Item2.Count} audio input(s) available.");
        }
        catch (Exception ex) { SetStatus("Device scan failed: " + FriendlyError(ex), true); }
        finally { SetControls(); }
    }
    async void VideoDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!discoverHardware || closing) return;
        int request = ++modeRequest; VideoModes.ItemsSource = null;
        if (VideoDevices.SelectedItem is not CaptureDevice selected) return;
        VideoModes.IsEnabled = false;
        if (preferredAudio == null && AudioDevices.SelectedItem is not CaptureDevice)
        {
            var cardAudio = AudioDevices.Items.OfType<CaptureDevice>().FirstOrDefault(a => a.Name.Contains(selected.Name, StringComparison.OrdinalIgnoreCase));
            if (cardAudio != null) AudioDevices.SelectedItem = cardAudio;
        }
        try
        {
            var modes = await Task.Run(() => CaptureDevices.Modes(selected));
            if (request != modeRequest || closing) return;
            VideoModes.ItemsSource = modes;
            VideoModes.SelectedItem = modes.Where(m => m.Width == 1920 && m.Height == 1080 && m.Fps <= 61).OrderByDescending(m => m.Fps).FirstOrDefault()
                ?? modes.Where(m => m.Width <= 1920 && m.Fps <= 61).FirstOrDefault() ?? modes.FirstOrDefault();
            if (modes.Count == 0) SetStatus("This device did not report any supported video formats.", true);
        }
        catch (Exception ex) { if (request == modeRequest) SetStatus("Cannot read device formats: " + FriendlyError(ex), true); }
        finally { if (request == modeRequest) { VideoModes.IsEnabled = !Connected; SetControls(); } }
    }
    async void ToggleCapture(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        if (busy) return;
        if (Connected)
        {
            await Disconnect();
            PlayCue(InterfaceCue.Disconnected);
            return;
        }
        await connectionStart.Run(ConnectSelected);
    });
    const string WaitingForHdmi = "Waiting for HDMI video.";
    async Task ConnectSelected()
    {
        if (closing || sessions.Active) return;
        if (VideoDevices.SelectedItem is not CaptureDevice device || VideoModes.SelectedItem is not VideoMode mode) { SetStatus("Select your capture card and a 60 fps capture format first.", true); return; }
        using var operation = BusyScope(); int session = sessions.Begin();
        embeddedPreview.Active = true; activePreview = embeddedPreview; SetStatus("Connecting to your selected inputs…");
        CaptureStream? ownedVideo = null, ownedAudio = null;
        try
        {
            ownedVideo = captureFactory(device, mode); video = ownedVideo;
            ownedVideo.Data += packet => OnData(session, packet, 2);
            ownedVideo.Failed += ex => ReportInputFailure(session, ex);
            await ownedVideo.Start();
            if (!sessions.IsCurrent(session) || closing) return;
            if (AudioDevices.SelectedItem is CaptureDevice ad)
            {
                ownedAudio = captureFactory(ad, null); audio = ownedAudio;
                var input = ownedAudio;
                input.Data += packet => OnData(session, packet, input.AudioChannels);
                input.Failed += ex => PostUi(() => StopFailedAudio(session, input, ex));
                try { await input.Start(); }
                catch (Exception ex)
                {
                    await StopFailedAudio(session, input, ex);
                    ownedAudio = null;
                }
            }
            if (!sessions.IsCurrent(session) || closing) return;
            SignalTitle.Text = "Live input"; SignalDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
            PreviewFormat.Text = $"{mode.Width} × {mode.Height}  /  {mode.Fps:0.##} FPS  /  SDR";
            if (audio != null || AudioDevices.SelectedItem is not CaptureDevice)
                SetStatus(Interlocked.Read(ref lastFrameAt) != 0 ? "Input connected." : WaitingForHdmi);
            SaveSettings();
            if (audio != null && AudioDevices.SelectedItem is CaptureDevice selectedAudio && selectedAudio.Name.Contains(device.Name, StringComparison.OrdinalIgnoreCase)) MonitorToggle.IsChecked = true;

        }
        catch (Exception ex)
        {
            if (sessions.IsCurrent(session)) { await Disconnect(); if (!closing) SetStatus("Could not connect: " + FriendlyError(ex), true); }
        }
        finally
        {
            if (!sessions.IsCurrent(session)) { await Task.WhenAll(StopOwned(ownedVideo), StopOwned(ownedAudio)); }
            SetControls();
        }
    }    async void ToggleDemo(object sender, RoutedEventArgs e) => await RunUi(async () => { if (!busy && !Connected) await StartDemo(); });
    async Task StartDemo()
    {
        if (Connected || busy) return;
        embeddedPreview.Active = true; activePreview = embeddedPreview;
        int session = sessions.Begin(); demo = new TestSignal(); demo.Data += packet => OnData(session, packet, 2); demo.Start();
        DemoBadge.Visibility = Visibility.Visible; SignalTitle.Text = "Test signal"; SignalDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
        PreviewFormat.Text = "1280 × 720  /  30 FPS  /  GENERATED";
        SetStatus("Generated video + a quiet 440 Hz test tone. No hardware input is active."); SetControls(); await Task.CompletedTask;
    }
    void OnData(int session, Packet packet, int channels)
    {
        if (!sessions.IsCurrent(session)) return;
        double l = 0, r = 0;
        if (packet.Audio)
        {
            channels = Math.Clamp(channels, 1, 2);
            for (int i = 0; i + channels * 2 <= packet.Length; i += channels * 2)
            {
                short a = (short)(packet.Bytes[i] | packet.Bytes[i + 1] << 8); l = Math.Max(l, Math.Abs((double)a) / 32768);
                short b = channels == 1 ? a : (short)(packet.Bytes[i + 2] | packet.Bytes[i + 3] << 8); r = Math.Max(r, Math.Abs((double)b) / 32768);
            }
        }
        // Session retirement and publication share a short gate. No native
        // encoding, rendering or driver shutdown runs while this gate is held.
        sessions.TryUse(session, () =>
        {
            if (packet.Audio)
            {
                Volatile.Write(ref peakL, l); Volatile.Write(ref peakR, r); Interlocked.Exchange(ref lastAudioAt, Clock.Now);
                try { lock (monitorGate) monitor?.Submit(packet); }
                catch (Exception ex) { PostUi(async () => { if (!sessions.IsCurrent(session)) return; MonitorToggle.IsChecked = false; await StopMonitor(); SetStatus("Audio monitoring stopped: " + FriendlyError(ex), true); }); }
            }
            else
            {
                Interlocked.Increment(ref receivedFrames);
                bool firstFrame = Interlocked.Exchange(ref lastFrameAt, packet.Time) == 0;
                lock (frameGate) { latest?.Dispose(); latest = packet.Retain(); }
                activePreview?.Submit(packet);
                if (firstFrame) PostUi(() =>
                {
                    if (sessions.IsCurrent(session))
                    {
                        EmptyState.Visibility = Visibility.Collapsed;
                        SnapshotButton.IsEnabled = true;
                        if (fullscreen == null) VideoHost.Visibility = Visibility.Visible;
                        if (Status.Text == WaitingForHdmi) SetStatus("Input connected.");
                        AnimateFeedback(SignalDot);
                        PlayCue(InterfaceCue.Connected);
                        SetControls();
                    }
                    return Task.CompletedTask;
                });
            }
            lock (recorderGate) recorder?.Submit(packet);
        });
    }    Direct3DHost NewPreview(bool paceSourceFrames = false, bool animateFullscreenEntry = false)
    {
        var host = new Direct3DHost(previewRenderer, paceSourceFrames, animateFullscreenEntry) { Sharpness = Sharpness.Value, Upscaler = SelectedUpscaler, FrameGeneration = SelectedFrameGeneration, Capped30Game = SelectedCapped30Game, AiVideo=SelectedAiVideo };
        host.EdgeSmoothing = paceSourceFrames && FullscreenEdgeSmoothing.IsChecked == true;
        host.FramePresented += _ => Interlocked.Increment(ref presentedFrames);
        host.Failed += ex => { if(!discoverHardware)DiagnosticPreviewError=ex;int session = sessions.Generation; PostUi(async () => { if (!sessions.IsCurrent(session) || !ReferenceEquals(activePreview, host)) return; await Disconnect(); if (!closing) SetStatus("Direct3D preview failed: " + FriendlyError(ex), true); }); };
        host.ExitRequested += () => { int session = sessions.Generation; PostUi(async () => { if (sessions.IsCurrent(session) && ReferenceEquals(activePreview, host)) { await ToggleFullscreen(); } }); };
        return host;
    }
    void UpdateMetrics(object? sender, EventArgs e)
    {
        UpdateUpscalingStatus();
        long now=Clock.Now;double seconds=Math.Max(.001,(now-previousMetricTime)/10_000_000.0);previousMetricTime=now;
        long count = Interlocked.Read(ref receivedFrames); double fps = (count - previousFrames) / seconds; previousFrames = count;
        double age = lastFrameAt == 0 ? 0 : Math.Max(0, (Clock.Now - lastFrameAt) / 10000.0);
        long shown = Interlocked.Read(ref presentedFrames); double displayFps = (shown - previousPresented) / seconds; previousPresented = shown;
        var metricHost=activePreview;long generated=metricHost?.GeneratedPresentations??0;
        double generatedFps=ReferenceEquals(metricHost,previousMetricHost) ? Math.Max(0,generated-previousGenerated)/seconds:0;
        previousMetricHost=metricHost;previousGenerated=generated;
        Metrics.Text = Connected ? $"{displayFps:0} fps shown  ·  {generatedFps:0} generated  ·  {fps:0} input" : "— fps shown  ·  — fps input";

        if (Connected && lastFrameAt > 0 && age > 2500) SetStatus("No recent video frames. Check the HDMI signal and input device.", true);
        double l = Clock.Now - Interlocked.Read(ref lastAudioAt) < 1_000_000 ? Volatile.Read(ref peakL) : 0;
        double r = Clock.Now - Interlocked.Read(ref lastAudioAt) < 1_000_000 ? Volatile.Read(ref peakR) : 0;
        LeftMeter.Value = Meter(l); RightMeter.Value = Meter(r); LeftDb.Text = Db(l); RightDb.Text = Db(r);
        if (recorder != null) { var elapsed = TimeSpan.FromTicks(Clock.Now - recordedAt); RecordTime.Text = $"● REC  {elapsed:hh\\:mm\\:ss}"; }
    }
    static double Meter(double v) => v < 0.001 ? 0 : Math.Clamp((20 * Math.Log10(v) + 60) / 60, 0, 1);
    static string Db(double v) => v < 0.001 ? "−∞ dB" : $"{20 * Math.Log10(v):0} dB";
    async void ToggleRecording(object sender, RoutedEventArgs e) => await RunUi(StartOrStopRecording);
    async Task StartOrStopRecording()
    {
        if (busy || !Connected) return;
        if (recorder != null) { await StopRecording(); return; }
        int session = sessions.Generation;
        int sourceWidth, sourceHeight; PixelLayout sourceLayout;
        lock (frameGate) { sourceWidth = latest?.Width ?? 0; sourceHeight = latest?.Height ?? 0; sourceLayout = latest?.Layout ?? PixelLayout.Bgr32; }
        if (sourceWidth == 0) { SetStatus("Wait for the first video frame before recording.", true); return; }
        using var operation = BusyScope(); Recorder? starting = null;
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "XENO_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture) + ".mp4");
            int height = int.Parse((string)((ComboBoxItem)OutputResolution.SelectedItem).Tag, CultureInfo.InvariantCulture);
            int width = height switch { 1080 => 1920, 1440 => 2560, 2160 => 3840, _ => sourceWidth };
            if (height == 0) height = sourceHeight;
            var mode = VideoModes.SelectedItem as VideoMode;
            var options = new RecordingOptions(path, sourceWidth, sourceHeight, width, height, demo != null ? 30 : mode?.Numerator ?? 30, demo != null ? 1 : mode?.Denominator ?? 1,
                int.Parse((string)((ComboBoxItem)Bitrate.SelectedItem).Tag, CultureInfo.InvariantCulture) * 1_000_000, demo != null ? 48000 : audio?.AudioRate ?? 0, demo != null ? 2 : audio?.AudioChannels ?? 0, sourceLayout);
            var next = starting = new Recorder(options); lock (recorderGate) pendingRecorder = next; next.Failed += ex => ReportRecordingFailure(session, next, ex);
            if (discoverHardware) await interfaceSounds.SilenceAsync();
            if (!sessions.IsCurrent(session) || closing) { await next.Stop(); return; }
            try { await next.Start(); } catch { try { await next.Stop(); } catch { } throw; }
            if (!sessions.IsCurrent(session) || closing) { await next.Stop(); return; }
            lock (recorderGate) { recorder = next; pendingRecorder = null; }
            recordedAt = Clock.Now; lastRecording = path; RecordBadge.Visibility = Visibility.Visible;
            AnimateFeedback(RecordBadge);
            SetStatus($"Recording {width} × {height} MP4 · H.264{(options.AudioRate > 0 ? " + AAC audio" : " · no audio input")}. Saved locally.");
        }
        catch (Exception ex) { if (!closing && sessions.IsCurrent(session)) SetStatus("Recording could not start: " + FriendlyError(ex) + " Try Match input and a lower bitrate.", true); }
        finally { lock (recorderGate) { if (ReferenceEquals(pendingRecorder, starting)) pendingRecorder = null; } SetControls(); }
    }
    void SetControls()
    {
        if (closing || mayClose) return;
        bool active = Connected && sessions.Active;
        VideoDevices.IsEnabled = AudioDevices.IsEnabled = RefreshButton.IsEnabled = !busy && !active;
        VideoModes.IsEnabled = !busy && !active && VideoModes.Items.Count > 0;
        ConnectButton.IsEnabled = !busy && (active || VideoModes.SelectedItem != null);
        ConnectButton.Content = active ? "Disconnect" : "Play";
        RecordButton.IsEnabled = !busy && active;
        RecordButton.Content = recorder == null ? "●  Record" : "■  Stop recording";
        OutputResolution.IsEnabled = Bitrate.IsEnabled = FolderButton.IsEnabled = !busy && recorder == null;
        DemoButton.IsEnabled = !busy;
        FullscreenButton.IsEnabled = active && !busy && Interlocked.Read(ref lastFrameAt) != 0;
        MonitorToggle.IsEnabled = !busy && (audio != null || demo != null);
    }
    async void MonitorChanged(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        if (!IsInitialized) return;
        int request = ++monitorRequest, session = sessions.Generation; bool enable = MonitorToggle.IsChecked == true;
        await StopMonitor();
        if (request != monitorRequest || !enable || !sessions.IsCurrent(session) || closing || MonitorToggle.IsChecked != true) return;
        int rate = demo != null ? 48000 : audio?.AudioRate ?? 0, channels = demo != null ? 2 : audio?.AudioChannels ?? 0;
        if (rate == 0 || channels == 0) { MonitorToggle.IsChecked = false; return; }
        try
        {
            var next = AudioMonitor.BeginStart(rate, channels); lock (monitorGate) pendingMonitor = next;
            try { await next.Ready; } catch { try { await next.Stop(); } catch { } throw; }
            if (request != monitorRequest || !sessions.IsCurrent(session) || closing || MonitorToggle.IsChecked != true) { await next.Stop(); return; }
            lock (monitorGate) { monitor = next; pendingMonitor = null; }
        }
        catch (Exception ex) { MonitorToggle.IsChecked = false; if (!closing) SetStatus("Could not play audio: " + FriendlyError(ex), true); }
    });
    FrameGenerationMode SelectedFrameGeneration => PreviewFrameGeneration?.SelectedIndex switch
    { 1 => FrameGenerationMode.Adaptive2x, 2 => FrameGenerationMode.Adaptive3x, 3 => FrameGenerationMode.Adaptive4x, _ => FrameGenerationMode.Off };
    void FrameGenerationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (embeddedPreview != null) embeddedPreview.FrameGeneration = SelectedFrameGeneration;
        if (fullscreenPreview != null) fullscreenPreview.FrameGeneration = SelectedFrameGeneration;
        UpdateUpscalingStatus(); if (IsLoaded) SaveSettings();
    }
    bool SelectedCapped30Game => Capped30GameToggle?.IsChecked==true;
    void Capped30GameChanged(object sender,RoutedEventArgs e)
    {
        if (embeddedPreview!=null) embeddedPreview.Capped30Game=SelectedCapped30Game;
        if (fullscreenPreview!=null) fullscreenPreview.Capped30Game=SelectedCapped30Game;
        UpdateUpscalingStatus(); if (IsLoaded) SaveSettings();
    }
    AiVideoSettings SelectedAiVideo=>new(AiVideoToggle?.IsChecked==true,AiVideoResolution?.SelectedIndex switch{1=>720,2=>1080,3=>1440,4=>2160,_=>0},AiVideoQuality?.SelectedIndex is >=0 and <=3 ? AiVideoQuality.SelectedIndex+1:3);
    void AiVideoChanged(object sender,RoutedEventArgs e)
    {
        var settings=SelectedAiVideo;
        if(embeddedPreview!=null)embeddedPreview.AiVideo=settings;
        if(fullscreenPreview!=null)fullscreenPreview.AiVideo=settings;
        if(AiVideoControls!=null)AiVideoControls.IsEnabled=settings.Enabled;
        if(PreviewUpscaling!=null)PreviewUpscaling.IsEnabled=!settings.Enabled;
        UpdateUpscalingStatus();if(IsLoaded)SaveSettings();
    }
    PreviewUpscaler SelectedUpscaler => PreviewUpscaling?.SelectedIndex switch
    { 1 => PreviewUpscaler.Enhanced, 2 => PreviewUpscaler.RtxVideo, _ => PreviewUpscaler.Standard };
    void UpscalingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (embeddedPreview != null) embeddedPreview.Upscaler = SelectedUpscaler;
        if (fullscreenPreview != null) fullscreenPreview.Upscaler = SelectedUpscaler;
        UpdateUpscalingStatus(); if (IsLoaded) SaveSettings();
    }
    void UpdateUpscalingStatus()
    {
        if (FrameGenerationHint != null) FrameGenerationHint.Text = SelectedFrameGeneration == FrameGenerationMode.Off ? "Off · lowest preview delay" : activePreview?.FrameGenerationStatus ?? "Waiting for video · adds one frame of delay";
        bool aiEnabled=SelectedAiVideo.Enabled;
        if(AiVideoHint!=null)AiVideoHint.Text=aiEnabled ? !Connected?"Waiting for video · AI preview only":activePreview?.AiVideoStatus??"Waiting for video · AI preview only":"Off · uses the selected preview filter";
        if (EnhancementStatus == null) return;
        if(aiEnabled)
        {
            EnhancementStatus.Text=!Connected?"AI VIDEO · WAITING FOR INPUT":activePreview?.AiVideoActive==true ? activePreview.AiVideoStatus.ToUpperInvariant():activePreview?.AiVideoFallbackReason!=null?"AI VIDEO · FALLBACK":"AI VIDEO · PREPARING";
            EnhancementStatus.ToolTip=activePreview?.AiVideoFallbackReason??"NVIDIA video AI processing at the selected resolution. Higher quality adds GPU work. Higher-resolution sources keep their size. Recording is unchanged.";
            if(RtxVideoHint!=null)RtxVideoHint.Visibility=Visibility.Collapsed;return;
        }
        string? fallback = activePreview?.UpscalingFallbackReason;
        bool rtx = SelectedUpscaler == PreviewUpscaler.RtxVideo;
        EnhancementStatus.Text = SelectedUpscaler == PreviewUpscaler.Standard ? "STANDARD · LATEST FRAME"
            : rtx ? fallback != null ? activePreview?.EnhancedUpscalingActive == true ? "NORMAL · RTX VIDEO UNAVAILABLE" : "STANDARD · RTX VIDEO UNAVAILABLE" : activePreview?.RtxVideoRequested == true ? "RTX VIDEO · AI ACTIVE" : "RTX VIDEO · READY TO UPSCALE"
            : fallback != null ? "STANDARD · NORMAL UNAVAILABLE"
            : activePreview?.EnhancedUpscalingActive == true ? "NORMAL · ADAPTIVE UPSCALING" : "NORMAL · READY TO UPSCALE";
        EnhancementStatus.ToolTip = fallback ?? (rtx ? "Direct NVIDIA RTX Video SDK Super Resolution. AI Active confirms successful model evaluation. Requires a supported NVIDIA RTX GPU and an enlarged preview. Adds GPU work; the model initializes on first use."
            : "Normal retains NVIDIA Image Scaling when fitting lower-resolution input to your screen. Standard retains fast scaling.");
        if (RtxVideoHint != null) { RtxVideoHint.Visibility = rtx ? Visibility.Visible : Visibility.Collapsed; RtxVideoHint.Text = fallback ?? "Direct NVIDIA AI upscaling runs when the preview is enlarged. AI Active confirms model evaluation. Adds GPU work; first use initializes the model."; }
    }
    void SharpnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (embeddedPreview != null) embeddedPreview.Sharpness = e.NewValue;
        if (fullscreenPreview != null) fullscreenPreview.Sharpness = e.NewValue;
        if (SharpnessValue != null) SharpnessValue.Text = $"{e.NewValue:P0}";
    }
    async void Fullscreen(object sender, RoutedEventArgs e) => await RunUi(ToggleFullscreen);
    Task ToggleFullscreen() => ToggleFullscreenOnDisplay(discoverHardware ? ResolveFullscreenBounds : null);

    async Task ToggleFullscreenOnDisplay(Func<System.Drawing.Rectangle>? displayBounds)
    {
        if (fullscreenTransition) return;
        fullscreenTransition = true;
        try
        {
        if (fullscreen != null) { await fullscreen.CloseAsync(animate: MotionEnabled); return; }
        if (!Connected || !sessions.Active || closing || Interlocked.Read(ref lastFrameAt) == 0) return;
        PlayCue(InterfaceCue.Tap);
        int session = sessions.Generation;
        await embeddedPreview.SuspendAsync();
        if(!sessions.IsCurrent(session)||closing||fullscreen!=null)return;
        VideoHost.Visibility = Visibility.Collapsed;
        bool animateEntry = MotionEnabled;
        var host = NewPreview(paceSourceFrames: true, animateFullscreenEntry: animateEntry); host.Active = true; fullscreenPreview = host; activePreview = host;
        bool resumed = false;
        void ResumeEmbedded()
        {
            if (resumed || !sessions.IsCurrent(session) || closing) return;
            resumed = true;
            VideoHost.Visibility = Visibility.Visible;
            embeddedPreview.Active = true;
            activePreview = embeddedPreview;
            lock (frameGate) { if (latest != null) embeddedPreview.Submit(latest); }
        }

        async Task ResumeAtLanding(Task skipWait)
        {
            if (!sessions.IsCurrent(session) || closing) return;
            var embedded = embeddedPreview;
            var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Presented(PresentedFrame _) => firstFrame.TrySetResult();
            embedded.FramePresented += Presented;
            try
            {
                ResumeEmbedded();
                // The landing window covers the destination until the new swap chain draws.
                await Task.WhenAny(firstFrame.Task, skipWait, Task.Delay(250));
            }
            finally { embedded.FramePresented -= Presented; }
        }

        var playWindow = new PreviewWindow(host, animateEntry, displayBounds, () => MotionEnabled, EmbeddedPreviewBounds, ResumeAtLanding)
        {
            Title = "Xeno Capture — Fullscreen", Background = Brushes.Black, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Width = 640, Height = 360, ShowActivated = discoverHardware,
            ShowInTaskbar = false, Owner = this
        };
        fullscreen = playWindow;
        playWindow.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F11) { e.Handled = true; await RunUi(ToggleFullscreen); }
        };
        playWindow.Closed += (_, _) =>
        {
            if (!ReferenceEquals(fullscreen, playWindow)) return;
            fullscreenPreview = null; fullscreen = null;
            ResumeEmbedded();
        };
        try { playWindow.Show(); lock (frameGate) { if (latest != null) host.Submit(latest); } }
        catch
        {
            try { await playWindow.CloseAsync(animate: false); }
            finally
            {
                if (ReferenceEquals(fullscreen, playWindow))
                {
                    fullscreen = null; fullscreenPreview = null;
                    await host.StopAsync();
                    host.Dispose();
                    ResumeEmbedded();
                }
            }
            throw;
        }
        }finally{fullscreenTransition=false;}
    }
    async void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.F11) { e.Handled = true; await RunUi(ToggleFullscreen); } }
    void Snapshot(object sender, RoutedEventArgs e)
    {
        Packet? frame; lock (frameGate) { frame = latest?.Retain(); } using var heldFrame = frame; if (heldFrame == null) return;
        var dialog = new SaveFileDialog { Title = "Save input-resolution snapshot", Filter = "PNG image|*.png", FileName = "XENO_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".png", InitialDirectory = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
        if (dialog.ShowDialog(this) != true) return;
        try { var encoder = new PngBitmapEncoder(); using var rgb = PixelConversion.ToBgra(heldFrame); var image = BitmapSource.Create(rgb.Width, rgb.Height, 96, 96, PixelFormats.Bgr32, null, rgb.Bytes, rgb.Width * 4); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(dialog.FileName); encoder.Save(stream); SetStatus("Snapshot saved at input resolution."); }
        catch (Exception ex) { SetStatus("Snapshot failed: " + FriendlyError(ex), true); }
    }
    void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose recording folder", InitialDirectory = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
        if (dialog.ShowDialog(this) == true) { folder = dialog.FolderName; UpdateFolderLabel(); SaveSettings(); }
    }
    void OpenRecordings(object sender, RoutedEventArgs e)
    { try { Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); } catch (Exception ex) { SetStatus("Could not open recordings: " + FriendlyError(ex), true); } }
    void ShowHelp(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Connect an HDMI / USB video device, select a format, and choose its audio endpoint or a microphone. Windows camera and microphone permissions must allow desktop apps.\n\nPreview uses one pending frame. Select Normal under Preview Upscaling to use NVIDIA Image Scaling when fitting lower-resolution video to a 1440p / 4K screen. The Sharpness slider controls the selected preview filter. Standard retains the existing fast preview. Normal uses spatial upscaling. RTX Video Super Resolution runs NVIDIA’s AI video model directly when the preview is enlarged. AI Active confirms successful SDK evaluation. It uses additional GPU resources, initializes on first use, and falls back to Normal when unavailable. Keep the gpu folder beside the executable. The separate AI Video Super Resolution switch defaults to Off and adds explicit Auto, 720p, 1080p, 1440p and 4K processing targets with Performance, Balanced, High and Ultra SDK quality. While enabled, it takes priority over the selected preview upscaler; turning it off restores that selection. It is preview-only and does not change recordings. Higher-resolution inputs keep their size; aspect ratio is preserved. This is video upscaling rather than game-engine DLSS. Optional 2×, 3× and 4× frame generation uses NVIDIA hardware optical flow when available and software motion checks otherwise. Repeated images in a 60 FPS capture stream are detected automatically, so 30 FPS games target 60, 90 or 120 FPS. The Force 30 FPS game cadence override is available when automatic detection is unreliable. The display must support the selected target rate. Generation adds one content frame of buffering and can create interpolation artifacts. Input is limited to 1080p. The FPS display reports presented, actually generated, and input frames separately; static or uncertain frames use fallback. Recording remains at the captured source rate. MP4 output uses its separate resolution settings and high-quality bicubic resizing on the CPU.\n\nRecordings use Windows H.264 / AAC encoders. Hardware encoding is requested when available; GPU use is not guaranteed. 1440p / 4K support depends on the installed encoder. Heavy output settings can skip recording frames without blocking the preview.\n\nThe FPS display separates frames presented by Direct3D, frames with synthesized pixels, and capture input frames. This is not an end-to-end latency measurement. USB, the card and display still add delay.\n\nAudio playback uses event-driven WASAPI with a minimum default shared buffer. Device buffering still adds delay. Use headphones when monitoring a microphone. Audio/video alignment uses host timestamps; long-session drift and device latency require hardware validation.\n\nF11: fullscreen · Esc: exit fullscreen.\n\nSDR, 8-bit video; one video + one audio input. No HDR, VRR, ASIO, streaming, game hooking or protected HDMI input. Nothing is uploaded.",
        "About Xeno Capture", MessageBoxButton.OK, MessageBoxImage.Information);
    void LoadSettings()
    {
        var settings = ReadPreferences(SettingsPath, LegacySettingsPath);
        if (settings != null) ApplyPreferences(settings);
        UpdateFolderLabel();
    }

    static Preferences? ReadPreferences(string current, string legacy)
    {
        try
        {
            using var file = new FileStream(File.Exists(current) ? current : legacy, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 64 * 1024) return null;
            using var reader = new StreamReader(file);
            return JsonSerializer.Deserialize<Preferences>(reader.ReadToEnd());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    static string? RecordingFolderFromPreferences(string? saved)
    {
        if (string.IsNullOrEmpty(saved) || !Path.IsPathFullyQualified(saved)) return null;
        try
        {
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(saved));
            string? parent = Path.GetDirectoryName(normalized);
            return parent != null && string.Equals(Path.GetFileName(normalized), "ARC Capture", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(parent, "Xeno Capture")
                : normalized;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    void ApplyPreferences(Preferences settings)
    {
        ApplyTheme(settings.LightTheme);
        SoundsToggle.IsChecked = settings.InterfaceSounds;
        MotionToggle.IsChecked = settings.InterfaceMotion;
        fullscreenDisplayDevice = settings.FullscreenDisplayDevice;
        FullscreenEdgeSmoothing.IsChecked = settings.FullscreenEdgeSmoothing;
        if (IsLoaded) RefreshFullscreenDisplays();
        folder = RecordingFolderFromPreferences(settings.Folder) ?? folder;
        preferredVideo = settings.Video; preferredAudio = settings.Audio;
        OutputResolution.SelectedIndex = Math.Clamp(settings.Resolution, 0, 3); Bitrate.SelectedIndex = Math.Clamp(settings.Bitrate, 0, 3);
        Sharpness.Value = Math.Clamp(settings.Sharpness, 0, 1); PreviewUpscaling.SelectedIndex = settings.Upscaling is >= 0 and <= 2 ? settings.Upscaling : 0; PreviewFrameGeneration.SelectedIndex = settings.Interpolation is >= 0 and <= 3 ? settings.Interpolation : 0; Capped30GameToggle.IsChecked=settings.Capped30Game;
        AiVideoResolution.SelectedIndex=settings.AiResolution switch{720=>1,1080=>2,1440=>3,2160=>4,_=>0};
        AiVideoQuality.SelectedIndex=settings.AiQuality is >=1 and <=4 ? settings.AiQuality-1:2;AiVideoToggle.IsChecked=settings.AiEnabled;
        UpdateFolderLabel();
    }
    Preferences CurrentPreferences() => new(folder, (VideoDevices.SelectedItem as CaptureDevice)?.Id, (AudioDevices.SelectedItem as CaptureDevice)?.Id, OutputResolution.SelectedIndex, Bitrate.SelectedIndex, Sharpness.Value, (int)SelectedUpscaler, (int)SelectedFrameGeneration, SelectedCapped30Game,SelectedAiVideo.Enabled,SelectedAiVideo.OutputHeight,SelectedAiVideo.Quality,lightTheme,SoundsToggle.IsChecked == true,MotionToggle.IsChecked == true, fullscreenDisplayDevice, FullscreenEdgeSmoothing.IsChecked == true);
    void SaveSettings()
    {
        if (!discoverHardware) return;
        try
        {
            string directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, Path.GetRandomFileName());
            bool created = false;
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    JsonSerializer.Serialize(file, CurrentPreferences());
                }
                File.Move(temporary, SettingsPath, overwrite: true);
                created = false;
            }
            finally { if (created) File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not save Xeno Capture preferences ({0}).", error.GetType().Name);
        }
    }
    void UpdateFolderLabel() { FolderLabel.Text = folder; FolderLabel.ToolTip = folder; }
    sealed record Preferences(string Folder, string? Video, string? Audio, int Resolution, int Bitrate, double Sharpness, int Upscaling = 0, int Interpolation = 0, bool Capped30Game = false, bool AiEnabled=false,int AiResolution=0,int AiQuality=3,bool LightTheme=false,bool InterfaceSounds=true,bool InterfaceMotion=true, string? FullscreenDisplayDevice = null, bool FullscreenEdgeSmoothing = true);
    static string FriendlyError(Exception ex)
    {
        if (ex is COMException ce) return ce.HResult switch
        {
            unchecked((int)0x80070005) => "Access denied. Enable desktop camera/microphone access in Windows Privacy settings.",
            unchecked((int)0xC00D3704) => "The device is in use. Close other apps that are using it.",
            _ => $"{ex.Message} (0x{ce.HResult:X8})"
        };
        return ex.Message;
    }
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (mayClose) return; e.Cancel = true; if (closing) return;
        closing = true; sessions.Close(); ++modeRequest; IsEnabled = false; SaveSettings();
        if (discoverHardware) _ = interfaceSounds.SilenceAsync();
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
        try { await Disconnect(); }
        catch (Exception) { /* Close still releases the remaining managed UI resources below. */ }
        finally
        {
            metricsTimer.Stop(); activePreview = null;
            lock (frameGate) { latest?.Dispose(); latest = null; }
            try { await embeddedPreview.StopAsync(); embeddedPreview.Dispose(); } catch { }
            mayClose = true; Close();
        }
    }
}














