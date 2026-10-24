using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using LockTune.Core;

namespace LockTune.UI;

/// The Mac app's glass (Glass.swift, "imitation" path): frosted material, a
/// soft top sheen, a bright uneven rim and a shadow. Lite mode: a flat
/// translucent fill; Off: classic frosted blur.
public static class Glass
{
    /// `dark` = the widget's colour scheme (light artwork → light glass).
    /// `shadow`: false on the lock screen, where shadows are images (see Shadows).
    public static void Apply(Border border, GlassLevel level, bool lite, bool dark = true, bool onlyInMega = false, bool shadow = true)
    {
        if (onlyInMega && (level != GlassLevel.Mega || lite))
        {
            border.Background = Brushes.Transparent;
            border.BorderBrush = null;
            border.BorderThickness = new Thickness(0);
            border.Effect = null;
            return;
        }
        if (lite)
        {
            border.Background = Frozen(new SolidColorBrush(Color.FromArgb(107, 0, 0, 0)));
            border.BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)));
            border.BorderThickness = new Thickness(0.5);
            border.Effect = null;
            return;
        }
        if (level == GlassLevel.Off)
        {
            border.Background = Material(ultraThin: true, dark, sheen: 0);
            border.BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)));
            border.BorderThickness = new Thickness(0.5);
            border.Effect = null;
            return;
        }
        bool mega = level == GlassLevel.Mega;
        border.Background = Material(ultraThin: mega, dark, sheen: mega ? 0.22 : 0.14);
        border.BorderBrush = Rim(mega ? 1 : 0.7);
        border.BorderThickness = new Thickness(1.2);
        border.Effect = shadow ? Shadow(mega ? 0.28 : 0.18, mega ? 22 : 14, mega ? 10 : 6) : null;
    }

    /// The glass shadow (opacity, radius, y) for a level, or null when it has none.
    public static (double Opacity, double Radius, double Y)? ShadowOf(GlassLevel level, bool lite) =>
        lite || level == GlassLevel.Off ? null
        : level == GlassLevel.Mega ? (0.28, 22, 10) : (0.18, 14, 6);

    /// Frosted material + sheen (white top → clear → faint white bottom).
    private static Brush Material(bool ultraThin, bool dark, double sheen)
    {
        var material = dark
            ? Color.FromArgb(ultraThin ? (byte)70 : (byte)105, 28, 28, 32)
            : Color.FromArgb(ultraThin ? (byte)90 : (byte)140, 250, 250, 252);
        var group = new DrawingGroup();
        var rect = new RectangleGeometry(new Rect(0, 0, 1, 1));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(material), null, rect));
        if (sheen > 0)
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(sheen * 255), 255, 255, 255), 0));
            gradient.GradientStops.Add(new GradientStop(Colors.Transparent, 0.5));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(13, 255, 255, 255), 1));
            group.Children.Add(new GeometryDrawing(gradient, null, rect));
        }
        return Frozen(new DrawingBrush(group) { Stretch = Stretch.Fill });
    }

    /// Light catching the edge: bright top-left, faint middle, highlight bottom-right.
    public static Brush Rim(double strength)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(0.75 * strength * 255), 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(0.08 * strength * 255), 255, 255, 255), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(0.4 * strength * 255), 255, 255, 255), 1));
        return Frozen(brush);
    }

    /// SwiftUI .shadow(color: .black.opacity(o), radius: r, y: y).
    public static DropShadowEffect Shadow(double opacity, double radius, double y) =>
        new() { Color = Colors.Black, Opacity = opacity, BlurRadius = radius * 2, ShadowDepth = y, Direction = 270 };

    public static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }

    public static FontWeight Weight(ClockWeight weight) => weight switch
    {
        ClockWeight.UltraLight => FontWeights.UltraLight,
        ClockWeight.Thin => FontWeights.Thin,
        ClockWeight.Light => FontWeights.Light,
        ClockWeight.Regular => FontWeights.Normal,
        ClockWeight.Medium => FontWeights.Medium,
        ClockWeight.Semibold => FontWeights.SemiBold,
        _ => FontWeights.Bold,
    };

    public static FontFamily Family(FontChoice font) => new(Titles.Family(font));

    /// SF Pro (system font) stand-in.
    public static readonly FontFamily System = new("SF Pro Display, Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily SystemText = new("SF Pro Text, Segoe UI Variable Text, Segoe UI");
    /// SF Pro Rounded stand-in (times under the progress bar).
    public static readonly FontFamily Rounded = new("SF Pro Rounded, Segoe UI Variable Display, Segoe UI");

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

    /// Close to SwiftUI's .spring(response: ~0.6, dampingFraction: ~0.8).
    public static IEasingFunction Spring => new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
    /// Bouncier spring (dampingFraction ≈ 0.66).
    public static IEasingFunction Bouncy => new ElasticEase { Oscillations = 1, Springiness = 5, EasingMode = EasingMode.EaseOut };
}

