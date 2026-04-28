using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Geometry;
using SimPlasPost.Core.Models;
using SimPlasPost.Core.Rendering;
using SimPlasPost.Core.Rendering.Gpu;
using SimPlasPost.Desktop.ViewModels;
using Veldrid;

namespace SimPlasPost.Desktop.Controls;

/// <summary>
/// 3D mesh surface backed by Veldrid (OpenGL backend) hosted inside an Avalonia
/// <see cref="OpenGlControlBase"/>. This control draws the filled mesh, edges,
/// and contour/feature lines into the Avalonia GL framebuffer; it does not
/// render any text overlay (the parent <see cref="MeshViewport"/> draws the
/// color bar, axis triad and info string on top of this surface).
/// </summary>
public class MeshGlSurface : OpenGlControlBase
{
    private MainViewModel? _vm;
    private VeldridBackend? _backend;
    private VeldridMeshRenderer? _renderer;
    private CommandList? _cl;

    // ─── Cached projected geometry (rebuilt only when state changes) ───
    private MeshData? _cachedMesh;
    private string? _cachedField;
    private bool _cachedShowDef;
    private double _cachedDefScale;
    private DisplayMode _cachedMode;
    private string? _cachedUserMin, _cachedUserMax;
    private int _cachedStep = -1;
    private int _cachedContourN = -1;
    private bool _geomDirty = true;

    private float[] _posX = Array.Empty<float>();
    private float[] _posY = Array.Empty<float>();
    private float[] _posZ = Array.Empty<float>();
    private int _nVerts;
    private int[] _faceOffsets = Array.Empty<int>();
    private int[] _faceVertices = Array.Empty<int>();
    private bool[] _drawEdges = Array.Empty<bool>();
    private byte[] _vertR = Array.Empty<byte>();
    private byte[] _vertG = Array.Empty<byte>();
    private byte[] _vertB = Array.Empty<byte>();
    private float[] _featEdgePos = Array.Empty<float>();
    private float[] _contourPos = Array.Empty<float>();
    private uint[] _contourColors = Array.Empty<uint>();

    private float[] _sx = Array.Empty<float>();
    private float[] _sy = Array.Empty<float>();
    private float[] _sz = Array.Empty<float>();

    public void SetViewModel(MainViewModel vm)
    {
        if (_vm != null) _vm.SceneInvalidated -= OnInvalidated;
        _vm = vm;
        _vm.SceneInvalidated += OnInvalidated;
        _geomDirty = true;
        RequestNextFrameRendering();
    }

    private void OnInvalidated()
    {
        _geomDirty = _geomDirty || GeometryChanged();
        RequestNextFrameRendering();
    }

    private bool GeometryChanged() => _vm != null && (
        _vm.MeshData != _cachedMesh || _vm.ActiveField != _cachedField ||
        _vm.ShowDef != _cachedShowDef || _vm.DefScale != _cachedDefScale ||
        _vm.DisplayMode_ != _cachedMode ||
        _vm.UserMin != _cachedUserMin || _vm.UserMax != _cachedUserMax ||
        _vm.CurrentStep != _cachedStep ||
        _vm.ContourN != _cachedContourN);

    protected override void OnOpenGlInit(GlInterface gl)
    {
        uint w = (uint)Math.Max(1, Bounds.Width);
        uint h = (uint)Math.Max(1, Bounds.Height);

        _backend = VeldridBackend.CreateOpenGL(
            getProcAddress: name => gl.GetProcAddress(name),
            makeCurrent: _ => { },           // Avalonia keeps the context current on this thread
            getCurrentContext: () => IntPtr.Zero,
            clearCurrentContext: () => { },
            deleteContext: _ => { },
            swapBuffers: () => { },          // Avalonia performs the swap
            setSyncToVerticalBlank: _ => { },
            contextHandle: IntPtr.Zero,
            width: w, height: h);

        _renderer = new VeldridMeshRenderer(_backend);
        _cl = _backend.Factory.CreateCommandList();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _cl?.Dispose();
        _renderer?.Dispose();
        _backend?.Dispose();
        _cl = null;
        _renderer = null;
        _backend = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_vm == null || _backend == null || _renderer == null || _cl == null) return;
        if (_vm.MeshData == null) return;

        int w = (int)Math.Max(1, Bounds.Width);
        int h = (int)Math.Max(1, Bounds.Height);

        // Resize the Veldrid swapchain when the control size changes.
        if (_backend.Device.MainSwapchain.Framebuffer.Width != (uint)w ||
            _backend.Device.MainSwapchain.Framebuffer.Height != (uint)h)
        {
            _backend.Device.ResizeMainWindow((uint)w, (uint)h);
            _backend.BuildPipelines(_backend.Device.MainSwapchain.Framebuffer.OutputDescription);
        }

