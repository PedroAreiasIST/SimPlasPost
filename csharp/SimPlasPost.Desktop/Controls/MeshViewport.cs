using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Geometry;
using SimPlasPost.Core.Models;
using SimPlasPost.Core.Rendering;
using SimPlasPost.Desktop.ViewModels;
using System.Runtime.InteropServices;

namespace SimPlasPost.Desktop.Controls;

/// <summary>
/// High-performance mesh viewport: cached geometry + parallel software rasterizer
/// writing to WriteableBitmap. No external 3D library, no licensing.
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
    private string? _cachedUserMin, _cachedUserMax;

    private float[] _posX = Array.Empty<float>(); // flat position arrays for speed
    private float[] _posY = Array.Empty<float>();
    private float[] _posZ = Array.Empty<float>();
    private int _nVerts;
    private int[] _faceOffsets = Array.Empty<int>();
    private int[] _faceVertices = Array.Empty<int>();
    private int[] _edgePairs = Array.Empty<int>();
    private byte[] _faceR = Array.Empty<byte>();
    private byte[] _faceG = Array.Empty<byte>();
    private byte[] _faceB = Array.Empty<byte>();

    // ─── Frame buffer (reused) ───
    private WriteableBitmap? _bitmap;
    private uint[]? _pixels;
    private float[]? _zbuf;
    private int _bw, _bh;

    // ─── Mouse ───
    private bool _dragging, _panning;
    private Point _lastMouse;

    public void SetViewModel(MainViewModel vm)
    {
        if (_vm != null) _vm.SceneInvalidated -= OnInvalidated;
        _vm = vm;
        _vm.SceneInvalidated += OnInvalidated;
        RebuildGeometry();
    }

    private void OnInvalidated() => Dispatcher.UIThread.Post(() =>
    {
        if (GeometryChanged()) RebuildGeometry();
        else InvalidateVisual();
    });

    private bool GeometryChanged() => _vm != null && (
        _vm.MeshData != _cachedMesh || _vm.ActiveField != _cachedField ||
        _vm.ShowDef != _cachedShowDef || _vm.DefScale != _cachedDefScale ||
        _vm.DisplayMode_ != _cachedMode ||
        _vm.UserMin != _cachedUserMin || _vm.UserMax != _cachedUserMax);

    /// <summary>Expensive: extract boundary, compute colors. Called rarely.</summary>
    private void RebuildGeometry()
    {
        if (_vm?.MeshData == null) return;
        var mesh = _vm.MeshData;
        var dMode = _vm.DisplayMode_;
        _cachedMesh = mesh; _cachedField = _vm.ActiveField;
        _cachedShowDef = _vm.ShowDef; _cachedDefScale = _vm.DefScale;
        _cachedMode = dMode; _cachedUserMin = _vm.UserMin; _cachedUserMax = _vm.UserMax;

        var ns = mesh.Nodes;
        float mnX = float.MaxValue, mnY = float.MaxValue, mnZ = float.MaxValue;
        float mxX = float.MinValue, mxY = float.MinValue, mxZ = float.MinValue;
        for (int i = 0; i < ns.Length; i++)
        {
            float x = (float)ns[i][0], y = (float)ns[i][1], z = (float)ns[i][2];
            if (x < mnX) mnX = x; if (x > mxX) mxX = x;
            if (y < mnY) mnY = y; if (y > mxY) mxY = y;
            if (z < mnZ) mnZ = z; if (z > mxZ) mxZ = z;
        }
        float cenX = (mnX + mxX) / 2, cenY = (mnY + mxY) / 2, cenZ = (mnZ + mxZ) / 2;
        float span = Math.Max(Math.Max(mxX - mnX, mxY - mnY), Math.Max(mxZ - mnZ, 1e-6f));
        float sc = 2f / span;

        var dispField = mesh.GetDisplacementField();
        _nVerts = ns.Length;
        _posX = new float[_nVerts]; _posY = new float[_nVerts]; _posZ = new float[_nVerts];
        float defScale = (float)_vm.DefScale;
        bool showDef = _vm.ShowDef;
        for (int i = 0; i < _nVerts; i++)
        {
            float dx = 0, dy = 0, dz = 0;
            if (showDef && dispField is { IsVector: true, VectorValues: not null } && i < dispField.VectorValues.Length)
            { var d = dispField.VectorValues[i]; dx = (float)d[0] * defScale; dy = (float)d[1] * defScale; dz = (float)d[2] * defScale; }
            _posX[i] = ((float)ns[i][0] + dx - cenX) * sc;
            _posY[i] = ((float)ns[i][1] + dy - cenY) * sc;
            _posZ[i] = ((float)ns[i][2] + dz - cenZ) * sc;
        }

        bool is3D = mesh.Dim == 3 || mesh.Elements.Any(e => FaceTable.Faces.TryGetValue(e.Type, out var ft) && ft.Dim == 3);
        var bfaces = BoundaryExtractor.Extract(mesh.Elements, is3D);

        double[]? fv = null; double fmin = 0, fmax = 1;
        if (dMode != DisplayMode.Wireframe && !string.IsNullOrEmpty(_vm.ActiveField) &&
            mesh.Fields.TryGetValue(_vm.ActiveField, out var field) && !field.IsVector)
        {
            fv = field.ScalarValues;
            if (fv != null && fv.Length > 0)
            {
                fmin = double.MaxValue; fmax = double.MinValue;
                foreach (double v in fv) { if (v < fmin) fmin = v; if (v > fmax) fmax = v; }
                if (Math.Abs(fmax - fmin) < 1e-15) fmax = fmin + 1;
            }
        }
        double efMin = double.TryParse(_vm.UserMin, out var mn) ? mn : fmin;
        double efMax = double.TryParse(_vm.UserMax, out var mx) ? mx : fmax;
        double efSpan = Math.Abs(efMax - efMin) < 1e-15 ? 1 : efMax - efMin;
        _vm.FRangeMin = fmin; _vm.FRangeMax = fmax;

        // Pack faces into flat arrays
        int totalVerts = 0; foreach (var f in bfaces) totalVerts += f.Length;
        var offsets = new int[bfaces.Count + 1];
        var verts = new int[totalVerts];
        var fr = new byte[bfaces.Count]; var fg = new byte[bfaces.Count]; var fb = new byte[bfaces.Count];
        var edges = new List<int>();
        int vi = 0;
        for (int fi = 0; fi < bfaces.Count; fi++)
        {
            var face = bfaces[fi];
            offsets[fi] = vi;
            for (int j = 0; j < face.Length; j++) verts[vi++] = face[j];

            if (dMode == DisplayMode.Wireframe) { fr[fi] = 255; fg[fi] = 255; fb[fi] = 255; }
            else if (fv != null)
            {
                double avg = 0; for (int j = 0; j < face.Length; j++) avg += fv[face[j]]; avg /= face.Length;
                var (r, g, b) = TurboColormap.Sample((avg - efMin) / efSpan);
                fr[fi] = (byte)(r * 255); fg[fi] = (byte)(g * 255); fb[fi] = (byte)(b * 255);
            }
            else { fr[fi] = 191; fg[fi] = 199; fb[fi] = 209; }

            if (dMode == DisplayMode.Wireframe || dMode == DisplayMode.Plot)
                for (int j = 0; j < face.Length; j++) { edges.Add(face[j]); edges.Add(face[(j + 1) % face.Length]); }
        }
        offsets[bfaces.Count] = vi;
        _faceOffsets = offsets; _faceVertices = verts;
        _faceR = fr; _faceG = fg; _faceB = fb;
        _edgePairs = edges.ToArray();
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); _bitmap = null; InvalidateVisual(); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        int w = (int)Bounds.Width, h = (int)Bounds.Height;
        if (w < 2 || h < 2 || _vm == null || _nVerts == 0) return;

        if (_bitmap == null || _bw != w || _bh != h)
        {
            _bw = w; _bh = h;
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);
            _pixels = new uint[w * h];
            _zbuf = new float[w * h];
        }

        // Project vertices (the only per-frame work for camera changes)
        var cam = Camera.Build(_vm.Camera);
        double orthoHH = _vm.Camera.Dist;
        float scale = (float)(h / (2.0 * orthoHH));
        float hw = w / 2f, hh = h / 2f;
        float ex = (float)cam.Eye.X, ey = (float)cam.Eye.Y, ez = (float)cam.Eye.Z;
        float rrx = (float)cam.Right.X, rry = (float)cam.Right.Y, rrz = (float)cam.Right.Z;
        float urx = (float)cam.Up.X, ury = (float)cam.Up.Y, urz = (float)cam.Up.Z;
        float frx = (float)cam.Forward.X, fry = (float)cam.Forward.Y, frz = (float)cam.Forward.Z;

        var sx = new float[_nVerts]; var sy = new float[_nVerts]; var sz = new float[_nVerts];
        for (int i = 0; i < _nVerts; i++)
        {
            float rx = _posX[i] - ex, ry = _posY[i] - ey, rz = _posZ[i] - ez;
            sx[i] = hw + (rx * rrx + ry * rry + rz * rrz) * scale;
            sy[i] = hh - (rx * urx + ry * ury + rz * urz) * scale;
            sz[i] = rx * frx + ry * fry + rz * frz;
        }

        // Rasterize
        BitmapRenderer.RenderFaces(_pixels!, _zbuf!, w, h, sx, sy, sz, _faceOffsets, _faceVertices, _faceR, _faceG, _faceB);
        if (_edgePairs.Length > 0)
            BitmapRenderer.RenderEdges(_pixels!, w, h, sx, sy, sz, _edgePairs, 0xFF333333, _zbuf!, 0.02f);

        using (var fb = _bitmap!.Lock())
            Marshal.Copy((int[])(object)_pixels!, 0, fb.Address, _pixels!.Length);

        context.DrawImage(_bitmap, new Rect(new Point(0, 0), new Size(w, h)));

        // Lightweight overlays
        var bounds = new Rect(0, 0, w, h);
        if (!string.IsNullOrEmpty(_vm.ActiveField) && _cachedMode != DisplayMode.Wireframe)
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
        double bh = Math.Min(220, bounds.Height - 60), bw = 16;
        double by = (bounds.Height - bh) / 2, barX = bounds.Width - bw - 30, labelRight = barX - 6;
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
            var text = new FormattedText(v.ToString("E2"), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, SciBold, 10, lb);
            ctx.DrawText(text, new Point(labelRight - text.Width, by + t * bh - text.Height / 2));
        }
        var ft = new FormattedText(_vm.ActiveField, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, SciBold, 12, lb);
        using (ctx.PushTransform(Matrix.CreateTranslation(barX + bw + 14, by + bh / 2 + ft.Width / 2)))
        using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2)))
            ctx.DrawText(ft, new Point(0, 0));
    }

    private void DrawTriad(DrawingContext ctx, Rect bounds)
    {
        if (_vm == null) return;
        var rot = _vm.Camera.Rot;
        double cx = 60, cy = bounds.Height - 60, al = 50;
        var axes = new[] {
            (rot[0] * al, -rot[3] * al, Color.FromRgb(220, 40, 40), "X\u2081"),
            (rot[1] * al, -rot[4] * al, Color.FromRgb(40, 170, 40), "X\u2082"),
            (rot[2] * al, -rot[5] * al, Color.FromRgb(40, 80, 220), "X\u2083"),
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

    // ─── Arcball + screen-space pan ───
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (_vm == null) return;
        _dragging = true; _panning = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        _lastMouse = e.GetPosition(this); e.Handled = true; Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); if (!_dragging || _vm == null) return;
        var pos = e.GetPosition(this); var cam = _vm.Camera;
        if (_panning)
        {
            double dx = pos.X - _lastMouse.X, dy = pos.Y - _lastMouse.Y;
            double ps = 2.0 * cam.Dist / Math.Max(Bounds.Width, Bounds.Height);
            var r = cam.Rot;
            cam.Tx += (dx * r[0] - dy * r[3]) * ps;
            cam.Ty += (dx * r[1] - dy * r[4]) * ps;
            cam.Tz += (dx * r[2] - dy * r[5]) * ps;
        }
        else
        {
            double w = Bounds.Width, h = Bounds.Height, dim = Math.Min(w, h);
            cam.Rot = CameraParams.Mul(CameraParams.ArcballDelta(
                (2 * _lastMouse.X - w) / dim, -(2 * _lastMouse.Y - h) / dim,
                (2 * pos.X - w) / dim, -(2 * pos.Y - h) / dim), cam.Rot);
        }
        _lastMouse = pos; InvalidateVisual(); e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _dragging = false; e.Handled = true; }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); if (_vm == null) return;
        _vm.Camera.Dist *= e.Delta.Y < 0 ? 1.03 : (1.0 / 1.03);
        _vm.Camera.Dist = Math.Clamp(_vm.Camera.Dist, 0.1, 50);
        InvalidateVisual(); e.Handled = true;
    }
}
