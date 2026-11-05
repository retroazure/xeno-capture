using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using XenoCapture.Engine;

namespace XenoCapture;

public partial class MainWindow
{
    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XenoCapture", "settings.json");
    static string LegacySettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcCapture", "settings.json");

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
        preferredVideo = settings.Video;
        preferredAudio = settings.Audio;
        OutputResolution.SelectedIndex = Math.Clamp(settings.Resolution, 0, 3);
        Bitrate.SelectedIndex = Math.Clamp(settings.Bitrate, 0, 3);
        Sharpness.Value = Math.Clamp(settings.Sharpness, 0, 1);
        PreviewUpscaling.SelectedIndex = settings.Upscaling is >= 0 and <= (int)PreviewUpscaler.Performance ? settings.Upscaling : 0;
        PreviewFrameGeneration.SelectedIndex = settings.Interpolation is >= 0 and <= 3 ? settings.Interpolation : 0;
        Capped30GameToggle.IsChecked = settings.Capped30Game;
        AiVideoResolution.SelectedIndex = settings.AiResolution switch
        {
            720 => 1,
            1080 => 2,
            1440 => 3,
            2160 => 4,
            _ => 0
        };
        AiVideoQuality.SelectedIndex = settings.AiQuality is >= 1 and <= 4 ? settings.AiQuality - 1 : 2;
        AiVideoToggle.IsChecked = settings.AiEnabled;
        UpdateFolderLabel();
    }
    Preferences CurrentPreferences()
    {
        var aiVideo = SelectedAiVideo;
        return new Preferences(
            Folder: folder,
            Video: (VideoDevices.SelectedItem as CaptureDevice)?.Id,
            Audio: (AudioDevices.SelectedItem as CaptureDevice)?.Id,
            Resolution: OutputResolution.SelectedIndex,
            Bitrate: Bitrate.SelectedIndex,
            Sharpness: Sharpness.Value,
            Upscaling: (int)SelectedUpscaler,
            Interpolation: (int)SelectedFrameGeneration,
            Capped30Game: SelectedCapped30Game,
            AiEnabled: aiVideo.Enabled,
            AiResolution: aiVideo.OutputHeight,
            AiQuality: aiVideo.Quality,
            LightTheme: lightTheme,
            InterfaceSounds: SoundsToggle.IsChecked == true,
            InterfaceMotion: MotionToggle.IsChecked == true,
            FullscreenDisplayDevice: fullscreenDisplayDevice,
            FullscreenEdgeSmoothing: FullscreenEdgeSmoothing.IsChecked == true);
    }

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
}
