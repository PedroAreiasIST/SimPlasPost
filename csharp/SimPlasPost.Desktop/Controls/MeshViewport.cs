using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Models;
using SimPlasPost.Desktop.ViewModels;

namespace SimPlasPost.Desktop.Controls;

/// <summary>
/// Custom Avalonia control: arcball-based mesh viewport with axis triad,
/// non-overlapping color bar, and STIX Two scientific fonts.
/// </summary>
public class MeshViewport : Control
{
    // System font fallback chain: best scientific font available on the system
    private static readonly FontFamily SciFont = new("STIX Two Text, CMU Serif, Latin Modern Roman, Times New Roman, serif");
    private static readonly Typeface SciBold = new(SciFont, FontStyle.Normal, FontWeight.Bold);

    private MainViewModel? _vm;
    private ExportScene? _cachedScene;

    private bool _dragging;
    private bool _panning;
    private Point _lastMouse;

    public void SetViewModel(MainViewModel vm)
    {
        if (_vm != null) _vm.SceneInvalidated -= OnSceneInvalidated;
        _vm = vm;
        _vm.SceneInvalidated += OnSceneInvalidated;
        RebuildScene();
    }

    private void OnSceneInvalidated() => Dispatcher.UIThread.Post(RebuildScene);

    private void RebuildScene()
    {
        if (_vm == null || Bounds.Width < 1 || Bounds.Height < 1) return;
        _cachedScene = _vm.BuildScene((int)Bounds.Width, (int)Bounds.Height);
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RebuildScene();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, bounds);

        if (_cachedScene == null || _vm == null) return;
        var scene = _cachedScene;

