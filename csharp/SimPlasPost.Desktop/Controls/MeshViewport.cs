using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Geometry;
using SimPlasPost.Core.Models;
using SimPlasPost.Core.Rendering;
using SimPlasPost.Desktop.ViewModels;
using System.Runtime.InteropServices;

namespace SimPlasPost.Desktop.Controls;

/// <summary>
/// High-performance mesh viewport using WriteableBitmap + software rasterizer.
/// Boundary extraction is cached; only reprojection happens on camera change.
/// </summary>
public class MeshViewport : Control
{
    private static readonly FontFamily SciFont = new("STIX Two Text, CMU Serif, Latin Modern Roman, Times New Roman, serif");
    private static readonly Typeface SciBold = new(SciFont, FontStyle.Normal, FontWeight.Bold);

    private MainViewModel? _vm;

    // ─── Cached geometry (rebuilt only when mesh/field/deformation changes) ───
    private MeshData? _cachedMesh;
    private string? _cachedField;
    private bool _cachedShowDef;
    private double _cachedDefScale;
    private DisplayMode _cachedMode;

    private double[][] _dp = Array.Empty<double[]>();       // displaced positions (normalized)
    private List<int[]> _bfaces = new();                     // boundary faces
    private int[] _faceOffsets = Array.Empty<int>();          // packed face offset array
    private int[] _faceVertices = Array.Empty<int>();         // packed vertex indices
    private int[] _edgePairs = Array.Empty<int>();            // wireframe edge pairs
    private byte[] _faceR = Array.Empty<byte>();
    private byte[] _faceG = Array.Empty<byte>();
    private byte[] _faceB = Array.Empty<byte>();
    private bool _is3D;

    // ─── Frame buffer (reused across frames) ───
    private WriteableBitmap? _bitmap;
    private uint[]? _pixels;
    private float[]? _zbuf;
    private int _bw, _bh;

    // ─── Mouse ───
    private bool _dragging;
    private bool _panning;
    private Point _lastMouse;

    public void SetViewModel(MainViewModel vm)
    {
        if (_vm != null) _vm.SceneInvalidated -= OnSceneInvalidated;
        _vm = vm;
        _vm.SceneInvalidated += OnSceneInvalidated;
        RebuildGeometry();
    }

    private void OnSceneInvalidated() => Dispatcher.UIThread.Post(() =>
    {
        if (GeometryChanged()) RebuildGeometry();
        InvalidateVisual();
    });

    private bool GeometryChanged() => _vm != null && (
        _vm.MeshData != _cachedMesh ||
        _vm.ActiveField != _cachedField ||
        _vm.ShowDef != _cachedShowDef ||
        _vm.DefScale != _cachedDefScale ||
        _vm.DisplayMode_ != _cachedMode);

