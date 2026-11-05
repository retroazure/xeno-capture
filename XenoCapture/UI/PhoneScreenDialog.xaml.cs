using System.Windows;
using XenoCapture.Engine;

namespace XenoCapture.UI;

public partial class PhoneScreenDialog : Window
{
    internal bool PlayPhoneAudio => PhoneAudio.IsChecked == true;

    internal PhoneScreenDialog(bool captureAvailable)
    {
        InitializeComponent();
        PhoneAudio.IsEnabled = PlayAndroidUsb.IsEnabled = captureAvailable && AndroidUsbCaptureBackend.IsPackaged;
        if (!PlayAndroidUsb.IsEnabled)
            AndroidUsbHint.Text = "Android USB preview needs Windows Graphics Capture and the verified Android helper beside XenoCapture.exe.";
    }

    void StartAndroidUsb(object sender, RoutedEventArgs e) => DialogResult = true;
}
