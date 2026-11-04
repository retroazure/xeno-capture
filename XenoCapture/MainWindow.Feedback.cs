using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using XenoCapture.UI;

namespace XenoCapture;

public partial class MainWindow
{
    readonly InterfaceSounds interfaceSounds = new();
    static readonly DoubleAnimationUsingKeyFrames feedbackPop = CreateFeedbackPop();

    bool MotionEnabled => MotionToggle.IsChecked == true && SystemParameters.ClientAreaAnimation;

    void InitializeFeedback()
    {
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(AnimateButtonClick));
        ContentRendered += (_, _) => AnimateFeedback(BrandMark);
    }

    void AnimateButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button button) AnimateFeedback(button);
    }

    void AnimateFeedback(FrameworkElement element)
    {
        if (!MotionEnabled || closing) return;
        var scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(.5, .5);
        element.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, feedbackPop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, feedbackPop);
    }

    static DoubleAnimationUsingKeyFrames CreateFeedbackPop()
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(.94, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1.025, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(105))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(165))));
        animation.Freeze();
        return animation;
    }

    void PlayCue(InterfaceCue cue)
    {
        if (!discoverHardware || closing || SoundsToggle.IsChecked != true || recordingStop.Current != null) return;
        lock (recorderGate)
            if (recorder != null || pendingRecorder != null) return;
        interfaceSounds.Play(cue);
    }

    void ShowFeedbackSettings(object sender, RoutedEventArgs e)
    {
        FeedbackPopup.PopupAnimation = MotionEnabled ? PopupAnimation.Fade : PopupAnimation.None;
        FeedbackPopup.IsOpen = true;
        SoundsToggle.Focus();
        PlayCue(InterfaceCue.Tap);
    }

    void FeedbackSettingsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        FeedbackPopup.IsOpen = false;
        FeedbackButton.Focus();
    }

    void FeedbackSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || !FeedbackPopup.IsOpen) return;
        if (SoundsToggle.IsChecked == true) PlayCue(InterfaceCue.Tap);
        else _ = interfaceSounds.SilenceAsync();
    }
}
