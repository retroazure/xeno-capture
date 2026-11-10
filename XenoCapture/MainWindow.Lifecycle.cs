using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using XenoCapture.Engine;
using XenoCapture.UI;

namespace XenoCapture;
public partial class MainWindow
{
    readonly OperationCounter operations = new();
    readonly SessionLifetime sessions = new();
    readonly SharedOperation disconnect = new(true), recordingStop = new(true), connectionStart = new(true), monitorStop = new(true);
    Recorder? pendingRecorder;
    CapturePowerRequest? capturePower;
    AudioMonitor? pendingMonitor;
    int monitorRequest;
    bool busy => operations.IsBusy || closing;
    IDisposable BusyScope()
    {
        var scope = operations.Enter(); SetControls();
        return new ActionScope(() => { scope.Dispose(); if (!mayClose) SetControls(); });
    }
    async Task RunUi(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing && !mayClose) SetStatus(FriendlyError(ex), true); }
    }
    void PostUi(Func<Task> action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try { Dispatcher.BeginInvoke(new Action(async () => { if (!closing && !mayClose) await RunUi(action); })); }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { }
    }
    async Task StopOwned(CaptureStream? input)
    { if (input != null) { try { await input.Stop(); } catch (Exception ex) { if (!closing) SetStatus("Input cleanup: " + FriendlyError(ex), true); } } }
    void KeepCaptureAwake()
    {
        if (capturePower != null) return;
        try { capturePower = new CapturePowerRequest(); }
        catch (Win32Exception error)
        {
            SetStatus("Video is active, but Windows could not keep the display awake. " + error.Message, true);
        }
    }
    Task Disconnect() => disconnect.Run(DisconnectCore);
    async Task DisconnectCore()
    {
        using var operation = BusyScope();
        sessions.End(); ++modeRequest;
        using var power = capturePower;
        capturePower = null;
        activePreview = null; embeddedPreview.Active = false;
        if (!closing) SetStatus("Disconnecting…");
        var errors = new List<Exception>();
        Task inputsAndRecording = Task.WhenAll(StopSources(), StopRecording());
        lock (frameGate) { latest?.Dispose(); latest = null; }
        Task embeddedStopped = embeddedPreview.StopAsync(releaseRenderer: true);
        _ = fullscreenPreview?.StopAsync(releaseRenderer: true); // The window awaits this stop before closing.
        try { if (fullscreen != null) await fullscreen.CloseAsync(animate: false); } catch (Exception ex) { errors.Add(ex); }
        try { await embeddedStopped; embeddedPreview.Dispose(); } catch (Exception ex) { errors.Add(ex); }
        try { await previewRenderer.ReleaseAsync(); } catch (Exception ex) { errors.Add(ex); }
        fullscreen = null; fullscreenPreview = null; VideoHost.Visibility = Visibility.Collapsed;
        try { await inputsAndRecording; } catch (Exception ex) { errors.Add(ex); }
        if (!closing)
        {
            RestorePreviewAfterPhone();
            embeddedPreview = NewPreview(); VideoHost.Child = embeddedPreview;
        }
        EmptyState.Visibility = Visibility.Visible; DemoBadge.Visibility = Visibility.Collapsed;
        SignalTitle.Text = "Preview"; SignalDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "IdleBrush"); PreviewFormat.Text = "Waiting for a signal";
        lastFrameAt = 0; previousFrames = System.Threading.Interlocked.Read(ref receivedFrames); previousPresented = System.Threading.Interlocked.Read(ref presentedFrames);
        SnapshotButton.IsEnabled = false; LeftMeter.Value = RightMeter.Value = 0; LeftDb.Text = RightDb.Text = "−∞ dB"; Metrics.Text = "— fps shown   ·   — fps input";
        if (!closing) SetStatus(errors.Count == 0 ? "Inputs disconnected." : "Disconnected with a driver error: " + FriendlyError(errors[0]), errors.Count != 0);
    }
    async Task StopSources()
    {
        var v = video; var a = audio; var d = demo;
        video = null; audio = null; demo = null;
        phoneWindowPreview = false;
        if (phoneAudioBackend != null) SetPhoneAudioBackend(null);
        MonitorToggle.IsChecked = false;
        await Task.WhenAll(StopMonitor(), v?.Stop() ?? Task.CompletedTask, a?.Stop() ?? Task.CompletedTask, d?.Stop() ?? Task.CompletedTask);
    }
    Task StopRecording() => recordingStop.Run(StopRecordingCore);
    async Task StopRecordingCore()
    {
        Recorder? old;
        lock (recorderGate) { old = recorder ?? pendingRecorder; recorder = pendingRecorder = null; }
        if (old == null) return;
        using var operation = BusyScope();
        if (!closing) SetStatus("Finishing MP4…");
        try
        {
            await old.Stop();
            if (!closing) SetStatus(old.VideoSamplesWritten > 0 ? $"Saved {System.IO.Path.GetFileName(lastRecording)} · {old.DroppedFrames} frame(s) skipped by the encoder." : "Recording stopped before video arrived; the file may be empty.");
        }
        catch (Exception ex) { if (!closing) SetStatus("Recording error: " + FriendlyError(ex), true); }
        finally { RecordBadge.Visibility = Visibility.Collapsed; }
    }
    Task StopMonitor() => monitorStop.Run(async () =>
    {
        AudioMonitor? old; lock (monitorGate) { old = monitor ?? pendingMonitor; monitor = pendingMonitor = null; }
        if (old != null) await old.Stop();
    });
    async Task StopFailedAudio(int session, CaptureStream input, Exception error)
    {
        if (!sessions.IsCurrent(session) || !ReferenceEquals(audio, input)) return;
        audio = null;
        MonitorToggle.IsChecked = false;
        try { await Task.WhenAll(StopOwned(input), StopMonitor()); }
        catch (Exception cleanup) { error = new AggregateException(error, cleanup); }
        if (sessions.IsCurrent(session) && !closing)
        {
            SetControls();
            SetStatus("Audio input stopped. Video stays connected. " + FriendlyError(error), true);
        }
    }
    void ReportInputFailure(int session, Exception ex) => PostUi(async () =>
    {
        if (!sessions.IsCurrent(session)) return;
        await Disconnect(); if (!closing) SetStatus("Input stopped: " + FriendlyError(ex), true);
    });
    void ReportRecordingFailure(int session, Recorder owner, Exception ex) => PostUi(async () =>
    {
        if (!sessions.IsCurrent(session)) return;
        lock (recorderGate) { if (!ReferenceEquals(recorder, owner) && !ReferenceEquals(pendingRecorder, owner)) return; }
        await StopRecording(); if (!closing) SetStatus("Recording stopped: " + FriendlyError(ex), true);
    });

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
            capturePower?.Dispose();
            capturePower = null;
            metricsTimer.Stop(); activePreview = null;
            lock (frameGate) { latest?.Dispose(); latest = null; }
            try { await embeddedPreview.StopAsync(); embeddedPreview.Dispose(); } catch { }
            mayClose = true; Close();
        }
    }
}
