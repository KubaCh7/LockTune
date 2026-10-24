using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using LockTune.Core;

namespace LockTune.UI;

/// Icon glyphs (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10).
public static class Icons
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public const string Play = "", Pause = "", Previous = "", Next = "";
    public const string Lyrics = "", Photo = "", View = "", Hide = "", Close = "";
    public const string Add = "", Remove = "", Lock = "", Unlock = "", Music = "";
    public const string DockLeft = "", DockRight = "", Center = "", Settings = "";
    public const string ChevronUp = "";
}

/// "Liquid glass" look in WPF: translucent fill with a light rim. The
/// background behind it is already blurred, so it reads as frosted glass.
public static class Glass
{
    public static Brush Fill(GlassLevel level, bool light)
    {
        if (level == GlassLevel.Off)
            return Frozen(new SolidColorBrush(Color.FromArgb(light ? (byte)70 : (byte)80, light ? (byte)255 : (byte)0,
                                                             light ? (byte)255 : (byte)0, light ? (byte)255 : (byte)0)));
        double top = level == GlassLevel.Mega ? 0.30 : 0.20, bottom = level == GlassLevel.Mega ? 0.12 : 0.09;
        if (light) { top += 0.15; bottom += 0.15; }
        return Frozen(new LinearGradientBrush(Color.FromArgb((byte)(top * 255), 255, 255, 255),
                                              Color.FromArgb((byte)(bottom * 255), 255, 255, 255), 90));
    }

    public static Brush Rim(GlassLevel level)
    {
        if (level == GlassLevel.Off) return Brushes.Transparent;
        double strong = level == GlassLevel.Mega ? 0.75 : 0.45;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(strong * 255), 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(18, 255, 255, 255), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(strong * 0.55 * 255), 255, 255, 255), 1));
        return Frozen(brush);
    }

    public static Thickness RimThickness(GlassLevel level) => new(level == GlassLevel.Mega ? 1.5 : level == GlassLevel.Off ? 0 : 1);

    /// Applies the glass look to a border.
    public static void Apply(Border border, GlassLevel level, bool light, bool lite)
    {
        border.Background = Fill(level, light);
        border.BorderBrush = Rim(level);
        border.BorderThickness = RimThickness(level);
        border.Effect = lite || level == GlassLevel.Off ? null
            : new DropShadowEffect { BlurRadius = level == GlassLevel.Mega ? 30 : 22, ShadowDepth = 6, Direction = 270,
                                     Opacity = level == GlassLevel.Mega ? 0.28 : 0.2, Color = Colors.Black };
    }

    public static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }

    public static FontWeight Weight(string name) => name switch
    {
        "Light" => FontWeights.Light,
        "Regular" => FontWeights.Normal,
        "Medium" => FontWeights.Medium,
        "SemiBold" => FontWeights.SemiBold,
        "Bold" => FontWeights.Bold,
        "Black" => FontWeights.Black,
        _ => FontWeights.SemiBold,
    };

    /// The chosen font, falling back to Segoe UI when it isn't installed.
    public static FontFamily Family(string name) => new($"{name}, Segoe UI Variable Display, Segoe UI");

    public static void Animate(UIElement element, DependencyProperty property, double to, double seconds,
                               IEasingFunction? easing = null, double delay = 0)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds))
        {
            EasingFunction = easing ?? new CubicEase { EasingMode = EasingMode.EaseOut },
            BeginTime = TimeSpan.FromSeconds(delay),
        };
        element.BeginAnimation(property, animation);
    }

    public static void Animate(Animatable target, DependencyProperty property, double to, double seconds,
                               IEasingFunction? easing = null, double delay = 0)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds))
        {
            EasingFunction = easing ?? new CubicEase { EasingMode = EasingMode.EaseOut },
            BeginTime = TimeSpan.FromSeconds(delay),
        };
        target.BeginAnimation(property, animation);
    }

    /// Spring-like ease (slight overshoot), close to iOS springs.
    public static IEasingFunction Spring => new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
}

/// Round glass button with an icon glyph; shrinks while pressed.
public sealed class GlassButton : Border
{
    private readonly TextBlock glyph;
    private readonly ScaleTransform scale = new(1, 1);
    private bool pressed;
    public event Action? Click;

    public GlassButton(string icon, double size, double iconSize, string tooltip, bool glass = true)
    {
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        Cursor = Cursors.Hand;
        ToolTip = tooltip;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = scale;
        Background = Brushes.Transparent;
        IsGlass = glass;
        glyph = new TextBlock
        {
            Text = icon,
            FontFamily = Icons.Font,
            FontSize = iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = glyph;
        MouseLeftButtonDown += (_, e) => { pressed = true; CaptureMouse(); Press(true); e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            ReleaseMouseCapture();
            Press(false);
            if (IsMouseOver) Click?.Invoke();
            e.Handled = true;
        };
        LostMouseCapture += (_, _) => { if (pressed) { pressed = false; Press(false); } };
    }

    public bool IsGlass { get; set; }

    public string Icon { get => glyph.Text; set => glyph.Text = value; }

    public void ApplyLook(GlassLevel level, bool light, bool lite, bool isOn = false)
    {
        if (IsGlass) Glass.Apply(this, level, light, lite);
        else if (level == GlassLevel.Mega) Glass.Apply(this, level, light, true);
        else { Background = Brushes.Transparent; BorderThickness = new Thickness(0); Effect = null; }
        if (isOn)
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
            BorderThickness = new Thickness(1.5);
        }
    }

    private void Press(bool down)
    {
        Glass.Animate(scale, ScaleTransform.ScaleXProperty, down ? 0.82 : 1, down ? 0.08 : 0.3, down ? null : Glass.Spring);
        Glass.Animate(scale, ScaleTransform.ScaleYProperty, down ? 0.82 : 1, down ? 0.08 : 0.3, down ? null : Glass.Spring);
        Glass.Animate(glyph, OpacityProperty, down ? 0.55 : 1, 0.12);
    }
}
