using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace EnhanceCapCut.App;

/// <summary>
/// "♥ Donate" next to the Export button: the counterpart of the macOS version's <c>DonateButton</c>, with the same
/// schedule and effects. Now and then it plays one short effect to catch the eye — a double heartbeat, the label
/// swinging like a swing, or a pink glow — and stays still the rest of the time. With animations turned off in Windows,
/// only the glow plays.
/// </summary>
sealed class DonateButton : Grid
{
    enum Effect { Pulse, Swing, Glow }

    /// <summary>The first effect plays a little after the window appears…</summary>
    static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(6);
    /// <summary>…then one effect every 10 s, in turn: each lasts under 2 s, so the button is still ~85% of the time.</summary>
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    /// <summary>Pulse: the button grows by 12%, then by 7% (lub-dub).</summary>
    static readonly (double, double) PulseScales = (1.12, 1.07);
    /// <summary>Swing: the label hangs from its top edge and swings out to 12°, each swing ~70% of the one before.</summary>
    static readonly double[] SwingAngles = [12, -8.5, 6, -4, 2.5, -1];
    /// <summary>Glow: the pink halo's opacity at full strength (WinUI has no blurred shadow; the halo is a soft rim).</summary>
    const double GlowOpacity = 0.45;

    readonly ScaleTransform scale = new();
    readonly RotateTransform rotation = new();
    readonly Border halo = new() { Background = Theme.Pink, CornerRadius = new CornerRadius(8), Margin = new Thickness(-4), Opacity = 0 };
    readonly DispatcherTimer timer = new() { Interval = FirstDelay };
    int next;

    public event RoutedEventHandler? Click;

    public DonateButton()
    {
        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            RenderTransform = rotation,
            RenderTransformOrigin = new Point(0.5, 0),
            Children = { Theme.Icon(Theme.HeartFill, 12, Theme.Pink), new TextBlock { Text = "Donate" } },
        };
        var button = new Button { Content = label };
        ToolTipService.SetToolTip(button, "Support EnhanceCapCut");
        button.Click += (s, e) => Click?.Invoke(s, e);
        RenderTransform = scale;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Children.Add(halo);
        Children.Add(button);

        timer.Tick += (_, _) =>
        {
            timer.Interval = Interval;
            Play(new UISettings().AnimationsEnabled ? (Effect)(next++ % 3) : Effect.Glow);
        };
        timer.Start();
    }

    void Play(Effect effect)
    {
        var storyboard = new Storyboard();
        switch (effect)
        {
            case Effect.Pulse:
                double[] scales = [PulseScales.Item1, 1, PulseScales.Item2, 1];
                double[] times = [0.14, 0.3, 0.44, 0.94];
                storyboard.Children.Add(Track(scale, "ScaleX", scales, times));
                storyboard.Children.Add(Track(scale, "ScaleY", scales, times));
                break;
            case Effect.Swing:
                storyboard.Children.Add(Track(rotation, "Angle", [.. SwingAngles, 0],
                    [.. Enumerable.Range(1, SwingAngles.Length + 1).Select(i => 0.24 * i)]));
                break;
            case Effect.Glow:
                storyboard.Children.Add(Track(halo, "Opacity", [GlowOpacity, GlowOpacity, 0], [0.5, 0.9, 1.8]));
                break;
        }
        storyboard.Begin();
    }

    /// <summary>Animates <paramref name="property"/> through <paramref name="values"/>, reached at <paramref name="seconds"/>.</summary>
    static DoubleAnimationUsingKeyFrames Track(DependencyObject target, string property, double[] values, double[] seconds)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        for (var i = 0; i < values.Length; i++)
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                Value = values[i],
                KeyTime = TimeSpan.FromSeconds(seconds[i]),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }
}
