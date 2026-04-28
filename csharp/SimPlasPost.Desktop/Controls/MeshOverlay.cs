using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Models;
using SimPlasPost.Desktop.ViewModels;

namespace SimPlasPost.Desktop.Controls;

/// <summary>
/// Transparent Avalonia overlay drawn ON TOP of the Veldrid GL surface.
/// Renders the color bar, axis triad, and info string using Avalonia's
/// vector text/path APIs so they keep crisp rasterization on HiDPI displays.
/// Pointer events bubble up to the parent <see cref="MeshViewport"/>.
/// </summary>
public class MeshOverlay : Control
{
    private static readonly FontFamily SciFont = new("STIX Two Text, CMU Serif, Latin Modern Roman, Times New Roman, serif");
    private static readonly Typeface SciBold = new(SciFont, FontStyle.Normal, FontWeight.Bold);

    private MainViewModel? _vm;

    public MeshOverlay()
    {
        // Don't intercept pointer events — let the parent handle pan/rotate.
        IsHitTestVisible = false;
    }

    public void SetViewModel(MainViewModel vm)
    {
        if (_vm != null) _vm.SceneInvalidated -= OnInvalidated;
        _vm = vm;
        _vm.SceneInvalidated += OnInvalidated;
        InvalidateVisual();
    }

    private void OnInvalidated() => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_vm == null) return;
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);

        if (!string.IsNullOrEmpty(_vm.ActiveField) && _vm.DisplayMode_ != DisplayMode.Wireframe)
            DrawColorBar(context, bounds);
        DrawTriad(context, bounds);
        if (!string.IsNullOrEmpty(_vm.Info))
        {
            var text = new FormattedText(_vm.Info, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, 13, Brushes.Gray);
            context.DrawText(text, new Point(10, 10));
        }
    }

    private void DrawColorBar(DrawingContext ctx, Rect bounds)
    {
        if (_vm == null) return;
        const double LabelFontSize = 15;
        const double TitleFontSize = 18;
        double bh = Math.Min(260, bounds.Height - 80), bw = 22;
        double by = (bounds.Height - bh) / 2, barX = bounds.Width - bw - 110, labelRight = barX - 8;
        var lb = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        for (int i = 0; i < 64; i++)
        {
            double t = i / 63.0; var (r, g, b) = TurboColormap.Sample(t);
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255))),
                new Rect(barX, by + bh - (i + 1) * bh / 64, bw, bh / 64 + 0.5));
        }
        ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 0.5), new Rect(barX - 0.5, by - 0.5, bw + 1, bh + 1));
        for (int i = 0; i < 6; i++)
        {
            double t = i / 5.0, v = _vm.FRangeMax - t * (_vm.FRangeMax - _vm.FRangeMin);
            var text = new FormattedText(v.ToString("E2"), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, SciBold, LabelFontSize, lb);
            ctx.DrawText(text, new Point(labelRight - text.Width, by + t * bh - text.Height / 2));
        }
        var ft = new FormattedText(_vm.ActiveField, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, SciBold, TitleFontSize, lb);
        using (ctx.PushTransform(Matrix.CreateTranslation(barX + bw + 20, by + bh / 2 + ft.Width / 2)))
        using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2)))
            ctx.DrawText(ft, new Point(0, 0));
    }

    private void DrawTriad(DrawingContext ctx, Rect bounds)
    {
        if (_vm == null) return;
        var rot = _vm.Camera.Rot;
        double cx = 60, cy = bounds.Height - 60, al = 50;
        var axes = new[] {
            (rot[0] * al, -rot[3] * al, Color.FromRgb(220, 40, 40), "X₁"),
            (rot[1] * al, -rot[4] * al, Color.FromRgb(40, 170, 40), "X₂"),
            (rot[2] * al, -rot[5] * al, Color.FromRgb(40, 80, 220), "X₃"),
        };
        foreach (var (dx, dy, col, lbl) in axes)
        {
            var tip = new Point(cx + dx, cy + dy);
            ctx.DrawLine(new Pen(new SolidColorBrush(col), 2.5), new Point(cx, cy), tip);
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 5)
            {
                double ux = dx / len, uy = dy / len, px = -uy, py = ux;
                var hg = new StreamGeometry(); using (var c = hg.Open()) { c.BeginFigure(tip, true); c.LineTo(new Point(tip.X - ux * 10 + px * 4, tip.Y - uy * 10 + py * 4)); c.LineTo(new Point(tip.X - ux * 10 - px * 4, tip.Y - uy * 10 - py * 4)); c.EndFigure(true); }
                ctx.DrawGeometry(new SolidColorBrush(col), null, hg);
            }
            var lt = new FormattedText(lbl, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, SciBold, 17, new SolidColorBrush(col));
            double lox = len > 5 ? dx / len * 14 : 7, loy = len > 5 ? dy / len * 14 : -7;
            ctx.DrawText(lt, new Point(tip.X + lox - lt.Width / 2, tip.Y + loy - lt.Height / 2));
        }
    }
}
