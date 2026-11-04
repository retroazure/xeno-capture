using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using XenoCapture.Engine;
using XenoCapture.UI;

namespace XenoCapture;

public partial class App : Application
{
    // Preserve the diagnostic precedence when multiple modes are specified.
    static readonly string[] DiagnosticModes =
    [
        "--card-test", "--theme-tests", "--ai-video-tests", "--capped-game-tests", "--capped-game-soak",
        "--rtx-video-tests", "--framegen-soak", "--framegen-tests", "--upscaling-tests",
        "--scheduling-benchmark", "--render-benchmark", "--presentation-tests", "--shutdown-tests", "--self-test"
    ];
    static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    bool mediaFoundationStarted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        foreach (string mode in DiagnosticModes)
        {
            int index = Array.IndexOf(e.Args, mode);
            if (index < 0) continue;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await RunDiagnosticAsync(mode, e.Args, index));
            return;
        }

        try
        {
            StartMediaFoundation();
            MainWindow = new MainWindow(Array.IndexOf(e.Args, "--demo") >= 0);
            MainWindow.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Xeno Capture could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    void StartMediaFoundation()
    {
        Native.Hr(Native.MFStartup(0x20070, 0));
        mediaFoundationStarted = true;
    }

    async Task<int> RunDiagnosticAsync(string mode, string[] args, int index)
    {
        string? directory = null;
        try
        {
            string? cardName = mode == "--card-test" ? ReadArgument(args, index + 1) : null;
            string? destination = ReadArgument(args, index + (mode == "--card-test" ? 2 : 1));
            if (mode == "--card-test" && (cardName == null || destination == null))
            {
                Trace.TraceError("Usage: --card-test <capture card name> <output folder>.");
                return 2;
            }

            directory = destination == null ? Path.Combine(AppContext.BaseDirectory, "self-test") : Path.GetFullPath(destination);
            Directory.CreateDirectory(directory);
            StartMediaFoundation();
            await (mode switch
            {
                "--card-test" => RunCardDiagnosticAsync(cardName!, directory),
                "--theme-tests" => XenoCapture.MainWindow.CheckAppearance(directory),
                "--ai-video-tests" => AiVideoDiagnostics.Run(directory),
                "--capped-game-tests" => CappedGameDiagnostics.Run(directory, 6),
                "--capped-game-soak" => CappedGameDiagnostics.Run(directory, 20),
                "--rtx-video-tests" => RtxVideoDiagnostics.Run(directory),
                "--framegen-soak" => FrameGenerationDiagnostics.Run(directory, true),
                "--framegen-tests" => FrameGenerationDiagnostics.Run(directory),
                "--upscaling-tests" => UpscalingDiagnostics.Run(directory),
                "--scheduling-benchmark" => WriteBenchmarkAsync(directory, SchedulingDiagnostics.Run),
                "--render-benchmark" => WriteBenchmarkAsync(directory, RenderPerformanceDiagnostics.Run),
                "--presentation-tests" => RunPresentationDiagnosticAsync(directory, args, index),
                "--shutdown-tests" => RunShutdownDiagnosticAsync(directory),
                "--self-test" => Diagnostics.Run(directory),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            });
            return 0;
        }
        catch (Exception error)
        {
            if (directory != null)
            {
                try { await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), error.ToString()); }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
                { Trace.TraceError("Could not write the diagnostic failure report ({0}).", writeError.GetType().Name); }
            }
            else Trace.TraceError("Invalid diagnostic output path ({0}).", error.GetType().Name);

            foreach (var open in Windows.Cast<Window>().ToArray())
            {
                try
                {
                    if (open is MainWindow main) await main.DiagnosticClose();
                    else if (open is PreviewWindow preview) await preview.CloseAsync(animate: false);
                    else open.Close();
                }
                catch (Exception closeError)
                { Trace.TraceError("Could not close a diagnostic window ({0}).", closeError.GetType().Name); }
            }
            return directory == null ? 2 : 1;
        }
    }

    static string? ReadArgument(string[] args, int index)
    {
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
            return null;
        return args[index];
    }

    static async Task RunCardDiagnosticAsync(string name, string directory)
    {
        var available = await Task.Run(() => CaptureDevices.List(false));
        var matches = available.FindAll(device => string.Equals(device.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matches.Count != 1) throw new InvalidOperationException("The requested capture card was not uniquely identified.");
        var modes = await Task.Run(() => CaptureDevices.Modes(matches[0]));
        var mode = modes.Find(mode => mode.Width == 1920 && mode.Height == 1080 && mode.Fps is > 59 and < 61 && mode.Subtype == Keys.Nv12)
            ?? throw new InvalidOperationException("1080p60 NV12 is not available.");
        var results = await PresentationDiagnostics.Run(matches[0], mode,
            step => File.WriteAllText(Path.Combine(directory, "stage.txt"), step));
        await WriteResultsAsync(directory, results);
    }

    static Task RunPresentationDiagnosticAsync(string directory, string[] args, int index)
    {
        int cycles = int.TryParse(ReadArgument(args, index + 2), out int requested) ? Math.Clamp(requested, 1, 100) : 20;
        var upscaler = string.Equals(ReadArgument(args, index + 3), "enhanced", StringComparison.OrdinalIgnoreCase)
            ? PreviewUpscaler.Enhanced : PreviewUpscaler.Standard;
        return PresentationStressDiagnostics.Run(directory, true, cycles, upscaler);
    }

    static async Task RunShutdownDiagnosticAsync(string directory) =>
        await WriteResultsAsync(directory, await LifecycleDiagnostics.Run(directory));

    static async Task WriteBenchmarkAsync<T>(string directory, Func<T> run) =>
        await WriteResultsAsync(directory, await Task.Run(run));

    static Task WriteResultsAsync<T>(string directory, T results) =>
        File.WriteAllTextAsync(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(results, ReportJson));

    protected override void OnExit(ExitEventArgs e)
    {
        if (mediaFoundationStarted && MediaFoundationUsage.TryBeginShutdown())
        {
            int result = Native.MFShutdown();
            if (result < 0) Trace.TraceWarning("Media Foundation shutdown failed (0x{0:X8}).", result);
        }
        base.OnExit(e);
    }
}
