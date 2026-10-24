using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LockTune.UI;

/// Vector look-alikes of the SF Symbols the Mac app uses (SF Symbols itself
/// can't be shipped on Windows). Drawn in a 24×24 box.
public static class Symbols
{
    public sealed record Part(Geometry Geometry, bool Fill, double Stroke = 0);

    private static readonly Dictionary<string, Part[]> All = new();

    private static Geometry G(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static Geometry Circle(double x, double y, double r)
    {
        var g = new EllipseGeometry(new Point(x, y), r, r);
        g.Freeze();
        return g;
    }

    private static Geometry Rect(double x, double y, double w, double h, double r)
    {
        var g = new RectangleGeometry(new Rect(x, y, w, h), r, r);
        g.Freeze();
        return g;
    }

    private static Geometry Union(params Geometry[] parts)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var p in parts) group.Children.Add(p);
        group.Freeze();
        return group;
    }

    /// `shape` with `cut` removed (cut is stroked with `width` if > 0).
    private static Geometry Cut(Geometry shape, Geometry cut, double width = 0)
    {
        var hole = width > 0
            ? cut.GetWidenedPathGeometry(new Pen(Brushes.Black, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round })
            : cut;
        var g = new CombinedGeometry(GeometryCombineMode.Exclude, shape, hole);
        var flat = g.GetFlattenedPathGeometry();
        flat.Freeze();
        return flat;
    }

    private static Geometry Gear()
    {
        var figure = new PathFigure { IsClosed = true };
        const int teeth = 8;
        const double rOut = 10.2, rIn = 7.7;
        Point P(double r, double deg) => new(12 + r * Math.Cos(deg * Math.PI / 180), 12 + r * Math.Sin(deg * Math.PI / 180));
        bool first = true;
        for (int i = 0; i < teeth; i++)
        {
            double a = i * 360.0 / teeth;
            foreach (var p in new[] { P(rIn, a - 14), P(rOut, a - 8.5), P(rOut, a + 8.5), P(rIn, a + 14) })
            {
                if (first) { figure.StartPoint = p; first = false; }
                else figure.Segments.Add(new LineSegment(p, true));
            }
            figure.Segments.Add(new ArcSegment(P(rIn, a + 45 - 14), new Size(rIn, rIn), 0, false, SweepDirection.Clockwise, true));
        }
        var path = new PathGeometry(new[] { figure });
        path.Freeze();
        return path;
    }

    static Symbols()
    {
        var bubble = G("M5.5,3.5 H18.5 A3,3 0 0 1 21.5,6.5 V14.5 A3,3 0 0 1 18.5,17.5 H11.5 L7,21 V17.5 H5.5 A3,3 0 0 1 2.5,14.5 V6.5 A3,3 0 0 1 5.5,3.5 Z");
        var quotes = Union(Circle(8.7, 9.4, 1.65), G("M10.35,9.4 C10.35,11.4 9.4,12.8 7.9,13.4 L7.55,12.75 C8.4,12.2 8.85,11.4 8.85,10.8 Z"),
                           Circle(14.2, 9.4, 1.65), G("M15.85,9.4 C15.85,11.4 14.9,12.8 13.4,13.4 L13.05,12.75 C13.9,12.2 14.35,11.4 14.35,10.8 Z"));
        All["quote.bubble"] = new[] { new Part(bubble, false, 1.7), new Part(quotes, true) };
        All["quote.bubble.fill"] = new[] { new Part(Cut(bubble, quotes), true) };

        All["play.fill"] = new[] { new Part(G("M7,4.3 C7,3.3 8.1,2.7 8.9,3.3 L19.7,11 C20.4,11.5 20.4,12.5 19.7,13 L8.9,20.7 C8.1,21.3 7,20.7 7,19.7 Z"), true) };
        All["pause.fill"] = new[] { new Part(Union(Rect(4.6, 3.8, 5.4, 16.4, 1.3), Rect(14, 3.8, 5.4, 16.4, 1.3)), true) };
        All["forward.fill"] = new[] { new Part(G("M1.5,6.4 C1.5,5.6 2.4,5.1 3.1,5.6 L11.4,11.2 C12,11.6 12,12.4 11.4,12.8 L3.1,18.4 C2.4,18.9 1.5,18.4 1.5,17.6 Z M12,6.4 C12,5.6 12.9,5.1 13.6,5.6 L21.9,11.2 C22.5,11.6 22.5,12.4 21.9,12.8 L13.6,18.4 C12.9,18.9 12,18.4 12,17.6 Z"), true) };
        All["backward.fill"] = new[] { new Part(G("M22.5,6.4 C22.5,5.6 21.6,5.1 20.9,5.6 L12.6,11.2 C12,11.6 12,12.4 12.6,12.8 L20.9,18.4 C21.6,18.9 22.5,18.4 22.5,17.6 Z M12,6.4 C12,5.6 11.1,5.1 10.4,5.6 L2.1,11.2 C1.5,11.6 1.5,12.4 2.1,12.8 L10.4,18.4 C11.1,18.9 12,18.4 12,17.6 Z"), true) };

        All["minus"] = new[] { new Part(G("M5,12 H19"), false, 2) };
        All["plus"] = new[] { new Part(G("M12,5 V19 M5,12 H19"), false, 2) };
        All["xmark"] = new[] { new Part(G("M6.5,6.5 L17.5,17.5 M17.5,6.5 L6.5,17.5"), false, 2) };

        var eye = G("M1.8,12 C4.3,7.3 7.9,5 12,5 C16.1,5 19.7,7.3 22.2,12 C19.7,16.7 16.1,19 12,19 C7.9,19 4.3,16.7 1.8,12 Z");
        All["eye"] = new[] { new Part(eye, false, 1.7), new Part(Circle(12, 12, 3.3), true) };
        All["eye.slash"] = new[] { new Part(eye, false, 1.7), new Part(Circle(12, 12, 3.3), true), new Part(G("M3.8,3.2 L20.2,20.8"), false, 1.9) };

        var photoFrame = G("M5,4 H19 A2.5,2.5 0 0 1 21.5,6.5 V17.5 A2.5,2.5 0 0 1 19,20 H5 A2.5,2.5 0 0 1 2.5,17.5 V6.5 A2.5,2.5 0 0 1 5,4 Z");
        var mountains = G("M4.2,18.2 L9.1,12.6 L12.9,16.6 L15.5,13.9 L19.8,18.2 Z");
        All["photo"] = new[] { new Part(photoFrame, false, 1.7), new Part(mountains, true), new Part(Circle(16.2, 8.6, 1.7), true) };
        All["photo.fill"] = new[] { new Part(Cut(photoFrame, Union(mountains, Circle(16.2, 8.6, 1.7))), true) };

        var screen = G("M4.5,4.5 H19.5 A2.5,2.5 0 0 1 22,7 V17 A2.5,2.5 0 0 1 19.5,19.5 H4.5 A2.5,2.5 0 0 1 2,17 V7 A2.5,2.5 0 0 1 4.5,4.5 Z");
        All["rectangle.righthalf.inset.filled"] = new[] { new Part(screen, false, 1.6), new Part(G("M12.5,7 H18.5 A1,1 0 0 1 19.5,8 V16 A1,1 0 0 1 18.5,17 H12.5 Z"), true) };
        All["rectangle.lefthalf.inset.filled"] = new[] { new Part(screen, false, 1.6), new Part(G("M5.5,7 H11.5 V17 H5.5 A1,1 0 0 1 4.5,16 V8 A1,1 0 0 1 5.5,7 Z"), true) };
        All["rectangle.center.inset.filled"] = new[] { new Part(screen, false, 1.6), new Part(Rect(7.5, 7, 9, 10, 1), true) };

        var noteHead = new EllipseGeometry(new Point(8.8, 17.4), 3.4, 2.7) { Transform = new RotateTransform(-18, 8.8, 17.4) };
        noteHead.Freeze();
        All["music.note"] = new[] { new Part(Union(noteHead, G("M10.8,4.6 L12.6,4 V17.4 H10.8 Z"), G("M12.6,4 C14.8,4.6 17.8,5.4 18.6,8.8 C17.1,7.6 15,7.4 12.6,7.4 Z")), true) };
        var smallHead = new EllipseGeometry(new Point(10.7, 16.7), 1.95, 1.5) { Transform = new RotateTransform(-18, 10.7, 16.7) };
        smallHead.Freeze();
        All["music.note.house"] = new[]
        {
            new Part(G("M2.8,11.4 L12,3.6 L21.2,11.4 M5.4,9.4 V19.6 A0.9,0.9 0 0 0 6.3,20.5 H17.7 A0.9,0.9 0 0 0 18.6,19.6 V9.4"), false, 1.9),
            new Part(Union(smallHead, G("M11.9,10.2 H12.9 V16.7 H11.9 Z"), G("M12.9,10.2 C14.2,10.6 15.4,11.2 15.6,12.8 C14.8,12.2 13.9,12.1 12.9,12.1 Z")), true),
        };

        All["checkmark.circle"] = new[] { new Part(Circle(12, 12, 9.4), false, 1.7), new Part(G("M7.6,12.4 L10.6,15.3 L16.4,8.9"), false, 2) };
        All["checkmark.circle.fill"] = new[] { new Part(Cut(Circle(12, 12, 10), G("M7.4,12.4 L10.6,15.5 L16.6,8.9"), 2.3), true) };
        All["exclamationmark.circle.fill"] = new[] { new Part(Cut(Circle(12, 12, 10), Union(G("M10.9,6.2 H13.1 L12.75,13.8 H11.25 Z"), Circle(12, 17, 1.35))), true) };
        All["exclamationmark.triangle"] = new[]
        {
            new Part(G("M10.3,4.2 C11.1,2.9 12.9,2.9 13.7,4.2 L21.6,17.9 C22.4,19.2 21.4,20.8 19.9,20.8 H4.1 C2.6,20.8 1.6,19.2 2.4,17.9 Z"), false, 1.7),
            new Part(Union(G("M11.1,8.8 H12.9 L12.65,14.6 H11.35 Z"), Circle(12, 17.3, 1.15)), true),
        };

        All["gearshape"] = new[] { new Part(Gear(), false, 1.6), new Part(Circle(12, 12, 3.2), false, 1.6) };
        All["paintpalette"] = new[]
        {
            new Part(G("M12,3 C6.8,3 2.8,6.9 2.8,11.8 C2.8,16.9 6.9,21 11.6,21 C13.2,21 14,20 13.5,18.7 C13,17.4 13.7,16 15.5,16 H17.4 C20,16 21.6,14.4 21.6,11.8 C21.6,6.9 17.2,3 12,3 Z"), false, 1.6),
            new Part(Union(Circle(7.4, 11.3, 1.45), Circle(9.4, 7.3, 1.45), Circle(14.2, 6.9, 1.45), Circle(17.6, 10, 1.45)), true),
        };
        All["rectangle.3.offgrid"] = new[] { new Part(Union(Rect(2.8, 4, 10.4, 7, 1.6), Rect(2.8, 13.2, 6.8, 6.8, 1.6), Rect(15.2, 4, 6, 16, 1.6)), false, 1.6) };
        All["opticaldisc"] = new[]
        {
            new Part(Union(Circle(12, 12, 9.4), Circle(12, 12, 2.6)), false, 1.6),
            new Part(G("M7,8.6 A6.2,6.2 0 0 1 10.3,6"), false, 1.3),
        };
        All["textformat.size.smaller"] = new[] { new Part(G("M7.2,18 L10.8,9.6 L14.4,18 M8.6,15 H13"), false, 1.7) };
        All["textformat.size.larger"] = new[] { new Part(G("M4,20 L12,3.6 L20,20 M6.9,14.6 H17.1"), false, 1.9) };
        All["text.badge.xmark"] = new[] { new Part(G("M3,6 H16 M3,11 H12.5 M3,16 H9 M15.4,13.6 L20.6,18.8 M20.6,13.6 L15.4,18.8"), false, 1.8) };
        All["wifi.exclamationmark"] = new[]
        {
            new Part(G("M2.4,9.6 C7.8,4.6 16.2,4.6 21.6,9.6 M5.6,12.9 C9.2,9.7 14.8,9.7 18.4,12.9 M8.8,16.1 C10.6,14.6 13.4,14.6 15.2,16.1"), false, 1.8),
            new Part(Circle(12, 19.4, 1.4), true),
        };
        All["chevron.up.chevron.down"] = new[] { new Part(G("M8.5,9.6 L12,6.1 L15.5,9.6 M8.5,14.4 L12,17.9 L15.5,14.4"), false, 1.8) };
    }

    public static Part[] Get(string name) => All.TryGetValue(name, out var parts) ? parts : All["music.note"];

    /// Renders a symbol into a drawing (for tray icons etc.).
    public static void Draw(DrawingContext dc, string name, Brush brush, Rect box, double weight = 1)
    {
        double s = Math.Min(box.Width, box.Height) / 24;
        dc.PushTransform(new TranslateTransform(box.X + (box.Width - 24 * s) / 2, box.Y + (box.Height - 24 * s) / 2));
        dc.PushTransform(new ScaleTransform(s, s));
        foreach (var part in Get(name))
        {
            if (part.Fill) dc.DrawGeometry(brush, null, part.Geometry);
            else dc.DrawGeometry(null, new Pen(brush, part.Stroke * weight) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, part.Geometry);
        }
        dc.Pop();
        dc.Pop();
    }
}

