using System;
using System.Linq;

using System.Windows.Interop;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using XenoCapture.Engine;

namespace XenoCapture;
// Internal diagnostic entry points invoke the production handlers. Hardware
// discovery and preference writes are disabled by the diagnostic constructor.
public partial class MainWindow
{
    internal Exception? DiagnosticPreviewError {get;private set;}
    internal bool DiagnosticDisablePreviewWarmup { get; set; }
    readonly TaskCompletionSource diagnosticClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void DiagnosticPrepareInput(bool withAudio = false)
    {
        var device = new CaptureDevice("Synthetic video", "diagnostic-video", false);
        VideoDevices.ItemsSource = new[] { device }; VideoDevices.SelectedIndex = 0;
        VideoModes.ItemsSource = new[] { new VideoMode(0, 320, 180, 30, 1, Keys.Rgb32) }; VideoModes.SelectedIndex = 0;
        AudioDevices.Items.Clear(); AudioDevices.Items.Add("No audio"); AudioDevices.SelectedIndex = 0;
        if (withAudio) { AudioDevices.Items.Add(new CaptureDevice("Synthetic audio", "diagnostic-audio", true)); AudioDevices.SelectedIndex = 1; }
        SetControls();
    }
    internal AiVideoSettings DiagnosticAiVideo=>SelectedAiVideo;
    internal bool DiagnosticAiControlsEnabled=>AiVideoControls.IsEnabled;
    internal void DiagnosticSelectAiVideo(bool enabled,int height=0,int quality=3)
    {
        AiVideoResolution.SelectedIndex=height switch{720=>1,1080=>2,1440=>3,2160=>4,_=>0};
        AiVideoQuality.SelectedIndex=Math.Clamp(quality-1,0,3);AiVideoToggle.IsChecked=enabled;
    }
    internal bool DiagnosticCapped30Game => SelectedCapped30Game;
    internal void DiagnosticSelectCapped30Game(bool enabled) => Capped30GameToggle.IsChecked=enabled;
    internal FrameGenerationMode DiagnosticFrameGeneration => SelectedFrameGeneration;
    internal void DiagnosticSetSourcePacing(bool enabled) => SourcePacingToggle.IsChecked = enabled;
    internal void DiagnosticSelectFrameGeneration(FrameGenerationMode mode) => PreviewFrameGeneration.SelectedIndex = (int)mode;
    internal PreviewUpscaler DiagnosticUpscaler => SelectedUpscaler;
    internal XenoCapture.UI.Direct3DHost DiagnosticEmbedded => embeddedPreview;
    internal void DiagnosticSelectUpscaler(PreviewUpscaler mode) => PreviewUpscaling.SelectedIndex = (int)mode;
    internal string DiagnosticSettingsJson => System.Text.Json.JsonSerializer.Serialize(CurrentPreferences());
    internal void DiagnosticApplySettingsJson(string json)
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<Preferences>(json);
        if (settings != null) ApplyPreferences(settings);
    }
    internal Task DiagnosticConnect() => connectionStart.Run(ConnectSelected);
    internal async Task DiagnosticConnectFullscreen(bool maximize = true, bool fastestMonitor = false)
    {
        await DiagnosticConnect();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!DiagnosticHasFrame && DiagnosticConnected && timeout.ElapsedMilliseconds < 5000)
            await Task.Delay(10);
        if (maximize && fastestMonitor)
        {
            var screen = System.Windows.Forms.Screen.AllScreens
                .OrderByDescending(s => DisplayTiming.RefreshRateForDevice(s.DeviceName)).First();
            await ToggleFullscreenOnDisplay(() => screen.Bounds);
        }
        else
        {
            await ToggleFullscreen();
            if (maximize) DiagnosticMaximizeFullscreen();
        }
        if (fullscreen == null) throw new InvalidOperationException("The diagnostic video could not enter fullscreen.");
    }
    internal Task DiagnosticDemo() => StartDemo();
    internal long DiagnosticPresented => System.Threading.Interlocked.Read(ref presentedFrames);
    internal XenoCapture.UI.Direct3DHost? DiagnosticPreview => activePreview;
    internal async Task DiagnosticFullscreen()
    {
        await ToggleFullscreen();
        if (fullscreen != null) fullscreen.WindowState = WindowState.Maximized;
    }
    internal void DiagnosticMaximizeFullscreen()
    {
        if (fullscreen != null) fullscreen.WindowState = WindowState.Maximized;
    }
    internal Task DiagnosticWindowed() => fullscreen?.CloseAsync() ?? Task.CompletedTask;
    internal Task DiagnosticDisconnect() => Disconnect();
    internal Task DiagnosticRecord(string directory) { folder = directory; return StartOrStopRecording(); }
    internal Task DiagnosticClose()
    {
        if (!diagnosticClosed.Task.IsCompleted) { Closed += (_, _) => diagnosticClosed.TrySetResult(); Close(); }
        return diagnosticClosed.Task;
    }
    internal string DiagnosticStatus => Status.Text;
    internal int DiagnosticSession => sessions.Generation;
    internal bool DiagnosticActive => sessions.Active;
    internal bool DiagnosticConnected => Connected;
    internal bool DiagnosticBusy => busy;
    internal bool DiagnosticRecording => recorder != null || pendingRecorder != null;
    internal bool DiagnosticHasFrame { get { lock (frameGate) return latest != null; } }
    internal long DiagnosticFrameTime { get { lock (frameGate) return latest?.Time ?? 0; } }
    internal Recorder? DiagnosticRecorder { get { lock (recorderGate) return recorder; } }
    internal void DiagnosticRecordingFailure(int session, Recorder owner) => ReportRecordingFailure(session, owner, new InvalidOperationException("Late encoder callback"));
    internal string? DiagnosticRecordingPath => lastRecording;
    internal void DiagnosticStaleFrame(int session, Packet frame) => OnData(session, frame, 2);
    internal void DiagnosticStaleFailure(int session) => ReportInputFailure(session, new InvalidOperationException("Late diagnostic callback"));

    internal static async Task CheckPhonePreviewDefaults()
    {
        var window = new MainWindow(false, false, null);
        try
        {
            window.DiagnosticSelectUpscaler(PreviewUpscaler.RtxVideo);
            window.DiagnosticSelectAiVideo(true, 1080, 3);
            window.ApplyStartupPreferences(window.CurrentPreferences());
            if (window.SelectedAiVideo != new AiVideoSettings(false, 1080, 3)
                || window.embeddedPreview.AiVideo.Enabled || window.SelectedUpscaler != PreviewUpscaler.Standard
                || window.embeddedPreview.Upscaler != PreviewUpscaler.Standard)
                throw new InvalidOperationException("Startup did not use Standard scaling with AI upscaling off.");

            window.DiagnosticSelectUpscaler(PreviewUpscaler.Enhanced);
            window.DiagnosticSelectAiVideo(true, 1080, 3);
            window.ApplyPhonePreviewDefaults();
            if (window.SelectedAiVideo.Enabled || window.embeddedPreview.AiVideo.Enabled
                || window.SelectedUpscaler != PreviewUpscaler.Standard || window.embeddedPreview.Upscaler != PreviewUpscaler.Standard)
                throw new InvalidOperationException("Phone preview inherited AI upscaling from another input.");
            var saved = window.CurrentPreferences();
            if (!saved.AiEnabled || saved.Upscaling != (int)PreviewUpscaler.Enhanced)
                throw new InvalidOperationException("Phone preview overwrote the saved upscaling preferences.");
            window.DiagnosticSelectAiVideo(true, 1080, 3);
            if (!window.embeddedPreview.AiVideo.Enabled)
                throw new InvalidOperationException("Phone AI upscaling could not be enabled manually.");
            window.RestorePreviewAfterPhone();
            if (!window.SelectedAiVideo.Enabled || window.SelectedUpscaler != PreviewUpscaler.Enhanced)
                throw new InvalidOperationException("Disconnect did not restore the previous upscaling settings.");
        }
        finally { await window.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    internal static async Task CheckAppearance(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        var sources = new System.Collections.Generic.Queue<TestCaptureBackend>();
        var main = new MainWindow(false, false, (_, _) => new CaptureStream(() => sources.Dequeue(), TimeSpan.FromSeconds(2))) { ShowActivated = false };
        try
        {
            main.DiagnosticPrepareInput(withAudio: true);
            main.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Capture("dark");
            SwitchTheme();
            Require(main.lightTheme && main.ThemeAction.Text == "Dark mode", "The light-mode toggle did not update.");
            Require(((SolidColorBrush)main.Background).Color == Color.FromRgb(242, 242, 246), "The light palette did not reach the window.");
            Capture("light");
            main.Width = main.MinWidth; main.Height = main.MinHeight;
            Capture("light-compact");

            string legacy = System.IO.Path.Combine(folder, "theme-test-arc.json");
            string current = System.IO.Path.Combine(folder, "theme-test-xeno.json");
            System.IO.File.WriteAllText(legacy, """{"Folder":"C:\\Captures","Video":"fixture-video","Resolution":2,"Bitrate":3}""");
            var imported = ReadPreferences(current, legacy);
            Require(imported is { LightTheme: false, Video: "fixture-video", Resolution: 2, Bitrate: 3 },
                "Legacy settings did not carry over with the dark default.");
            System.IO.File.WriteAllText(current, main.DiagnosticSettingsJson);
            var saved = ReadPreferences(current, legacy);
            Require(saved is { LightTheme: true }, "The saved theme did not round-trip.");
            SwitchTheme();
            Require(!main.lightTheme && ((SolidColorBrush)main.Background).Color == Color.FromRgb(21, 23, 29), "The dark palette did not restore.");
            main.ApplyPreferences(saved!);
            Require(main.lightTheme, "The loaded preference did not select light mode.");
            main.DiagnosticApplySettingsJson("""{"Folder":"C:\\Captures\\ARC Capture\\","InterfaceSounds":false,"InterfaceMotion":false}""");
            Require(main.folder == @"C:\Captures\Xeno Capture" && main.FolderLabel.Text == main.folder,
                "The legacy recording folder did not migrate to Xeno Capture.");
            main.ApplyPreferences(new Preferences("C:\\Captures\\invalid\0folder", null, null, 0, 0, 0, InterfaceSounds: false, InterfaceMotion: false));
            Require(main.folder == @"C:\Captures\Xeno Capture", "An invalid saved recording path replaced the current folder.");
            var feedback = System.Text.Json.JsonSerializer.Deserialize<Preferences>(main.DiagnosticSettingsJson);
            Require(feedback is { InterfaceSounds: false, InterfaceMotion: false }, "The feedback preferences did not round-trip.");
            var transform = main.BrandMark.RenderTransform;
            main.AnimateFeedback(main.BrandMark);
            Require(ReferenceEquals(transform, main.BrandMark.RenderTransform), "The disabled animation setting was ignored.");
            main.FeedbackButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(main.FeedbackPopup.IsOpen && main.FeedbackPopup.PopupAnimation == System.Windows.Controls.Primitives.PopupAnimation.None,
                "The feedback menu did not open with reduced motion.");
            main.FeedbackPopup.IsOpen = false;
            main.ApplyPreferences(saved!);
            Require(main.folder == saved!.Folder, "A custom recording path did not survive the migration.");
            foreach (var cue in Enum.GetValues<UI.InterfaceCue>())
            {
                var wave = UI.InterfaceSounds.Wave(cue);
                Require(wave.Length is > 2000 and < 15000 && wave.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                    && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40, 4)) == wave.Length - 44,
                    "An interface sound has an invalid WAV container.");
            }

            await main.DiagnosticDemo();
            var host = main.DiagnosticPreview;
            await WaitForFrames(main.DiagnosticPresented);
            SwitchTheme();
            await WaitForFrames(main.DiagnosticPresented);
            Require(ReferenceEquals(host, main.DiagnosticPreview), "Changing the theme replaced the live preview.");
            await main.DiagnosticDisconnect();

            await CheckFullscreenDisplays();
            var animated = await CheckFullscreenEntry(true);
            var appDisplay = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(main).Handle);
            var secondDisplay = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName != appDisplay.DeviceName);
            var crossDisplay = secondDisplay == null ? (int?)null : (await CheckFullscreenEntry(true, secondDisplay.Bounds)).ExitRegionUpdates;
            var interrupted = await CheckFullscreenEntry(true, interruptEntry: true);
            var reducedMotion = await CheckFullscreenEntry(false);
            await Task.Run(PreviewDiagnostics.Run);
            var entry = new { animationEnabledByWindows = SystemParameters.ClientAreaAnimation,
                animatedFrames = animated.Frames, animatedRegionUpdates = animated.WindowRegionUpdates,
                reducedMotionFrames = reducedMotion.Frames, reducedMotionRegionUpdates = reducedMotion.WindowRegionUpdates,
                exitRegionUpdates = animated.ExitRegionUpdates, reducedMotionExitRegionUpdates = reducedMotion.ExitRegionUpdates,
                displaysChecked = System.Windows.Forms.Screen.AllScreens.Length, crossDisplayExitRegionUpdates = crossDisplay,
                interruptedExitRegionUpdates = interrupted.ExitRegionUpdates, dockViewportReadback = "pass" };
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "fullscreen-entry.json"), System.Text.Json.JsonSerializer.Serialize(entry));
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "appearance-result.txt"), "PASS");
        }
        finally { await main.DiagnosticClose(); }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        void SwitchTheme() => main.ThemeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

        void Capture(string name)
        {
            main.UpdateLayout();
            var content = (FrameworkElement)main.Content;
            int width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
            int height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            image.Render(main);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png"));
            png.Save(file);
        }

        async Task CheckFullscreenDisplays()
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                main.fullscreenDisplayDevice = screen.DeviceName;
                main.RefreshFullscreenDisplays();
                Require(main.FullscreenDisplay.SelectedItem is DisplayChoice { Device: var device } && device == screen.DeviceName,
                    "The display selector did not restore a connected monitor.");
                var window = new UI.PreviewWindow(new UI.Direct3DHost(new UI.PreviewRendererHandoff()), displayBounds: main.ResolveFullscreenBounds)
                {
                    Width = 640, Height = 360, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    ShowActivated = false, ShowInTaskbar = false, Owner = main
                };
                try
                {
                    window.Show();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
                    var topLeft = window.PointToScreen(new Point());
                    var bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
                    Require(Math.Abs(topLeft.X - screen.Bounds.Left) <= 1 && Math.Abs(topLeft.Y - screen.Bounds.Top) <= 1
                        && Math.Abs(bottomRight.X - screen.Bounds.Right) <= 1 && Math.Abs(bottomRight.Y - screen.Bounds.Bottom) <= 1,
                        "The fullscreen window did not fill the chosen display in physical pixels.");
                }
                finally { await window.CloseAsync(animate: false); }
            }

            main.fullscreenDisplayDevice = "missing diagnostic display";
            main.RefreshFullscreenDisplays();
            string savedDisplay = main.DiagnosticSettingsJson;
            main.fullscreenDisplayDevice = null;
            main.DiagnosticApplySettingsJson(savedDisplay);
            Require(main.fullscreenDisplayDevice == "missing diagnostic display"
                && main.ResolveFullscreenBounds() == System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(main).Handle).Bounds,
                "An unavailable display did not round-trip and fall back to the app's monitor.");
            main.fullscreenDisplayDevice = null;
            main.RefreshFullscreenDisplays();
        }

        async Task<(int Frames, int WindowRegionUpdates, int ExitRegionUpdates)> CheckFullscreenEntry(bool motion,
            System.Drawing.Rectangle? display = null, bool interruptEntry = false)
        {
            sources.Enqueue(new TestCaptureBackend { Produce = true, OneFrame = true });
            main.MotionToggle.IsChecked = motion;
            main.DiagnosticPrepareInput();
            long before = main.DiagnosticPresented;
            await main.DiagnosticConnect();
            await WaitForFrames(before, 1);
            before = main.DiagnosticPresented;
            int expected = main.MotionEnabled && !interruptEntry ? 5 : 1;
            var target = main.EmbeddedPreviewBounds();
            Require(target.HasValue, "The embedded preview had no desktop bounds.");
            await main.ToggleFullscreenOnDisplay(display is { } bounds ? () => bounds : main.ResolveFullscreenBounds);
            Require(main.DiagnosticPreview != null && !ReferenceEquals(main.DiagnosticPreview, main.DiagnosticEmbedded),
                "The fullscreen entry test did not open a fullscreen host.");
            await WaitForFrames(before, expected);
            var window = main.fullscreen!;
            Require(window.EntryRegionActive == main.MotionEnabled, "The rounded region was not applied to the fullscreen window.");
            int shown = (int)(main.DiagnosticPresented - before);
            if (!interruptEntry)
            {
                await Task.Delay(400);
                long settled = main.DiagnosticPresented;
                await Task.Delay(100);
                shown = (int)(settled - before);
                Require(main.DiagnosticPresented == settled && (main.MotionEnabled ? shown >= expected && shown <= 42 : shown == 1),
                    "Fullscreen motion did not produce bounded redraws and settle when the source paused.");
                Require(!window.EntryRegionActive && (main.MotionEnabled ? window.EntryRegionUpdates > 3 : window.EntryRegionUpdates == 0),
                    "Fullscreen window motion did not apply and remove its rounded region.");
            }
            int updates = window.EntryRegionUpdates;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int coveredPresent = 0;
            main.embeddedPreview.FramePresented += _ =>
            {
                if (window.HoldsLandingSurface) System.Threading.Interlocked.Exchange(ref coveredPresent, 1);
            };
            window.Closed += (_, _) => closed.TrySetResult();
            var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(escape);
            Require(escape.Handled, "The fullscreen window did not handle Esc.");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(4));
            int exitUpdates = window.EntryRegionUpdates - updates;
            Require(main.fullscreen == null && (main.MotionEnabled ? exitUpdates > 3 : exitUpdates == 0),
                "Esc did not reverse the fullscreen motion or respect reduced motion.");
            if (main.MotionEnabled)
            {
                Require(window.FirstDockBounds is { } start && start.Size != target!.Value.Size && window.LandingBounds == target,
                    "The fullscreen window did not land on the embedded preview's desktop bounds.");
                Require(System.Threading.Volatile.Read(ref coveredPresent) == 1 && !window.HoldsLandingSurface,
                    "The landed window did not cover the preview through its first presentation or release afterward.");
            }
            else Require(window.LandingBounds == null, "Reduced motion moved the fullscreen window.");
            await main.DiagnosticDisconnect();
            return (shown, updates, exitUpdates);
        }

        async Task WaitForFrames(long before, int minimum = 3)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (main.DiagnosticPresented < before + minimum)
            {
                Require(timer.ElapsedMilliseconds < 5000, "The synthetic preview stopped presenting.");
                await Task.Delay(20);
            }
        }
    }
}