        if (_geomDirty || GeometryChanged())
        {
            RebuildGeometry();
            _geomDirty = false;
        }

        ProjectAndUpload(w, h);

        // Compute eye-Z range for the linear depth mapping in the vertex shader.
        // Find min/max projected z so we use the full depth precision available.
        float zMin = float.MaxValue, zMax = float.MinValue;
        for (int i = 0; i < _nVerts; i++)
        {
            float z = _sz[i];
            if (z < zMin) zMin = z;
            if (z > zMax) zMax = z;
        }
        if (zMin == zMax) { zMin -= 1; zMax += 1; }
        float pad = 0.05f * (zMax - zMin);
        zMin -= pad; zMax += pad;

        _backend.UpdateUniforms(w, h, zMin, zMax);

        _cl.Begin();
        _renderer.RenderFrame(_cl, _backend.Device.MainSwapchain.Framebuffer, RgbaFloat.White,
            drawFill: _cachedMode != DisplayMode.Wireframe);
        _cl.End();
        _backend.Device.SubmitCommands(_cl);
    }

    /// <summary>Rebuild cached projected geometry when mesh/field/mode changes.</summary>
    private void RebuildGeometry()
    {
        if (_vm?.MeshData == null) return;
        var mesh = _vm.MeshData;
        var dMode = _vm.DisplayMode_;
        _cachedMesh = mesh; _cachedField = _vm.ActiveField;
        _cachedShowDef = _vm.ShowDef; _cachedDefScale = _vm.DefScale;
        _cachedMode = dMode; _cachedUserMin = _vm.UserMin; _cachedUserMax = _vm.UserMax;
        _cachedStep = _vm.CurrentStep;
        _cachedContourN = _vm.ContourN;

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

        var vr = new byte[_nVerts]; var vg = new byte[_nVerts]; var vb = new byte[_nVerts];
        bool whiteFaces = dMode == DisplayMode.Wireframe || dMode == DisplayMode.Lines;
        for (int i = 0; i < _nVerts; i++)
        {
            if (whiteFaces) { vr[i] = 255; vg[i] = 255; vb[i] = 255; }
            else if (fv != null && i < fv.Length)
            {
                double t = (fv[i] - efMin) / efSpan;
                var (r, g, b) = TurboColormap.Sample(t);
                vr[i] = (byte)(r * 255); vg[i] = (byte)(g * 255); vb[i] = (byte)(b * 255);
            }
            else { vr[i] = 191; vg[i] = 199; vb[i] = 209; }
        }

        int totalVerts = 0; foreach (var f in bfaces) totalVerts += f.Length;
        var offsets = new int[bfaces.Count + 1];
        var verts = new int[totalVerts];
        var showEdges = new bool[bfaces.Count];
        int vi = 0;
        for (int fi = 0; fi < bfaces.Count; fi++)
        {
            var face = bfaces[fi];
            offsets[fi] = vi;
            for (int j = 0; j < face.Length; j++) verts[vi++] = face[j];
            showEdges[fi] = dMode == DisplayMode.Wireframe || dMode == DisplayMode.Plot;
        }
        offsets[bfaces.Count] = vi;
        _faceOffsets = offsets; _faceVertices = verts;
        _vertR = vr; _vertG = vg; _vertB = vb;
        _drawEdges = showEdges;

        if (dMode == DisplayMode.Lines)
        {
            var dp = new double[_nVerts][];
            for (int i = 0; i < _nVerts; i++)
                dp[i] = new double[] { _posX[i], _posY[i], _posZ[i] };

            var fed = FeatureEdgeDetector.Extract(bfaces, dp);
            _featEdgePos = new float[fed.Length];
            for (int i = 0; i < fed.Length; i++) _featEdgePos[i] = (float)fed[i];

            if (fv != null)
            {
                int contourN = Math.Max(1, _vm.ContourN);
                var raw = ContourGenerator.ComputeSegments(bfaces, dp, fv, efMin, efMax, contourN);
                var smooth = ContourGenerator.Smooth(raw, 2);
                int n = smooth.Count;
                var pos = new float[n * 6];
                var cols = new uint[n];
                for (int i = 0; i < n; i++)
                {
                    var s = smooth[i];
                    int b = i * 6;
                    pos[b] = (float)s.A[0]; pos[b + 1] = (float)s.A[1]; pos[b + 2] = (float)s.A[2];
                    pos[b + 3] = (float)s.B[0]; pos[b + 4] = (float)s.B[1]; pos[b + 5] = (float)s.B[2];
                    double t = (s.Level - efMin) / efSpan;
                    var (r, g, b2) = TurboColormap.Sample(t);
                    cols[i] = 0xFF000000u | ((uint)(r * 255) << 16) | ((uint)(g * 255) << 8) | (uint)(b2 * 255);
                }
                _contourPos = pos;
                _contourColors = cols;
            }
            else
            {
                _contourPos = Array.Empty<float>();
                _contourColors = Array.Empty<uint>();
            }
        }
        else
        {
            _featEdgePos = Array.Empty<float>();
            _contourPos = Array.Empty<float>();
            _contourColors = Array.Empty<uint>();
        }

        if (_sx.Length != _nVerts)
        {
            _sx = new float[_nVerts]; _sy = new float[_nVerts]; _sz = new float[_nVerts];
        }
    }

    /// <summary>
    /// Project world-space vertices to screen pixels using the current camera and
    /// upload the result to the GPU. Edges and segments are projected and uploaded
    /// the same way so all geometry shares the same depth space.
    /// </summary>
    private void ProjectAndUpload(int w, int h)
    {
        if (_vm == null || _renderer == null || _nVerts == 0) return;

        var cam = Camera.Build(_vm.Camera);
        float scale = (float)(h / (2.0 * _vm.Camera.Dist));
        float hw = w / 2f, hh = h / 2f;
        float ex = (float)cam.Eye.X, ey = (float)cam.Eye.Y, ez = (float)cam.Eye.Z;
        float rrx = (float)cam.Right.X, rry = (float)cam.Right.Y, rrz = (float)cam.Right.Z;
        float urx = (float)cam.Up.X, ury = (float)cam.Up.Y, urz = (float)cam.Up.Z;
        float frx = (float)cam.Forward.X, fry = (float)cam.Forward.Y, frz = (float)cam.Forward.Z;

        if (_sx.Length != _nVerts) { _sx = new float[_nVerts]; _sy = new float[_nVerts]; _sz = new float[_nVerts]; }

        for (int i = 0; i < _nVerts; i++)
        {
            float rx = _posX[i] - ex, ry = _posY[i] - ey, rz = _posZ[i] - ez;
            _sx[i] = hw + (rx * rrx + ry * rry + rz * rrz) * scale;
            _sy[i] = hh - (rx * urx + ry * ury + rz * urz) * scale;
            _sz[i] = rx * frx + ry * fry + rz * frz;
        }

        _renderer.UploadMesh(_sx, _sy, _sz, _vertR, _vertG, _vertB, _faceOffsets, _faceVertices);
        _renderer.UploadEdges(_sx, _sy, _sz, _faceOffsets, _faceVertices, _drawEdges, 0x22, 0x22, 0x22);

        var feat = ProjectWorldSegments(_featEdgePos, ex, ey, ez, rrx, rry, rrz, urx, ury, urz, frx, fry, frz, scale, hw, hh);
        var con  = ProjectWorldSegments(_contourPos,  ex, ey, ez, rrx, rry, rrz, urx, ury, urz, frx, fry, frz, scale, hw, hh);

        // Combine both segment sources into a single buffer so the renderer
        // issues one draw per frame.  Feature edges use a fixed dark color; iso
        // contour lines carry their own per-segment Turbo color.
        int nFeat = feat.Length / 6;
        int nCon  = con.Length  / 6;
        if (nFeat + nCon == 0)
        {
            _renderer.UploadSegments(ReadOnlySpan<float>.Empty, null, 0xFF222222);
            return;
        }

        var allSeg = new float[(nFeat + nCon) * 6];
        var allCol = new uint[nFeat + nCon];
        Array.Copy(feat, 0, allSeg, 0, feat.Length);
        Array.Copy(con,  0, allSeg, feat.Length, con.Length);
        for (int i = 0; i < nFeat; i++) allCol[i] = 0xFF222222u;
        for (int i = 0; i < nCon;  i++) allCol[nFeat + i] = _contourColors.Length > i ? _contourColors[i] : 0xFF222222u;
        _renderer.UploadSegments(allSeg, allCol, 0xFF222222);
    }

    private static float[] ProjectWorldSegments(
        float[] worldPos,
        float ex, float ey, float ez,
        float rrx, float rry, float rrz,
        float urx, float ury, float urz,
        float frx, float fry, float frz,
        float scale, float hw, float hh)
    {
        int n = worldPos.Length / 3;
        var o = new float[worldPos.Length];
        for (int i = 0; i < n; i++)
        {
            int b = i * 3;
            float rx = worldPos[b] - ex, ry = worldPos[b + 1] - ey, rz = worldPos[b + 2] - ez;
            o[b]     = hw + (rx * rrx + ry * rry + rz * rrz) * scale;
            o[b + 1] = hh - (rx * urx + ry * ury + rz * urz) * scale;
            o[b + 2] = rx * frx + ry * fry + rz * frz;
        }
        return o;
    }
}
