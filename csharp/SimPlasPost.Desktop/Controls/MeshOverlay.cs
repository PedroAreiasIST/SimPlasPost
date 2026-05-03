using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Models;
using SimPlasPost.Core.Rendering;
using SimPlasPost.Desktop.ViewModels;

namespace SimPlasPost.Desktop.Controls;

/// <summary>
/// Transparent Avalonia overlay drawn ON TOP of the GL surface.
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

        // Color bar is meaningful in Plot and Lines modes (where a scalar
        // field actually colours the geometry).  Plain is a purely
        // structural view — drop the bar.
        if (!string.IsNullOrEmpty(_vm.ActiveField) &&
            _vm.DisplayMode_ != DisplayMode.Plain)
            DrawColorBar(context, bounds);
        // Contour labels: only meaningful in Lines mode (we don't draw
        // iso-contour lines in any other mode).
        DrawContourLabels(context, bounds);
        // Plain-mode dimensioning overlay — bounding-box extents and any
        // detected 2D hole diameters.  Re-projected each frame so the
        // labels and arrows track the camera live.
        DrawDimensions(context, bounds);
        DrawTriad(context, bounds);
        if (!string.IsNullOrEmpty(_vm.Info))
        {
            var text = new FormattedText(_vm.Info, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, 13, Brushes.Gray);
            context.DrawText(text, new Point(10, 10));
        }
    }

    /// <summary>
    /// Place contour-line labels using a per-frame algorithm:
    ///
    ///   1. Pull world-space candidates from the view-model (built once at
    ///      RebuildGeometry time, one per polyline).
    ///   2. Project each anchor — and a nearby tangent point — onto screen
    ///      space at the live camera.
    ///   3. Reject candidates whose tangent is nearly perpendicular to the
    ///      view (the iso-line is edge-on, so the label would foreshorten
    ///      to a line).
    ///   4. Derive the screen rotation from the projected tangent and flip
    ///      it if it would render the text upside-down.
    ///   5. Sort the survivors by source polyline arc length descending so
    ///      long, prominent iso-lines win the placement race.
    ///   6. Greedy non-overlap: keep a label only if its conservative
    ///      axis-aligned bounding box (radius = √(w² + h²) / 2 around the
    ///      anchor) doesn't intersect any already-placed label.
    ///   7. Draw a small white background mask under each kept label so the
    ///      iso-line crossing under the text reads as broken, then draw
    ///      the rotated text on top.
    /// </summary>
    private void DrawContourLabels(DrawingContext ctx, Rect bounds)
    {
        if (_vm == null) return;
        if (!_vm.ShowContourLabels) return;
        // Labels are meaningful only in Lines mode (the only mode that
        // draws iso-contour lines on screen).
        if (_vm.DisplayMode_ != DisplayMode.Lines) return;
        if (_vm.ContourLabelsWorld.Count == 0) return;
        if (string.IsNullOrEmpty(_vm.ActiveField)) return;

        int w = (int)Math.Max(1, bounds.Width);
        int h = (int)Math.Max(1, bounds.Height);
        var cam = Camera.Build(_vm.Camera);
        const double textSize = 11;

        // Use the shared placer so the on-screen layout matches the PDF
        // exporter byte-for-byte (modulo the actual glyph metrics, which
        // we approximate with avgCharWidth × fontSize).
        var placed = ContourLabelPlacer.Place(
            _vm.ContourLabelsWorld, cam, _vm.Camera.Dist, w, h, textSize);

        var bgBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        var fgBrush = new SolidColorBrush(Color.FromRgb(40, 40, 40));

        foreach (var label in placed)
        {
            var formatted = new FormattedText(label.Text,
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                SciBold, textSize, fgBrush);
            double textW = formatted.Width + 6;
            double textH = formatted.Height + 2;

            using (ctx.PushTransform(Matrix.CreateTranslation(label.X, label.Y)))
            using (ctx.PushTransform(Matrix.CreateRotation(label.Angle)))
            {
                ctx.FillRectangle(bgBrush,
                    new Rect(-textW / 2, -textH / 2, textW, textH));
                ctx.DrawText(formatted,
                    new Point(-formatted.Width / 2, -formatted.Height / 2));
            }
        }
    }

    /// <summary>
    /// Draw the Plain-mode dimensioning overlay: extension lines from each
    /// measured feature, an offset dimension line with arrowheads at both
    /// ends, and a value label rotated to follow the dimension line.
    ///
    /// Available only when the user is in Plain mode with mesh lines off
    /// AND the "Dimensions" checkbox is on — the VM's ShowPlainDimensions
    /// guard already enforces this combination, so the only check here is
    /// against the cached world-space dimension list (empty when the mesh
    /// has no recoverable features).
    /// </summary>
    private void DrawDimensions(DrawingContext ctx, Rect bounds)
    {
        if (_vm == null) return;
        if (!_vm.ShowPlainDimensions) return;
        if (_vm.DisplayMode_ != DisplayMode.Plain) return;
        if (_vm.ShowPlainMeshLines) return; // belt-and-braces; setter already guards
        if (_vm.DimensionsWorld.Count == 0) return;

        int w = (int)Math.Max(1, bounds.Width);
        int h = (int)Math.Max(1, bounds.Height);
        var cam = Camera.Build(_vm.Camera);
        var laid = DimensionLayout.Project(
            _vm.DimensionsWorld, cam, _vm.Camera.Dist, w, h,
            bboxCenterWorld: new[] { 0.0, 0.0, 0.0 });
        if (laid.Count == 0) return;

        var inkBrush = new SolidColorBrush(Color.FromRgb(34, 34, 34));
        // Heavier strokes than the contour overlay so the dimension layer
        // stands out as drafting annotations rather than data marks.
        var inkPenThin  = new Pen(inkBrush, 1.0);
        var inkPenThick = new Pen(inkBrush, 1.6);
        const double textSize = 17;

        foreach (var d in laid)
        {
            if (d.Kind == DimensionKind.Linear)
            {
                ctx.DrawLine(inkPenThin,
                    new Point(d.Ext1[0], d.Ext1[1]), new Point(d.Dim1[0], d.Dim1[1]));
                ctx.DrawLine(inkPenThin,
                    new Point(d.Ext2[0], d.Ext2[1]), new Point(d.Dim2[0], d.Dim2[1]));
            }

            ctx.DrawLine(inkPenThick,
                new Point(d.Dim1[0], d.Dim1[1]), new Point(d.Dim2[0], d.Dim2[1]));

            // Filled triangular arrowheads pointing inward at both ends.
            DrawArrow(ctx, inkBrush, d.Dim1, d.Dim2, atStart: true);
            DrawArrow(ctx, inkBrush, d.Dim1, d.Dim2, atStart: false);

            string txt = d.Kind == DimensionKind.Diameter
                ? $"⌀ {DimensionLayout.FormatValue(d.Value)}"
                : $"{d.Label} = {DimensionLayout.FormatValue(d.Value)}";
            var formatted = new FormattedText(txt, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, textSize, inkBrush);

            using (ctx.PushTransform(Matrix.CreateTranslation(d.LabelPos[0], d.LabelPos[1])))
            using (ctx.PushTransform(Matrix.CreateRotation(d.Rot * Math.PI / 180.0)))
            {
                ctx.DrawText(formatted,
                    new Point(-formatted.Width / 2, -formatted.Height / 2));
            }
        }
    }

    private static void DrawArrow(DrawingContext ctx, IBrush brush, double[] a, double[] b, bool atStart)
    {
        double dx = b[0] - a[0], dy = b[1] - a[1];
        double L = Math.Sqrt(dx * dx + dy * dy);
        if (L < 1e-6) return;
        double ux = dx / L, uy = dy / L;
        const double hL = 11, hW = 4.0;
        double tipX, tipY, sign;
        if (atStart) { tipX = a[0]; tipY = a[1]; sign = +1; }
        else         { tipX = b[0]; tipY = b[1]; sign = -1; }
        var tip = new Point(tipX, tipY);
        var p1 = new Point(tipX + sign * (ux * hL - uy * hW), tipY + sign * (uy * hL + ux * hW));
        var p2 = new Point(tipX + sign * (ux * hL + uy * hW), tipY + sign * (uy * hL - ux * hW));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(tip, true);
            c.LineTo(p1);
            c.LineTo(p2);
            c.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, g);
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
