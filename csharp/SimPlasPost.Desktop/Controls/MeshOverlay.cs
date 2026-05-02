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

        if (!string.IsNullOrEmpty(_vm.ActiveField) && _vm.DisplayMode_ != DisplayMode.Wireframe)
            DrawColorBar(context, bounds);
        // Contour labels are drawn after the GL surface but BEFORE the
        // axis triad / info text, so the latter always stay legible at
        // the corners regardless of where iso-lines cluster.
        DrawContourLabels(context, bounds);
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
        if (!_vm.ShowContourLabels || _vm.DisplayMode_ != DisplayMode.Lines) return;
        var labels = _vm.ContourLabelsWorld;
        if (labels.Count == 0) return;
        if (string.IsNullOrEmpty(_vm.ActiveField)) return;

        int w = (int)Math.Max(1, bounds.Width);
        int h = (int)Math.Max(1, bounds.Height);
        var cam = Camera.Build(_vm.Camera);
        double orthoHH = _vm.Camera.Dist;

        // Sort longest-first so big iso-lines preempt smaller ones in case
        // of overlap; ToList materialises so we don't re-enumerate the
        // shared VM list while iterating.
        var sorted = labels.OrderByDescending(l => l.Length).ToList();

        const double textSize = 11;
        var bgBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        var fgBrush = new SolidColorBrush(Color.FromRgb(40, 40, 40));

        // We collect bounding rectangles of placed labels and skip any new
        // one whose axis-aligned bbox intersects any of them.  Margin
        // (in pixels) keeps adjacent labels from kissing.
        const double Margin = 6;
        var placedBoxes = new List<Rect>();

        foreach (var label in sorted)
        {
            // Project the anchor.
            var center = Camera.Project(label.Pos, cam, orthoHH, w, h);
            double cx = center[0], cy = center[1];

            // Cull off-screen anchors (with a small inset).
            if (cx < 16 || cx > w - 16 || cy < 16 || cy > h - 16) continue;

            // Project an offset along the world tangent so we can read off
            // the screen rotation.  Use a small but non-trivial offset so
            // the projected delta stays well-behaved on near-edge-on lines.
            const double tEps = 0.05;
            var tipWorld = new[]
            {
                label.Pos[0] + label.TangentDir[0] * tEps,
                label.Pos[1] + label.TangentDir[1] * tEps,
                label.Pos[2] + label.TangentDir[2] * tEps,
            };
            var tip = Camera.Project(tipWorld, cam, orthoHH, w, h);
            double dx = tip[0] - cx, dy = tip[1] - cy;
            double dlen2 = dx * dx + dy * dy;
            if (dlen2 < 4.0)
            {
                // Tangent is nearly parallel to the view direction, so the
                // iso-line projects almost to a point — skip the label.
                continue;
            }
            double angle = Math.Atan2(dy, dx);
            // Keep text right-side-up: flip orientations beyond ±90°.
            if (angle > Math.PI / 2) angle -= Math.PI;
            else if (angle < -Math.PI / 2) angle += Math.PI;

            var formatted = new FormattedText(label.Text,
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                SciBold, textSize, fgBrush);
            double textW = formatted.Width + Margin;
            double textH = formatted.Height + 2;

            // Conservative AABB: rotated label fits inside a circle of
            // radius √(w² + h²)/2 around the anchor — using that as the
            // half-extent of an AABB gives correct overlap rejection
            // independent of the rotation angle.
            double diag = Math.Sqrt(textW * textW + textH * textH) * 0.5;
            var aabb = new Rect(cx - diag, cy - diag, diag * 2, diag * 2);
            bool overlap = false;
            foreach (var p in placedBoxes)
            {
                if (aabb.Intersects(p)) { overlap = true; break; }
            }
            if (overlap) continue;
            placedBoxes.Add(aabb);

            using (ctx.PushTransform(Matrix.CreateTranslation(cx, cy)))
            using (ctx.PushTransform(Matrix.CreateRotation(angle)))
            {
                ctx.FillRectangle(bgBrush,
                    new Rect(-textW / 2, -textH / 2, textW, textH));
                ctx.DrawText(formatted,
                    new Point(-formatted.Width / 2, -formatted.Height / 2));
            }
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
