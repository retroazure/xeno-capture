using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XenoCapture.Engine;
using XenoCapture.UI;

namespace XenoCapture;

public partial class MainWindow
{
    Direct3DHost NewPreview(bool isFullscreen = false, bool animateFullscreenEntry = false)
    {
        var host = new Direct3DHost(previewRenderer, isFullscreen, animateFullscreenEntry)
        {
            Sharpness = Sharpness.Value,
            Upscaler = SelectedUpscaler,
            FrameGeneration = SelectedFrameGeneration,
            SmoothSourcePacing = SelectedSourcePacing,
            Capped30Game = SelectedCapped30Game,
            AiVideo = SelectedAiVideo,
            EdgeSmoothing = isFullscreen && FullscreenEdgeSmoothing.IsChecked == true
        };

        host.FramePresented += _ => Interlocked.Increment(ref presentedFrames);
        host.Failed += error =>
        {
            if (!discoverHardware) DiagnosticPreviewError = error;
            int session = sessions.Generation;
            PostUi(async () =>
            {
                if (!sessions.IsCurrent(session) || !ReferenceEquals(activePreview, host)) return;
                await Disconnect();
                if (!closing) SetStatus("Direct3D preview failed: " + FriendlyError(error), true);
            });
        };
        host.ExitRequested += () =>
        {
            int session = sessions.Generation;
            PostUi(async () =>
            {
                if (sessions.IsCurrent(session) && ReferenceEquals(activePreview, host))
                    await ToggleFullscreen();
            });
        };
        return host;
    }
    void UpdateMetrics(object? sender, EventArgs e)
    {
        RefreshPhoneVolume();
        UpdateUpscalingStatus();
        long now = Clock.Now;
        double seconds = Math.Max(.001, (now - previousMetricTime) / 10_000_000.0);
        previousMetricTime = now;

        long count = Interlocked.Read(ref receivedFrames);
        double fps = (count - previousFrames) / seconds;
        previousFrames = count;
        double age = lastFrameAt == 0 ? 0 : Math.Max(0, (Clock.Now - lastFrameAt) / 10000.0);

        long shown = Interlocked.Read(ref presentedFrames);
        double displayFps = (shown - previousPresented) / seconds;
        previousPresented = shown;

        var metricHost = activePreview;
        long generated = metricHost?.GeneratedPresentations ?? 0;
        double generatedFps = ReferenceEquals(metricHost, previousMetricHost)
            ? Math.Max(0, generated - previousGenerated) / seconds : 0;
        previousMetricHost = metricHost;
        previousGenerated = generated;
        Metrics.Text = Connected ? $"{displayFps:0} fps shown  ·  {generatedFps:0} generated  ·  {fps:0} input" : "— fps shown  ·  — fps input";

        if (Connected && !phoneWindowPreview && lastFrameAt > 0 && age > 2500)
            SetStatus("No recent video frames. Check the HDMI signal and input device.", true);
        double l = Clock.Now - Interlocked.Read(ref lastAudioAt) < 1_000_000 ? Volatile.Read(ref peakL) : 0;
        double r = Clock.Now - Interlocked.Read(ref lastAudioAt) < 1_000_000 ? Volatile.Read(ref peakR) : 0;
        LeftMeter.Value = Meter(l); RightMeter.Value = Meter(r); LeftDb.Text = Db(l); RightDb.Text = Db(r);
        if (recorder != null) { var elapsed = TimeSpan.FromTicks(Clock.Now - recordedAt); RecordTime.Text = $"● REC  {elapsed:hh\\:mm\\:ss}"; }
    }
    static double Meter(double v) => v < 0.001 ? 0 : Math.Clamp((20 * Math.Log10(v) + 60) / 60, 0, 1);
    static string Db(double v) => v < 0.001 ? "−∞ dB" : $"{20 * Math.Log10(v):0} dB";