/// Plain button that shrinks while pressed, like iOS media controls
/// (PressableButtonStyle in the Mac app).
public class PressableButton : Border
{
    private readonly ScaleTransform scale = new(1, 1);
    private bool pressed;
    public event Action? Click;

    public PressableButton()
    {
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = scale;
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

    private void Press(bool down)
    {
        var ease = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut };
        Glass.Animate(scale, ScaleTransform.ScaleXProperty, down ? 0.8 : 1, down ? 0.1 : 0.25, down ? null : ease);
        Glass.Animate(scale, ScaleTransform.ScaleYProperty, down ? 0.8 : 1, down ? 0.1 : 0.25, down ? null : ease);
        Glass.Animate(this, OpacityProperty, down ? 0.55 : 1, 0.12);
    }
}

/// 34 pt glass circle with a 13 pt semibold symbol (ToolbarButton in LockWidgetView.swift).
public sealed class ToolbarButton : PressableButton
{
    private readonly Image shadow = new();
    private readonly Border glass = new() { Width = 34, Height = 34, CornerRadius = new CornerRadius(17) };
    private readonly Border ring = new() { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1), IsHitTestVisible = false };
    private readonly Symbol symbol;

    public ToolbarButton(string symbolName, string help)
    {
        symbol = new Symbol(symbolName, 13, 1.25);
        var grid = new Grid { Width = 34, Height = 34 };
        var shadowLayer = new Canvas { IsHitTestVisible = false };
        shadowLayer.Children.Add(shadow);
        grid.Children.Add(shadowLayer);
        grid.Children.Add(glass);
        grid.Children.Add(symbol);
        grid.Children.Add(ring);
        Child = grid;
        ToolTip = help;
    }

    public string SymbolName { set => symbol.SymbolName = value; }

    public void ApplyLook(GlassLevel level, bool lite, bool dark, bool isOn)
    {
        Glass.Apply(glass, level, lite, dark, shadow: false);
        if (Glass.ShadowOf(level, lite) is { } s)
        {
            shadow.Source = Shadows.RoundedRect(34, 34, 17, s.Radius, s.Opacity, out double pad);
            shadow.Stretch = Stretch.Fill;
            shadow.Width = shadow.Height = 34 + pad * 2;
            Canvas.SetLeft(shadow, -pad);
            Canvas.SetTop(shadow, -pad + s.Y);
        }
        else shadow.Source = null;
        ring.BorderBrush = new SolidColorBrush(Color.FromArgb(isOn ? (byte)153 : (byte)0, 255, 255, 255));
    }
}

/// Transport button: symbol in a (size × 1.6) frame; glass bubble only in Mega.
public sealed class TransportButton : PressableButton
{
    private readonly Border glass = new();
    private readonly Symbol symbol;

    public TransportButton(string symbolName, double size)
    {
        symbol = new Symbol(symbolName, size, 1.2);
        glass.Width = glass.Height = size * 1.6;
        glass.CornerRadius = new CornerRadius(size * 0.8);
        var grid = new Grid();
        grid.Children.Add(glass);
        grid.Children.Add(symbol);
        Child = grid;
    }

    public string SymbolName { set => symbol.SymbolName = value; }

    public void ApplyLook(GlassLevel level, bool lite, bool dark) => Glass.Apply(glass, level, lite, dark, onlyInMega: true, shadow: false);
}