/// An SF Symbol look-alike, sized like text (`Size` ≈ font size in points)
/// and coloured with the inherited text colour.
public sealed class Symbol : FrameworkElement
{
    public static readonly DependencyProperty SymbolNameProperty =
        DependencyProperty.Register(nameof(SymbolName), typeof(string), typeof(Symbol),
            new FrameworkPropertyMetadata("music.note", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(Symbol),
            new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WeightProperty =
        DependencyProperty.Register(nameof(Weight), typeof(double), typeof(Symbol),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(Symbol),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string SymbolName { get => (string)GetValue(SymbolNameProperty); set => SetValue(SymbolNameProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// Stroke weight: 1 = regular, 1.2 ≈ semibold, 1.35 ≈ bold.
    public double Weight { get => (double)GetValue(WeightProperty); set => SetValue(WeightProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public Symbol() { SnapsToDevicePixels = false; }

    public Symbol(string name, double size, double weight = 1) : this()
    {
        SymbolName = name;
        Size = size;
        Weight = weight;
    }

    private double Box => Size * 1.2;

    protected override Size MeasureOverride(Size available) => new(Box, Box);

    protected override void OnRender(DrawingContext dc)
    {
        double box = Box;
        var rect = new Rect((ActualWidth - box) / 2, (ActualHeight - box) / 2, box, box);
        Symbols.Draw(dc, SymbolName, Foreground, rect, Weight);
    }
}
