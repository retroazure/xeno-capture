using System;
using System.Threading.Tasks;
using System.Windows;
using Windows.Graphics.Capture;
using XenoCapture.Engine;
using XenoCapture.UI;

namespace XenoCapture;

public partial class MainWindow
{
    bool phoneWindowPreview, phoneVolumeUpdating;
    (bool AiEnabled, PreviewUpscaler Upscaler)? previewBeforePhone;
    AndroidUsbCaptureBackend? phoneAudioBackend;
    int? pendingPhoneVolume;

    void SetPhoneAudioBackend(AndroidUsbCaptureBackend? backend)
    {
        phoneAudioBackend = backend;
        pendingPhoneVolume = null;
        PhoneVolume.IsEnabled = false;
        bool showPhoneVolume = backend != null;
        AudioInputTitle.Text = showPhoneVolume ? "Phone audio" : "Audio input";
        PhoneVolumeControls.Visibility = showPhoneVolume ? Visibility.Visible : Visibility.Collapsed;
        AudioDevices.Visibility = MonitorToggle.Visibility = InputAudioMeters.Visibility =
            showPhoneVolume ? Visibility.Collapsed : Visibility.Visible;
    }

    void PhoneVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (phoneAudioBackend == null || !PhoneVolume.IsEnabled) return;
        pendingPhoneVolume = (int)e.NewValue;
        RefreshPhoneVolume();
    }

    void RefreshPhoneVolume()
    {
        var backend = phoneAudioBackend;
        if (backend == null || closing || phoneVolumeUpdating || System.Threading.Interlocked.Read(ref lastFrameAt) == 0)
            return;
        int? requested = pendingPhoneVolume;
        if (PhoneVolume.IsEnabled && requested == null) return;
        _ = RunUi(async () =>
        {
            phoneVolumeUpdating = true;
            try
            {
                float? level = await Task.Run(() => backend.AccessPlaybackVolume(requested / 100f));
                if (level == null || !ReferenceEquals(backend, phoneAudioBackend)) return;
                if (requested == null)
                {
                    // A disabled slider reads the initial level without triggering a volume write.
                    PhoneVolume.Value = Math.Round(level.Value * 100);
                    PhoneVolume.IsEnabled = true;
                }
                else if (pendingPhoneVolume == requested) pendingPhoneVolume = null;
            }
            finally { phoneVolumeUpdating = false; }
        });
    }

    async void OpenPhoneScreen(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        if (busy || Connected) return;
        await connectionStart.Run(ConnectPhoneUsb);
    });

    void ApplyPhonePreviewDefaults()
    {
        previewBeforePhone = (SelectedAiVideo.Enabled, SelectedUpscaler);
        AiVideoToggle.IsChecked = false;
        PreviewUpscaling.SelectedIndex = (int)PreviewUpscaler.Standard;
    }

    void RestorePreviewAfterPhone()
    {
        if (previewBeforePhone is not { } previous) return;
        PreviewUpscaling.SelectedIndex = (int)previous.Upscaler;
        AiVideoToggle.IsChecked = previous.AiEnabled;
        previewBeforePhone = null;
    }

    async Task ConnectPhoneUsb()
    {
        using var operation = BusyScope();
        var dialog = new PhoneScreenDialog(GraphicsCaptureSession.IsSupported()) { Owner = this };
        if (dialog.ShowDialog() != true || closing || sessions.Active) return;
        int session = sessions.Begin();
        bool playAudio = dialog.PlayPhoneAudio;
        var backend = new AndroidUsbCaptureBackend(playAudio: playAudio, profile: dialog.SelectedProfile);
        var input = new CaptureStream(() => backend, TimeSpan.FromSeconds(2));
        if (playAudio) SetPhoneAudioBackend(backend);
        phoneWindowPreview = true;
        ApplyPhonePreviewDefaults();
        video = input;
        input.Data += packet => OnData(session, packet, 0);
        input.Failed += ex => ReportInputFailure(session, ex);
        embeddedPreview.Active = true;
        activePreview = embeddedPreview;
        SignalTitle.Text = "Phone screen";
        SignalDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
        PreviewFormat.Text = playAudio ? "Android USB / phone audio requested" : "Android USB / video only";
        SetStatus("Connect one Android phone with USB debugging on. Unlock it and approve the USB debugging prompt.");
        previewRenderer.Warm();
        try { await input.Start(); }
        catch (Exception ex)
        {
            if (!sessions.IsCurrent(session)) return;
            await Disconnect();
            if (!closing) SetStatus("Could not start phone preview: " + FriendlyError(ex), true);
        }
    }
}
