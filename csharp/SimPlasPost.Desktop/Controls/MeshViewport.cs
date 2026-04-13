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
/// Custom Avalonia control: arcball-based mesh viewport with STIX Two fonts.
/// </summary>
public class MeshViewport : Control
{
    // Font for scientific labels (STIX Two Text — Computer Modern equivalent)
    private static readonly FontFamily SciFont = new("avares://SimPlasPost.Desktop/Fonts#STIX Two Text");
    private static readonly Typeface SciBold = new(SciFont, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface SciRegular = new(SciFont, FontStyle.Normal, FontWeight.Normal);

    private MainViewModel? _vm;
    private ExportScene? _cachedScene;

    // Mouse state
    private bool _dragging;
    private bool _panning; // true = right-button pan
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

    // ─── Rendering ───
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, bounds);

        if (_cachedScene == null) return;
        var scene = _cachedScene;

        // Faces (painter's algorithm — already sorted back-to-front)
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

        // Visible wireframe edges
        if (scene.VisibleEdges.Count > 0)
        {
            var edgePen = new Pen(new SolidColorBrush(Color.FromArgb(
                (byte)(scene.Lp.Opacity * 255), 34, 34, 34)), scene.Lp.SvgWidth);
            foreach (var e in scene.VisibleEdges)
                context.DrawLine(edgePen, new Point(e.P1[0], e.P1[1]), new Point(e.P2[0], e.P2[1]));
        }

        // Contour lines
        foreach (var c in scene.Contours)
        {
            var color = Color.FromRgb((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255));
            var pen = new Pen(new SolidColorBrush(color), 1.0);
            context.DrawLine(pen, new Point(c.P1[0], c.P1[1]), new Point(c.P2[0], c.P2[1]));
        }

        // Color bar with STIX fonts
        if (!string.IsNullOrEmpty(scene.FieldName))
            DrawColorBar(context, scene, bounds);

        // Info overlay
        if (_vm != null && !string.IsNullOrEmpty(_vm.Info))
        {
            var text = new FormattedText(_vm.Info, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, 13, Brushes.Gray);
            context.DrawText(text, new Point(10, 10));
        }

        // Help text
        var helpText = new FormattedText("Drag: rotate \u00b7 Right-drag: pan \u00b7 Scroll: zoom",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, SciBold, 12,
            new SolidColorBrush(Color.FromRgb(170, 170, 170)));
        context.DrawText(helpText, new Point(bounds.Width - helpText.Width - 14, bounds.Height - 24));
    }

    private static void DrawColorBar(DrawingContext context, ExportScene scene, Rect bounds)
    {
        double bx = bounds.Width - 55, bw = 18, bh = 220;
        double by = bounds.Height / 2 - bh / 2;
        int nSteps = 64, nLabels = 6;

        for (int i = 0; i < nSteps; i++)
        {
            double t = i / (double)(nSteps - 1);
            var (r, g, b) = TurboColormap.Sample(t);
            double ry = by + bh - (i + 1) * bh / (double)nSteps;
            var color = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
            context.FillRectangle(new SolidColorBrush(color),
                new Rect(bx, ry, bw, bh / (double)nSteps + 0.5));
        }

        context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(85, 85, 85)), 0.5),
            new Rect(bx - 0.5, by - 0.5, bw + 1, bh + 1));

        var labelBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        for (int i = 0; i < nLabels; i++)
        {
            double t = i / (double)(nLabels - 1);
            double v = scene.FMax - t * (scene.FMax - scene.FMin);
            double ly = by + t * bh;
            var text = new FormattedText(v.ToString("E2"), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, 11, labelBrush);
            context.DrawText(text, new Point(bx - text.Width - 5, ly - text.Height / 2));
        }

        var fieldText = new FormattedText(scene.FieldName!, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, SciBold, 13, labelBrush);
        using (context.PushTransform(Matrix.CreateTranslation(bx + bw + 18, by + bh / 2 + fieldText.Width / 2)))
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2)))
        {
            context.DrawText(fieldText, new Point(0, 0));
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
            cam.Tx += dx * 0.003 * cam.Dist;
            cam.Ty -= dy * 0.003 * cam.Dist;
        }
        else
        {
            // Arcball rotation: map screen coords to [-1,1] sphere
            double w = Bounds.Width, h = Bounds.Height;
            double dim = Math.Min(w, h);
            double x0 = (2.0 * _lastMouse.X - w) / dim;
            double y0 = -(2.0 * _lastMouse.Y - h) / dim;
            double x1 = (2.0 * pos.X - w) / dim;
            double y1 = -(2.0 * pos.Y - h) / dim;

            var delta = CameraParams.ArcballDelta(x0, y0, x1, y1);
            // Apply rotation: new_rot = delta * current_rot
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
        _vm.Camera.Dist *= e.Delta.Y < 0 ? 1.08 : 0.92;
        _vm.Camera.Dist = Math.Clamp(_vm.Camera.Dist, 0.3, 20);
        RebuildScene();
        e.Handled = true;
    }
}
