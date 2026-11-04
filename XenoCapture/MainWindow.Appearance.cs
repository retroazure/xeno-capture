using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;

namespace XenoCapture;

public partial class MainWindow
{
    ResourceDictionary? lightPalette;
    bool lightTheme;

    void ToggleTheme(object sender, RoutedEventArgs e)
    {
        ApplyTheme(!lightTheme);
        AnimateFeedback(BrandMark);
        PlayCue(UI.InterfaceCue.Tap);
    }

    void ApplyTheme(bool light)
    {
        if (light != lightTheme)
        {
            if (light)
            {
                lightPalette ??= new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/UI/Themes/Light.xaml", UriKind.Absolute)
                };
                Resources.MergedDictionaries.Add(lightPalette);
            }
            else if (lightPalette != null)
            {
                Resources.MergedDictionaries.Remove(lightPalette);
            }
            lightTheme = light;
        }

        ThemeAction.Text = light ? "Dark mode" : "Light mode";
        AutomationProperties.SetName(ThemeButton, $"Switch to {ThemeAction.Text.ToLowerInvariant()}");
        UpdateCaption();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateCaption();
    }

    void UpdateCaption()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemParameters.HighContrast) return;

        const uint CaptionColor = 35, CaptionTextColor = 36;
        SetCaptionColor(CaptionColor, (SolidColorBrush)FindResource("WindowBrush"));
        SetCaptionColor(CaptionTextColor, (SolidColorBrush)FindResource("TextBrush"));

        void SetCaptionColor(uint attribute, SolidColorBrush brush)
        {
            var color = brush.Color;
            int colorRef = color.R | (color.G << 8) | (color.B << 16);
            _ = DwmSetWindowAttribute(handle, attribute, ref colorRef, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);
}
