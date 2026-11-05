using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using XenoCapture.Engine;

namespace XenoCapture;

public partial class MainWindow
{
    async void ToggleRecording(object sender, RoutedEventArgs e) => await RunUi(StartOrStopRecording);
    async Task StartOrStopRecording()
    {
        if (busy || !Connected || phoneWindowPreview) return;
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

    void Snapshot(object sender, RoutedEventArgs e)
    {
        if (phoneWindowPreview) return;
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
}