    /// <summary>Rebuild cached geometry. Called only when mesh/field/deformation changes.</summary>
    private void RebuildGeometry()
    {
        if (_vm?.MeshData == null) return;
        var mesh = _vm.MeshData;
        var dMode = _vm.DisplayMode_;
        _cachedMesh = mesh; _cachedField = _vm.ActiveField;
        _cachedShowDef = _vm.ShowDef; _cachedDefScale = _vm.DefScale;
        _cachedMode = dMode;

        var ns = mesh.Nodes;

        // Bounding box + normalize
        double mnX = double.MaxValue, mnY = double.MaxValue, mnZ = double.MaxValue;
        double mxX = double.MinValue, mxY = double.MinValue, mxZ = double.MinValue;
        for (int i = 0; i < ns.Length; i++)
        {
            var n = ns[i];
            mnX = Math.Min(mnX, n[0]); mxX = Math.Max(mxX, n[0]);
            mnY = Math.Min(mnY, n[1]); mxY = Math.Max(mxY, n[1]);
            mnZ = Math.Min(mnZ, n[2]); mxZ = Math.Max(mxZ, n[2]);
        }
        double cenX = (mnX + mxX) / 2, cenY = (mnY + mxY) / 2, cenZ = (mnZ + mxZ) / 2;
        double span = Math.Max(Math.Max(mxX - mnX, mxY - mnY), Math.Max(mxZ - mnZ, 1e-12));
        double sc = 2.0 / span;

        // Displaced positions
        var dispField = mesh.GetDisplacementField();
        _dp = new double[ns.Length][];
        double defScale = _vm.DefScale;
        bool showDef = _vm.ShowDef;
        for (int i = 0; i < ns.Length; i++)
        {
            var n = ns[i];
            double dx = 0, dy = 0, dz = 0;
            if (showDef && dispField is { IsVector: true, VectorValues: not null } && i < dispField.VectorValues.Length)
            {
                var d = dispField.VectorValues[i];
                dx = d[0] * defScale; dy = d[1] * defScale; dz = d[2] * defScale;
            }
            _dp[i] = new[] { (n[0] + dx - cenX) * sc, (n[1] + dy - cenY) * sc, (n[2] + dz - cenZ) * sc };
        }

        _is3D = mesh.Dim == 3 || mesh.Elements.Any(e =>
            FaceTable.Faces.TryGetValue(e.Type, out var ft) && ft.Dim == 3);
        _bfaces = BoundaryExtractor.Extract(mesh.Elements, _is3D);

        // Field values
        double[]? fv = null;
        double fmin = 0, fmax = 1;
        if (dMode != DisplayMode.Wireframe && !string.IsNullOrEmpty(_vm.ActiveField) &&
            mesh.Fields.TryGetValue(_vm.ActiveField, out var field) && !field.IsVector)
        {
            fv = field.ScalarValues;
            if (fv != null && fv.Length > 0)
            {
                fmin = double.MaxValue; fmax = double.MinValue;
                foreach (double v in fv) { fmin = Math.Min(fmin, v); fmax = Math.Max(fmax, v); }
                if (Math.Abs(fmax - fmin) < 1e-15) fmax = fmin + 1;
            }
        }
        double efMin = double.TryParse(_vm.UserMin, out var mn) ? mn : fmin;
        double efMax = double.TryParse(_vm.UserMax, out var mx) ? mx : fmax;
        double efSpan = Math.Abs(efMax - efMin) < 1e-15 ? 1 : efMax - efMin;

        // Build packed face arrays + per-face colors
        var offsets = new List<int> { 0 };
        var verts = new List<int>();
        var fr = new List<byte>(); var fg = new List<byte>(); var fb = new List<byte>();
        var edgeList = new List<int>();

        foreach (var face in _bfaces)
        {
            foreach (int ni in face) verts.Add(ni);
            offsets.Add(verts.Count);

            byte cr, cg, cb;
            if (dMode == DisplayMode.Wireframe)
            {
                cr = 255; cg = 255; cb = 255;
            }
            else if (fv != null)
            {
                double avgF = 0; foreach (int ni in face) avgF += fv[ni]; avgF /= face.Length;
                double t = (avgF - efMin) / efSpan;
                var (r, g, b) = TurboColormap.Sample(t);
                cr = (byte)(r * 255); cg = (byte)(g * 255); cb = (byte)(b * 255);
            }
            else { cr = 191; cg = 199; cb = 209; }

            fr.Add(cr); fg.Add(cg); fb.Add(cb);

            // Wireframe/plot edges
            if (dMode == DisplayMode.Wireframe || dMode == DisplayMode.Plot)
            {
                for (int j = 0; j < face.Length; j++)
                {
                    edgeList.Add(face[j]);
                    edgeList.Add(face[(j + 1) % face.Length]);
                }
            }
        }

        _faceOffsets = offsets.ToArray();
        _faceVertices = verts.ToArray();
        _faceR = fr.ToArray(); _faceG = fg.ToArray(); _faceB = fb.ToArray();
        _edgePairs = edgeList.ToArray();

        _vm.FRangeMin = fmin; _vm.FRangeMax = fmax;
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _bitmap = null; // force realloc
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        int w = (int)Bounds.Width, h = (int)Bounds.Height;
        if (w < 2 || h < 2 || _vm == null || _dp.Length == 0) return;

        // Allocate/resize frame buffer
        if (_bitmap == null || _bw != w || _bh != h)
        {
            _bw = w; _bh = h;
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888);
            _pixels = new uint[w * h];
            _zbuf = new float[w * h];
        }