    FrameGenerationMode SelectedFrameGeneration =>
        PreviewFrameGeneration?.SelectedIndex is >= 0 and <= (int)FrameGenerationMode.RecoverGaps
            ? (FrameGenerationMode)PreviewFrameGeneration.SelectedIndex : FrameGenerationMode.Off;
    void FrameGenerationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (embeddedPreview != null) embeddedPreview.FrameGeneration = SelectedFrameGeneration;
        if (fullscreenPreview != null) fullscreenPreview.FrameGeneration = SelectedFrameGeneration;
        UpdateUpscalingStatus(); if (IsLoaded) SaveSettings();
    }
    bool SelectedSourcePacing => SourcePacingToggle?.IsChecked == true;
    void SourcePacingChanged(object sender, RoutedEventArgs e)
    {
        if (embeddedPreview != null) embeddedPreview.SmoothSourcePacing = SelectedSourcePacing;
        if (fullscreenPreview != null) fullscreenPreview.SmoothSourcePacing = SelectedSourcePacing;
        UpdateUpscalingStatus(); if (IsLoaded) SaveSettings();
    }
    bool SelectedCapped30Game => Capped30GameToggle?.IsChecked==true;
    void Capped30GameChanged(object sender,RoutedEventArgs e)
    {
        if (embeddedPreview!=null) embeddedPreview.Capped30Game=SelectedCapped30Game;
        if (fullscreenPreview!=null) fullscreenPreview.Capped30Game=SelectedCapped30Game;
        UpdateUpscalingStatus(); if (IsLoaded) SaveSettings();
    }
    AiVideoSettings SelectedAiVideo
    {
        get
        {
            int height = AiVideoResolution?.SelectedIndex switch
            {
                1 => 720,
                2 => 1080,
                3 => 1440,
                4 => 2160,
                _ => 0
            };
            int quality = AiVideoQuality?.SelectedIndex is >= 0 and <= 3
                ? AiVideoQuality.SelectedIndex + 1 : 3;
            return new(AiVideoToggle?.IsChecked == true, height, quality);
        }
    }
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
    { 1 => PreviewUpscaler.Enhanced, 2 => PreviewUpscaler.RtxVideo, 3 => PreviewUpscaler.Performance, _ => PreviewUpscaler.Standard };
    void UpscalingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (embeddedPreview != null) embeddedPreview.Upscaler = SelectedUpscaler;
        if (fullscreenPreview != null) fullscreenPreview.Upscaler = SelectedUpscaler;
        UpdateUpscalingStatus(); if (IsLoaded) SaveSettings();
    }
    void UpdateUpscalingStatus()
    {
        if (FrameGenerationHint != null) FrameGenerationHint.Text = SelectedFrameGeneration == FrameGenerationMode.Off
            ? (SelectedSourcePacing ? "Off · display sync enabled" : "Off · lowest preview delay")
            : activePreview?.FrameGenerationStatus ?? FrameGenerationPolicy.WaitingStatus(SelectedFrameGeneration);
        bool aiEnabled=SelectedAiVideo.Enabled;
        if(AiVideoHint!=null)AiVideoHint.Text=aiEnabled ? !Connected?"Waiting for video · AI preview only":activePreview?.AiVideoStatus??"Waiting for video · AI preview only":"Off · uses the selected preview filter";
        bool performance = SelectedUpscaler == PreviewUpscaler.Performance && !aiEnabled;
        if (Sharpness != null) Sharpness.IsEnabled = !performance;
        if (FullscreenEdgeSmoothing != null) FullscreenEdgeSmoothing.IsEnabled = !performance;
        if (EnhancementStatus == null) return;
        if(aiEnabled)
        {
            EnhancementStatus.Text=!Connected?"AI VIDEO · WAITING FOR INPUT":activePreview?.AiVideoActive==true ? activePreview.AiVideoStatus.ToUpperInvariant():activePreview?.AiVideoFallbackReason!=null?"AI VIDEO · FALLBACK":"AI VIDEO · PREPARING";
            EnhancementStatus.ToolTip=activePreview?.AiVideoFallbackReason??"NVIDIA video AI processing at the selected resolution. Higher quality adds GPU work. Higher-resolution sources keep their size. Recording is unchanged.";
            if(RtxVideoHint!=null)RtxVideoHint.Visibility=Visibility.Collapsed;return;
        }
        string? fallback = activePreview?.UpscalingFallbackReason;
        bool rtx = SelectedUpscaler == PreviewUpscaler.RtxVideo;
        bool enhanced = activePreview?.EnhancedUpscalingActive == true;
        EnhancementStatus.Text = SelectedUpscaler switch
        {
            PreviewUpscaler.Standard => "STANDARD · LATEST FRAME",
            PreviewUpscaler.Performance => "PERFORMANCE · LOWEST GPU LOAD",
            PreviewUpscaler.RtxVideo when fallback != null => enhanced
                ? "NORMAL · RTX VIDEO UNAVAILABLE" : "STANDARD · RTX VIDEO UNAVAILABLE",
            PreviewUpscaler.RtxVideo => activePreview?.RtxVideoRequested == true
                ? "RTX VIDEO · AI ACTIVE" : "RTX VIDEO · READY TO UPSCALE",
            _ when fallback != null => "STANDARD · NORMAL UNAVAILABLE",
            _ => enhanced ? "NORMAL · ADAPTIVE UPSCALING" : "NORMAL · READY TO UPSCALE"
        };
        EnhancementStatus.ToolTip = performance ? "Basic scaling with preview sharpening and fullscreen edge smoothing bypassed."
            : fallback ?? (rtx ? "Direct NVIDIA RTX Video SDK Super Resolution. AI Active confirms successful model evaluation. Requires a supported NVIDIA RTX GPU and an enlarged preview. Adds GPU work; the model initializes on first use."
            : "Normal retains NVIDIA Image Scaling when fitting lower-resolution input to your screen. Standard retains fast scaling.");
        if (RtxVideoHint != null) { RtxVideoHint.Visibility = rtx ? Visibility.Visible : Visibility.Collapsed; RtxVideoHint.Text = fallback ?? "Direct NVIDIA AI upscaling runs when the preview is enlarged. AI Active confirms model evaluation. Adds GPU work; first use initializes the model."; }
    }
    void SharpnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (embeddedPreview != null) embeddedPreview.Sharpness = e.NewValue;
        if (fullscreenPreview != null) fullscreenPreview.Sharpness = e.NewValue;
        if (SharpnessValue != null) SharpnessValue.Text = $"{e.NewValue:P0}";
    }
}
