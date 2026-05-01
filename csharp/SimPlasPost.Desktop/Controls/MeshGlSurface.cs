using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Geometry;
using SimPlasPost.Core.Models;
using SimPlasPost.Core.Rendering;
using SimPlasPost.Core.Rendering.Gl;
using SimPlasPost.Desktop.ViewModels;

namespace SimPlasPost.Desktop.Controls;

/// <summary>
/// 3D mesh surface backed by raw OpenGL via Avalonia <see cref="OpenGlControlBase"/>.
/// Every GL call happens on the UI thread inside <see cref="OnOpenGlRender"/>,
/// so there is no thread/context affinity issue (which Veldrid 4.9's OpenGL
/// backend imposes and which Avalonia's GL integration cannot satisfy).
///
/// The control draws the filled mesh, edges, and contour/feature lines into
/// the Avalonia GL framebuffer; it does not render any text overlay (the
/// parent <see cref="MeshViewport"/> draws the color bar, axis triad and
/// info string on top of this surface).
/// </summary>
public class MeshGlSurface : OpenGlControlBase
{
    public MeshGlSurface()
    {
        Diag.Log("MeshGlSurface ctor");
    }

    private MainViewModel? _vm;
    private GlBindings? _gl;
    private GlMeshRenderer? _renderer;

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
        try
        {
            Diag.Log($"OnOpenGlInit start (w={Bounds.Width} h={Bounds.Height})");

            string vendor = "?", renderer = "?", version = "?", shading = "?";
            try
            {
                vendor   = gl.GetString(0x1F00 /* GL_VENDOR */)                   ?? "?";
                renderer = gl.GetString(0x1F01 /* GL_RENDERER */)                 ?? "?";
                version  = gl.GetString(0x1F02 /* GL_VERSION */)                  ?? "?";
                shading  = gl.GetString(0x8B8C /* GL_SHADING_LANGUAGE_VERSION */) ?? "?";
            }
            catch (Exception e) { Diag.Log("gl.GetString threw: " + e.Message); }
            Diag.Log($"GL_VENDOR='{vendor}' GL_RENDERER='{renderer}' GL_VERSION='{version}' GLSL='{shading}'");

            Diag.Log("Loading GL bindings...");
            _gl = GlBindings.Load(name => gl.GetProcAddress(name));
            Diag.Log($"GL bindings loaded — IsGles={_gl.IsGles}");

            Diag.Log("Constructing GlMeshRenderer...");
            _renderer = new GlMeshRenderer(_gl, Diag.Log);
            Diag.Log("OnOpenGlInit done");
        }
        catch (Exception ex)
        {
            Diag.Log("OnOpenGlInit threw: " + ex);
            throw;
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        try { _renderer?.Dispose(); }
        catch (Exception ex) { Diag.Log("OnOpenGlDeinit threw: " + ex); }
        _renderer = null;
        _gl = null;
    }

    private int _frameCount;
    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        try { RenderInner(fb); }
        catch (Exception ex)
        {
            Diag.Log("OnOpenGlRender threw: " + ex);
            throw;
        }
    }

    private void RenderInner(int fb)
    {
        if (_vm == null || _gl == null || _renderer == null) return;
        if (_vm.MeshData == null) return;

        bool trace = _frameCount < 3;
        if (trace) Diag.Log($"RenderInner frame={_frameCount} fb={fb}");

        // Avalonia configures the GL viewport to the physical-pixel size of
        // the framebuffer before calling OnOpenGlRender.  On HiDPI displays
        // that's larger than Bounds.Width/Height (which are in logical
        // pixels) — using Bounds for the projection and viewport produces a
        // correctly-sized image rendered into a small corner of the
        // framebuffer.  Query Avalonia's viewport directly and use those
        // dimensions consistently for projection, glViewport, and the
        // shader's screen-space → NDC mapping.
        var (vx, vy, vw, vh) = _gl.GetViewportRect();
        int w = vw > 0 ? vw : Math.Max(1, (int)Bounds.Width);
        int h = vh > 0 ? vh : Math.Max(1, (int)Bounds.Height);
        if (trace) Diag.Log($"  GL viewport=({vx},{vy},{vw},{vh}) → using {w}x{h}");

        if (_geomDirty || GeometryChanged())
        {
            if (trace) Diag.Log("  RebuildGeometry");
            RebuildGeometry();
            _geomDirty = false;
        }

        if (trace) Diag.Log($"  ProjectAndUpload nVerts={_nVerts}");
        ProjectAndUpload(w, h);

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

        if (trace) Diag.Log($"  RenderFrame fb={fb} zMin={zMin} zMax={zMax} drawFill={_cachedMode != DisplayMode.Wireframe}");
        _renderer.RenderFrame((uint)fb, w, h, zMin, zMax,
            drawFill: _cachedMode != DisplayMode.Wireframe,
            log: trace ? Diag.Log : null);
        if (trace) Diag.Log("  done");
        _frameCount++;
    }

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