        // Project all vertices to screen space (the ONLY per-frame work for camera changes)
        var cam = Camera.Build(_vm.Camera);
        double orthoHH = _vm.Camera.Dist;
        double scale = h / (2.0 * orthoHH);
        double hw = w / 2.0, hh = h / 2.0;

        var screenPts = new double[_dp.Length][];
        for (int i = 0; i < _dp.Length; i++)
        {
            var p = _dp[i];
            double rx = p[0] - cam.Eye.X, ry = p[1] - cam.Eye.Y, rz = p[2] - cam.Eye.Z;
            double sx = hw + (rx * cam.Right.X + ry * cam.Right.Y + rz * cam.Right.Z) * scale;
            double sy = hh - (rx * cam.Up.X + ry * cam.Up.Y + rz * cam.Up.Z) * scale;
            double sz = rx * cam.Forward.X + ry * cam.Forward.Y + rz * cam.Forward.Z;
            screenPts[i] = new[] { sx, sy, sz };
        }

        // Rasterize faces + edges to pixel buffer
        BitmapRenderer.RenderFaces(_pixels!, _zbuf!, w, h, screenPts, _faceOffsets, _faceVertices, _faceR, _faceG, _faceB);
        if (_edgePairs.Length > 0)
            BitmapRenderer.RenderEdges(_pixels!, w, h, screenPts, _edgePairs, 0xFF333333, _zbuf!, 0.02);

        // Copy pixels to WriteableBitmap
        using (var fb = _bitmap!.Lock())
        {
            Marshal.Copy((int[])(object)_pixels!, 0, fb.Address, _pixels!.Length);
        }

        // Draw bitmap
        context.DrawImage(_bitmap, new Rect(new Point(0, 0), new Size(w, h)));

        // Overlays (lightweight Avalonia drawing on top)
        var bounds = new Rect(0, 0, w, h);

        // Color bar
        if (!string.IsNullOrEmpty(_vm.ActiveField) && _cachedMode != DisplayMode.Wireframe)
            DrawColorBar(context, bounds);

        // Axis triad
        DrawTriad(context, bounds);

        // Info text
        if (!string.IsNullOrEmpty(_vm.Info))
        {
            var text = new FormattedText(_vm.Info, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, 13, Brushes.Gray);
            context.DrawText(text, new Point(10, 10));
        }