        // ── Faces ──
        foreach (var face in scene.Faces)
        {
            if (face.ScreenPts.Length < 3) continue;
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(new Point(face.ScreenPts[0][0], face.ScreenPts[0][1]), true);
                for (int i = 1; i < face.ScreenPts.Length; i++)
                    ctx.LineTo(new Point(face.ScreenPts[i][0], face.ScreenPts[i][1]));
                ctx.EndFigure(true);
            }
            var color = Color.FromRgb((byte)(face.R * 255), (byte)(face.G * 255), (byte)(face.B * 255));
            context.DrawGeometry(new SolidColorBrush(color), null, geom);
        }

        // ── Wireframe edges ──
        if (scene.VisibleEdges.Count > 0)
        {
            var edgePen = new Pen(new SolidColorBrush(Color.FromArgb(
                (byte)(scene.Lp.Opacity * 255), 34, 34, 34)), scene.Lp.SvgWidth);
            foreach (var e in scene.VisibleEdges)
                context.DrawLine(edgePen, new Point(e.P1[0], e.P1[1]), new Point(e.P2[0], e.P2[1]));
        }

        // ── Contour lines ──
        foreach (var c in scene.Contours)
        {
            var color = Color.FromRgb((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255));
            var pen = new Pen(new SolidColorBrush(color), 1.0);
            context.DrawLine(pen, new Point(c.P1[0], c.P1[1]), new Point(c.P2[0], c.P2[1]));
        }

        // ── Color bar (positioned to avoid overlap) ──
        if (!string.IsNullOrEmpty(scene.FieldName))
            DrawColorBar(context, scene, bounds);

        // ── Axis triad (bottom-left corner) ──
        DrawTriad(context, bounds);

        // ── Info text (top-left) ──
        if (!string.IsNullOrEmpty(_vm.Info))
        {
            var text = new FormattedText(_vm.Info, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, 13, Brushes.Gray);
            context.DrawText(text, new Point(10, 10));
        }

        // ── Help text (bottom-right) ──
        var helpText = new FormattedText("Drag: rotate \u00b7 Right-drag: pan \u00b7 Scroll: zoom",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, SciBold, 11,
            new SolidColorBrush(Color.FromRgb(170, 170, 170)));
        context.DrawText(helpText, new Point(bounds.Width - helpText.Width - 14, bounds.Height - 22));
    }

    // ─── Color bar: labels to the LEFT of the bar, field name rotated to the RIGHT ───
    private static void DrawColorBar(DrawingContext context, ExportScene scene, Rect bounds)
    {
        double bh = Math.Min(220, bounds.Height - 60);
        double bw = 16;
        double bx = bounds.Width - 30;       // bar right edge near viewport edge
        double by = (bounds.Height - bh) / 2; // vertically centered
        int nSteps = 64, nLabels = 6;

        // Measure widest label so we position the bar to its right
        var labelBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        double maxLabelW = 0;
        for (int i = 0; i < nLabels; i++)
        {
            double t = i / (double)(nLabels - 1);
            double v = scene.FMax - t * (scene.FMax - scene.FMin);
            var text = new FormattedText(v.ToString("E2"), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, 10, labelBrush);
            maxLabelW = Math.Max(maxLabelW, text.Width);
        }

        // Position: labels | gap | bar | gap | field name
        double barX = bounds.Width - bw - 30;          // leave room for rotated field name
        double labelsRight = barX - 5;                   // labels end 5px left of bar

        // Gradient strips
        for (int i = 0; i < nSteps; i++)
        {
            double t = i / (double)(nSteps - 1);
            var (r, g, b) = TurboColormap.Sample(t);
            double ry = by + bh - (i + 1) * bh / nSteps;
            var color = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
            context.FillRectangle(new SolidColorBrush(color), new Rect(barX, ry, bw, bh / nSteps + 0.5));
        }

        // Border
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 0.5),
            new Rect(barX - 0.5, by - 0.5, bw + 1, bh + 1));

        // Labels (right-aligned, to the left of the bar)
        for (int i = 0; i < nLabels; i++)
        {
            double t = i / (double)(nLabels - 1);
            double v = scene.FMax - t * (scene.FMax - scene.FMin);
            double ly = by + t * bh;
            var text = new FormattedText(v.ToString("E2"), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, 10, labelBrush);
            context.DrawText(text, new Point(labelsRight - text.Width, ly - text.Height / 2));
        }

        // Field name (rotated 90 CCW, to the right of the bar)
        var fieldText = new FormattedText(scene.FieldName!, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, SciBold, 12, labelBrush);
        double fnX = barX + bw + 14;
        double fnY = by + bh / 2;
        using (context.PushTransform(Matrix.CreateTranslation(fnX, fnY + fieldText.Width / 2)))
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2)))
        {
            context.DrawText(fieldText, new Point(0, 0));
        }
    }

    // ─── Axis triad (bottom-left): colored arrows with X₁, X₂, X₃ labels ───
    private void DrawTriad(DrawingContext context, Rect bounds)
    {
        if (_vm == null) return;
        var rot = _vm.Camera.Rot;

        // Triad center in bottom-left corner
        double cx = 50, cy = bounds.Height - 50;
        double axLen = 32; // arrow length in pixels

        // Axis directions in screen space via rotation matrix
        // Row 0 = right, Row 1 = up, Row 2 = forward
        // Screen X = dot(axis, right), Screen Y = -dot(axis, up)
        var axes = new (double dx, double dy, Color color, string label)[]
        {
            // X axis (1,0,0)
            (rot[0] * axLen, -rot[3] * axLen, Color.FromRgb(220, 40, 40), "X\u2081"),
            // Y axis (0,1,0)
            (rot[1] * axLen, -rot[4] * axLen, Color.FromRgb(40, 170, 40), "X\u2082"),
            // Z axis (0,0,1)
            (rot[2] * axLen, -rot[5] * axLen, Color.FromRgb(40, 80, 220), "X\u2083"),
        };

        foreach (var (dx, dy, color, label) in axes)
        {
            var pen = new Pen(new SolidColorBrush(color), 2.0);
            var tip = new Point(cx + dx, cy + dy);
            context.DrawLine(pen, new Point(cx, cy), tip);

            // Arrowhead
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 5)
            {
                double ux = dx / len, uy = dy / len;
                double px = -uy, py = ux; // perpendicular
                double hs = 6; // head size
                var h1 = new Point(tip.X - ux * hs + px * hs * 0.4, tip.Y - uy * hs + py * hs * 0.4);
                var h2 = new Point(tip.X - ux * hs - px * hs * 0.4, tip.Y - uy * hs - py * hs * 0.4);
                var headGeom = new StreamGeometry();
                using (var ctx = headGeom.Open())
                {
                    ctx.BeginFigure(tip, true);
                    ctx.LineTo(h1);
                    ctx.LineTo(h2);
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(new SolidColorBrush(color), null, headGeom);
            }

            // Label at tip
            var labelText = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, 11, new SolidColorBrush(color));
            double labelOffX = len > 5 ? dx / len * 10 : 5;
            double labelOffY = len > 5 ? dy / len * 10 : -5;
            context.DrawText(labelText, new Point(
                tip.X + labelOffX - labelText.Width / 2,
                tip.Y + labelOffY - labelText.Height / 2));
        }
    }

    // ─── Arcball mouse interaction ───
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm == null) return;
        _dragging = true;
        _panning = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        _lastMouse = e.GetPosition(this);
        e.Handled = true;
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || _vm == null) return;

        var pos = e.GetPosition(this);
        var cam = _vm.Camera;

        if (_panning)
        {
            double dx = pos.X - _lastMouse.X;
            double dy = pos.Y - _lastMouse.Y;
            // Scale pan speed by viewport size for consistent feel
            double viewSize = Math.Max(Bounds.Width, Bounds.Height);
            double panScale = 2.0 * cam.Dist / viewSize;
            cam.Tx += dx * panScale;
            cam.Ty -= dy * panScale;
        }
        else
        {
            double w = Bounds.Width, h = Bounds.Height;
            double dim = Math.Min(w, h);
            double x0 = (2.0 * _lastMouse.X - w) / dim;
            double y0 = -(2.0 * _lastMouse.Y - h) / dim;
            double x1 = (2.0 * pos.X - w) / dim;
            double y1 = -(2.0 * pos.Y - h) / dim;

            var delta = CameraParams.ArcballDelta(x0, y0, x1, y1);
            cam.Rot = CameraParams.Mul(delta, cam.Rot);
        }

        _lastMouse = pos;
        RebuildScene();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_vm == null) return;
        // Finer zoom steps (3% per tick instead of 8%)
        _vm.Camera.Dist *= e.Delta.Y < 0 ? 1.03 : (1.0 / 1.03);
        _vm.Camera.Dist = Math.Clamp(_vm.Camera.Dist, 0.1, 50);
        RebuildScene();
        e.Handled = true;
    }
}
