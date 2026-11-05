using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XenoCapture.Engine;
using XenoCapture.UI;

namespace XenoCapture;

public partial class MainWindow
{
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
            if (!DiagnosticDisablePreviewWarmup) previewRenderer.Warm();
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
                        SnapshotButton.IsEnabled = !phoneWindowPreview;
                        if (fullscreen == null) VideoHost.Visibility = Visibility.Visible;
                        if (phoneWindowPreview) SetStatus("Phone screen preview is active. Disconnect to stop it.");
                        else if (Status.Text == WaitingForHdmi) SetStatus("Input connected.");
                        AnimateFeedback(SignalDot);
                        PlayCue(InterfaceCue.Connected);
                        SetControls();
                    }
                    return Task.CompletedTask;
                });
            }
            lock (recorderGate) recorder?.Submit(packet);
        });
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
}