        // Help
        var help = new FormattedText("Drag: rotate \u00b7 Right-drag: pan \u00b7 Scroll: zoom",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, SciBold, 11, new SolidColorBrush(Color.FromRgb(170, 170, 170)));
        context.DrawText(help, new Point(bounds.Width - help.Width - 14, bounds.Height - 22));
    }

    private void DrawColorBar(DrawingContext context, Rect bounds)
    {
        if (_vm == null) return;
        double bh = Math.Min(220, bounds.Height - 60), bw = 16;
        double by = (bounds.Height - bh) / 2;
        double barX = bounds.Width - bw - 30;
        double labelRight = barX - 6;
        int nSteps = 64, nLabels = 6;
        var labelBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51));

        for (int i = 0; i < nSteps; i++)
        {
            double t = i / (double)(nSteps - 1);
            var (r, g, b) = TurboColormap.Sample(t);
            double ry = by + bh - (i + 1) * bh / nSteps;
            context.FillRectangle(new SolidColorBrush(Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255))),
                new Rect(barX, ry, bw, bh / nSteps + 0.5));
        }
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 0.5),
            new Rect(barX - 0.5, by - 0.5, bw + 1, bh + 1));

        for (int i = 0; i < nLabels; i++)
        {
            double t = i / (double)(nLabels - 1);
            double v = _vm.FRangeMax - t * (_vm.FRangeMax - _vm.FRangeMin);
            double ly = by + t * bh;
            var text = new FormattedText(v.ToString("E2"), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, 10, labelBrush);
            context.DrawText(text, new Point(labelRight - text.Width, ly - text.Height / 2));
        }

        var fieldText = new FormattedText(_vm.ActiveField, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, SciBold, 12, labelBrush);
        double fnX = barX + bw + 14;
        using (context.PushTransform(Matrix.CreateTranslation(fnX, by + bh / 2 + fieldText.Width / 2)))
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2)))
            context.DrawText(fieldText, new Point(0, 0));
    }

    private void DrawTriad(DrawingContext context, Rect bounds)
    {
        if (_vm == null) return;
        var rot = _vm.Camera.Rot;
        double cx = 60, cy = bounds.Height - 60, axLen = 50;

        var axes = new (double dx, double dy, Color color, string label)[]
        {
            (rot[0] * axLen, -rot[3] * axLen, Color.FromRgb(220, 40, 40), "X\u2081"),
            (rot[1] * axLen, -rot[4] * axLen, Color.FromRgb(40, 170, 40), "X\u2082"),
            (rot[2] * axLen, -rot[5] * axLen, Color.FromRgb(40, 80, 220), "X\u2083"),
        };

        foreach (var (dx, dy, color, label) in axes)
        {
            var pen = new Pen(new SolidColorBrush(color), 2.5);
            var tip = new Point(cx + dx, cy + dy);
            context.DrawLine(pen, new Point(cx, cy), tip);
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 5)
            {
                double ux = dx / len, uy = dy / len, px = -uy, py = ux;
                var headGeom = new StreamGeometry();
                using (var ctx = headGeom.Open())
                {
                    ctx.BeginFigure(tip, true);
                    ctx.LineTo(new Point(tip.X - ux * 10 + px * 4, tip.Y - uy * 10 + py * 4));
                    ctx.LineTo(new Point(tip.X - ux * 10 - px * 4, tip.Y - uy * 10 - py * 4));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(new SolidColorBrush(color), null, headGeom);
            }
            var labelText = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, SciBold, 17, new SolidColorBrush(color));
            double lox = len > 5 ? dx / len * 14 : 7, loy = len > 5 ? dy / len * 14 : -7;
            context.DrawText(labelText, new Point(tip.X + lox - labelText.Width / 2, tip.Y + loy - labelText.Height / 2));
        }
    }

    // ─── Arcball + screen-space pan ───
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm == null) return;
        _dragging = true;
        _panning = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        _lastMouse = e.GetPosition(this);
        e.Handled = true; Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || _vm == null) return;
        var pos = e.GetPosition(this);
        var cam = _vm.Camera;

        if (_panning)
        {
            double dx = pos.X - _lastMouse.X, dy = pos.Y - _lastMouse.Y;
            double viewSize = Math.Max(Bounds.Width, Bounds.Height);
            double panScale = 2.0 * cam.Dist / viewSize;
            var rot = cam.Rot;
            cam.Tx += (dx * rot[0] - dy * rot[3]) * panScale;
            cam.Ty += (dx * rot[1] - dy * rot[4]) * panScale;
            cam.Tz += (dx * rot[2] - dy * rot[5]) * panScale;
        }
        else
        {
            double w = Bounds.Width, h = Bounds.Height, dim = Math.Min(w, h);
            var delta = CameraParams.ArcballDelta(
                (2.0 * _lastMouse.X - w) / dim, -(2.0 * _lastMouse.Y - h) / dim,
                (2.0 * pos.X - w) / dim, -(2.0 * pos.Y - h) / dim);
            cam.Rot = CameraParams.Mul(delta, cam.Rot);
        }

        _lastMouse = pos;
        InvalidateVisual(); // just repaint — no geometry rebuild needed for camera change
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false; e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_vm == null) return;
        _vm.Camera.Dist *= e.Delta.Y < 0 ? 1.03 : (1.0 / 1.03);
        _vm.Camera.Dist = Math.Clamp(_vm.Camera.Dist, 0.1, 50);
        InvalidateVisual();
        e.Handled = true;
    }
}
