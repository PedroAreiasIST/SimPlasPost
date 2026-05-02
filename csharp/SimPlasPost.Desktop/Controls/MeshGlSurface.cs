using Avalonia;
using Avalonia.Controls;
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
    private bool _cachedShowLabels;
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
    // Connectivity for stand-alone Bar2 / Point1 elements (kept as flat
    // node-index arrays so the projection step can map them through _sx/_sy/_sz
    // straight into the GPU buffers without re-extraction every frame).
    private int[] _barNodes = Array.Empty<int>();
    private int[] _pointNodes = Array.Empty<int>();
    // Pre-triangulated face mesh as a flat soup (3 vertex slots per
    // triangle).  Each slot stores (a) the source node index, used at
    // projection time to look up the on-screen position from _sx/_sy/_sz,
    // and (b) an RGB colour byte triple, baked at RebuildGeometry time.
    // For per-NODE fields the colour comes from the node's field value
    // (so adjacent face copies of a shared vertex agree → smooth shading);
    // for per-ELEMENT fields it comes from the owning element's value
    // (so adjacent faces from different elements have different colours
    // → sharp element-boundary steps, matching FE-viewer convention).
    private int[] _triNode = Array.Empty<int>();
    private byte[] _triR = Array.Empty<byte>();
    private byte[] _triG = Array.Empty<byte>();
    private byte[] _triB = Array.Empty<byte>();

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
        _vm.ContourN != _cachedContourN ||
        _vm.ShowContourLabels != _cachedShowLabels);

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
        // Avalonia invokes this from a native callback.  An exception that
        // unwinds out of here can land the runtime in FailFast (exit 134
        // on Linux), so we log and swallow instead of rethrowing — losing
        // a frame is better than killing the app.
        try { RenderInner(fb); }
        catch (Exception ex)
        {
            Diag.Log("OnOpenGlRender threw: " + ex);
        }
    }

    private void RenderInner(int fb)
    {
        if (_vm == null || _gl == null || _renderer == null) return;
        if (_vm.MeshData == null) return;

        bool trace = _frameCount < 3;
        if (trace) Diag.Log($"RenderInner frame={_frameCount} fb={fb}");

        // The Avalonia framebuffer size is Bounds × RenderScaling — Bounds
        // are in logical pixels, the framebuffer is in physical pixels.  We
        // can't trust glGetIntegerv(GL_VIEWPORT) because Avalonia doesn't
        // actually pre-configure the viewport; it leaves it at the GL default
        // (1×1 since context creation), so reading it gives us nothing useful.
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)Math.Round(Bounds.Width  * scaling));
        int h = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));
        if (trace) Diag.Log($"  Bounds={Bounds.Width:F0}x{Bounds.Height:F0} scaling={scaling} → fb {w}x{h}");

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
        _cachedShowLabels = _vm.ShowContourLabels;

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
        var bfacesSrc = BoundaryExtractor.ExtractWithSource(mesh.Elements, is3D);
        var bfaces = bfacesSrc.Select(t => t.Face).ToList();

        // Resolve the active scalar field, distinguishing per-node vs
        // per-element addressing.  fv stays as a flat double[] in either
        // case (per-node => indexed by node, per-element => indexed by
        // element); the consumer checks isPerElement to know how to
        // look up a value for a given vertex.
        double[]? fv = null;
        bool isPerElement = false;
        double fmin = 0, fmax = 1;
        if (dMode != DisplayMode.Wireframe && !string.IsNullOrEmpty(_vm.ActiveField) &&
            mesh.Fields.TryGetValue(_vm.ActiveField, out var field) && !field.IsVector)
        {
            fv = field.ScalarValues;
            isPerElement = field.IsPerElement;
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

        // For per-element fields, contour iso-lines collapse (the field is
        // constant inside each element), so the user's Lines mode degrades
        // to Plot semantics for face-coloring and edge-drawing.  Wireframe
        // is unchanged.
        DisplayMode effectiveMode = (isPerElement && dMode == DisplayMode.Lines)
            ? DisplayMode.Plot
            : dMode;

        // Per-NODE colour table: used by Bar2/Point1 elements (which always
        // colour by node) and by the smooth-shading path for per-node fields.
        var vr = new byte[_nVerts]; var vg = new byte[_nVerts]; var vb = new byte[_nVerts];
        bool whiteFaces = effectiveMode == DisplayMode.Wireframe || effectiveMode == DisplayMode.Lines;
        for (int i = 0; i < _nVerts; i++)
        {
            if (whiteFaces) { vr[i] = 255; vg[i] = 255; vb[i] = 255; }
            else if (fv != null && !isPerElement && i < fv.Length)
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
            showEdges[fi] = effectiveMode == DisplayMode.Wireframe || effectiveMode == DisplayMode.Plot;
        }
        offsets[bfaces.Count] = vi;
        _faceOffsets = offsets; _faceVertices = verts;
        _vertR = vr; _vertG = vg; _vertB = vb;
        _drawEdges = showEdges;

        // Build the per-tri-vertex triangle soup used for filled face
        // rendering.  Each face is fan-triangulated; for per-NODE colouring
        // each vertex slot gets the colour of its node, for per-ELEMENT
        // colouring all three slots of every triangle in this face get the
        // colour of the owning element (so adjacent elements with different
        // values produce a sharp colour step at their shared edge).
        //
        // For per-NODE fields we additionally barycentrically subdivide
        // faces whose vertex t-range straddles multiple Turbo LUT bins.
        // The reason: GL interpolates RGB linearly between vertex slots,
        // and linear RGB interpolation does NOT track the Turbo curve —
        // a face going from blue (t≈0.1) to yellow (t≈0.6) gets a muddy
        // gray midpoint instead of the Turbo green at t=0.35.  Subdividing
        // keeps each sub-edge inside ~one LUT bin where linear RGB ≈ LUT
        // lookup.  Sub-vertex positions are interpolated in normalized
        // mesh space and appended to _posX/_posY/_posZ so the per-frame
        // projection picks them up automatically.
        var triNodes = new List<int>();
        var triR = new List<byte>();
        var triG = new List<byte>();
        var triB = new List<byte>();
        var extraX = new List<float>();
        var extraY = new List<float>();
        var extraZ = new List<float>();
        int origVerts = _nVerts;

        for (int fi = 0; fi < bfaces.Count; fi++)
        {
            var face = bfaces[fi];
            int n = face.Length;
            if (n < 3) continue;
            int elemIdx = bfacesSrc[fi].ElementIndex;

            byte er = 0, eg = 0, eb = 0;
            bool useElemColor = isPerElement && fv != null && elemIdx >= 0 && elemIdx < fv.Length;
            if (useElemColor)
            {
                double t = (fv![elemIdx] - efMin) / efSpan;
                var (r, g, b) = TurboColormap.Sample(t);
                er = (byte)(r * 255); eg = (byte)(g * 255); eb = (byte)(b * 255);
            }

            int subdivN = 1;
            double[]? tArr = null;
            if (!useElemColor && !whiteFaces && fv != null && !isPerElement)
            {
                tArr = new double[n];
                double tMin = double.MaxValue, tMax = double.MinValue;
                for (int j = 0; j < n; j++)
                {
                    tArr[j] = (fv[face[j]] - efMin) / efSpan;
                    if (tArr[j] < tMin) tMin = tArr[j];
                    if (tArr[j] > tMax) tMax = tArr[j];
                }
                // Turbo LUT has ~31 bins (Δt ≈ 1/30); pick N so each sub-edge
                // spans at most one bin, capped to keep triangle counts bounded.
                subdivN = (int)Math.Clamp(Math.Ceiling((tMax - tMin) * 30.0), 1, 8);
            }

            for (int k = 1; k < n - 1; k++)
            {
                int n0 = face[0], n1 = face[k], n2 = face[k + 1];
                if (subdivN <= 1)
                {
                    AppendSlot(triNodes, triR, triG, triB, n0, useElemColor, er, eg, eb, vr, vg, vb);
                    AppendSlot(triNodes, triR, triG, triB, n1, useElemColor, er, eg, eb, vr, vg, vb);
                    AppendSlot(triNodes, triR, triG, triB, n2, useElemColor, er, eg, eb, vr, vg, vb);
                }
                else
                {
                    AppendRefinedTriangle(
                        n0, n1, n2,
                        tArr![0], tArr[k], tArr[k + 1],
                        subdivN, _posX, _posY, _posZ, origVerts,
                        extraX, extraY, extraZ,
                        triNodes, triR, triG, triB);
                }
            }
        }

        _triNode = triNodes.ToArray();
        _triR = triR.ToArray();
        _triG = triG.ToArray();
        _triB = triB.ToArray();

        if (extraX.Count > 0)
        {
            int newCount = origVerts + extraX.Count;
            var nx = new float[newCount];
            var ny = new float[newCount];
            var nz = new float[newCount];
            Array.Copy(_posX, nx, origVerts);
            Array.Copy(_posY, ny, origVerts);
            Array.Copy(_posZ, nz, origVerts);
            for (int i = 0; i < extraX.Count; i++)
            {
                nx[origVerts + i] = extraX[i];
                ny[origVerts + i] = extraY[i];
                nz[origVerts + i] = extraZ[i];
            }
            _posX = nx; _posY = ny; _posZ = nz;
            _nVerts = newCount;
        }

        if (effectiveMode == DisplayMode.Lines)
        {
            var dp = new double[_nVerts][];
            for (int i = 0; i < _nVerts; i++)
                dp[i] = new double[] { _posX[i], _posY[i], _posZ[i] };

            var fed = FeatureEdgeDetector.Extract(bfaces, dp);
            _featEdgePos = new float[fed.Length];
            for (int i = 0; i < fed.Length; i++) _featEdgePos[i] = (float)fed[i];

            // Default: clear any prior label state.  We re-populate it below
            // when both a field is active AND the user requested labels.
            _vm.ContourLabelsWorld.Clear();

            if (fv != null)
            {
                int contourN = Math.Max(1, _vm.ContourN);
                var raw = ContourGenerator.ComputeSegments(bfaces, dp, fv, efMin, efMax, contourN);
                var polylines = ContourGenerator.SmoothPolylines(raw, 2);

                // Convert polylines back to GL line-list segments (the on-screen
                // pipeline draws them as GL_LINES) and remember per-polyline
                // arc length so the label placer below can prioritise long
                // iso-lines.
                var segPos = new List<float>();
                var segCols = new List<uint>();
                foreach (var pl in polylines)
                {
                    double t = (pl.Level - efMin) / efSpan;
                    var (r, g, b2) = TurboColormap.Sample(t);
                    uint col = 0xFF000000u | ((uint)(r * 255) << 16) | ((uint)(g * 255) << 8) | (uint)(b2 * 255);
                    var pts = pl.Points;
                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        var a = pts[i]; var bb = pts[i + 1];
                        segPos.Add((float)a[0]); segPos.Add((float)a[1]); segPos.Add((float)a[2]);
                        segPos.Add((float)bb[0]); segPos.Add((float)bb[1]); segPos.Add((float)bb[2]);
                        segCols.Add(col);
                    }
                }
                _contourPos = segPos.ToArray();
                _contourColors = segCols.ToArray();

                // Label candidates: one per polyline whose world-space arc
                // length is large enough that a label fits comfortably.  The
                // overlay culls overlaps later (greedy by Length descending).
                if (_vm.ShowContourLabels)
                {
                    foreach (var pl in polylines)
                    {
                        if (pl.Points.Count < 2) continue;
                        // World arc length and midpoint by arc length.
                        double total = 0;
                        for (int i = 1; i < pl.Points.Count; i++)
                            total += Dist(pl.Points[i - 1], pl.Points[i]);
                        // Skip very short pieces — short iso-loops just clutter.
                        if (total < 0.06) continue;

                        double half = total * 0.5, acc = 0;
                        int hit = 1;
                        for (int i = 1; i < pl.Points.Count; i++)
                        {
                            double L = Dist(pl.Points[i - 1], pl.Points[i]);
                            if (acc + L >= half) { hit = i; break; }
                            acc += L;
                        }
                        var pa = pl.Points[hit - 1];
                        var pb = pl.Points[hit];
                        double tt = Math.Max(0, Math.Min(1, (half - acc) / Math.Max(1e-12, Dist(pa, pb))));
                        var mid = new[]
                        {
                            pa[0] + tt * (pb[0] - pa[0]),
                            pa[1] + tt * (pb[1] - pa[1]),
                            pa[2] + tt * (pb[2] - pa[2]),
                        };

                        // Tangent: chord across a few neighbouring points so
                        // the orientation is robust against local Chaikin
                        // wiggles.
                        int span = Math.Min(3, Math.Min(hit, pl.Points.Count - hit));
                        var lo = pl.Points[Math.Max(0, hit - span)];
                        var hi = pl.Points[Math.Min(pl.Points.Count - 1, hit - 1 + span)];
                        var dir = new[] { hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2] };
                        double dl = Math.Sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
                        if (dl < 1e-12) dl = 1;
                        dir[0] /= dl; dir[1] /= dl; dir[2] /= dl;

                        _vm.ContourLabelsWorld.Add(new ContourLabelWorld
                        {
                            Pos = mid,
                            TangentDir = dir,
                            Text = pl.Level.ToString("G3", System.Globalization.CultureInfo.InvariantCulture),
                            Length = total,
                        });
                    }
                }
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
            _vm.ContourLabelsWorld.Clear();
        }

        if (_sx.Length != _nVerts)
        {
            _sx = new float[_nVerts]; _sy = new float[_nVerts]; _sz = new float[_nVerts];
        }

        // Stand-alone bar / point elements: harvest their connectivity once
        // here so per-frame uploading is just a node-index lookup against
        // _sx/_sy/_sz.
        var bars = BoundaryExtractor.ExtractByDim(mesh.Elements, 1);
        _barNodes = new int[bars.Count * 2];
        for (int i = 0; i < bars.Count; i++)
        {
            _barNodes[i * 2 + 0] = bars[i][0];
            _barNodes[i * 2 + 1] = bars[i][1];
        }
        var points = BoundaryExtractor.ExtractByDim(mesh.Elements, 0);
        _pointNodes = new int[points.Count];
        for (int i = 0; i < points.Count; i++) _pointNodes[i] = points[i][0];
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

        // Build the per-frame triangle soup: each triangle's 3 vertex slots
        // come from the pre-baked _triNode + _triR/G/B arrays, looking up
        // current screen positions from the per-node projection results.
        var triVerts = new GlVertex[_triNode.Length];
        for (int i = 0; i < _triNode.Length; i++)
        {
            int n = _triNode[i];
            triVerts[i] = new GlVertex(_sx[n], _sy[n], _sz[n], _triR[i], _triG[i], _triB[i]);
        }
        _renderer.UploadMesh(triVerts);
        _renderer.UploadEdges(_sx, _sy, _sz, _faceOffsets, _faceVertices, _drawEdges, 0x22, 0x22, 0x22);

        var feat = ProjectWorldSegments(_featEdgePos, ex, ey, ez, rrx, rry, rrz, urx, ury, urz, frx, fry, frz, scale, hw, hh);
        var con  = ProjectWorldSegments(_contourPos,  ex, ey, ez, rrx, rry, rrz, urx, ury, urz, frx, fry, frz, scale, hw, hh);

        int nFeat = feat.Length / 6;
        int nCon  = con.Length  / 6;
        if (nFeat + nCon == 0)
        {
            _renderer.UploadSegments(ReadOnlySpan<float>.Empty, null, 0xFF222222);
        }
        else
        {
            var allSeg = new float[(nFeat + nCon) * 6];
            var allCol = new uint[nFeat + nCon];
            Array.Copy(feat, 0, allSeg, 0, feat.Length);
            Array.Copy(con,  0, allSeg, feat.Length, con.Length);
            for (int i = 0; i < nFeat; i++) allCol[i] = 0xFF222222u;
            for (int i = 0; i < nCon;  i++) allCol[nFeat + i] = _contourColors.Length > i ? _contourColors[i] : 0xFF222222u;
            _renderer.UploadSegments(allSeg, allCol, 0xFF222222);
        }

        // Stand-alone Bar2 / Point1 elements share the per-vertex color
        // arrays with the rest of the mesh so they pick up the active field
        // shading; positions come from the projected _sx/_sy/_sz too.
        _renderer.UploadBars(_sx, _sy, _sz, _vertR, _vertG, _vertB, _barNodes);
        _renderer.UploadPoints(_sx, _sy, _sz, _vertR, _vertG, _vertB, _pointNodes);
    }

    /// <summary>
    /// Append one triangle-soup slot: stores the source node index (used at
    /// projection time) plus an RGB triple — either the owning element's
    /// colour (when <paramref name="useElem"/> is true, for per-element
    /// fields) or the node's colour from the per-node colour table.
    /// </summary>
    private static void AppendSlot(
        List<int> triNodes, List<byte> triR, List<byte> triG, List<byte> triB,
        int node, bool useElem,
        byte er, byte eg, byte eb,
        byte[] vr, byte[] vg, byte[] vb)
    {
        triNodes.Add(node);
        if (useElem) { triR.Add(er); triG.Add(eg); triB.Add(eb); }
        else         { triR.Add(vr[node]); triG.Add(vg[node]); triB.Add(vb[node]); }
    }

    /// <summary>
    /// Subdivide a triangle (n0, n1, n2) with per-vertex colormap parameters
    /// (t0, t1, t2) into N×N barycentric sub-triangles.  The three corner
    /// vertices keep their original mesh-node indices; interior and edge
    /// sub-vertices get fresh indices appended into the extra-position lists,
    /// and the per-frame projection picks them up via the extended
    /// <see cref="_posX"/>/<see cref="_posY"/>/<see cref="_posZ"/> arrays.
    /// Each sub-vertex's colour is sampled from the Turbo LUT at the
    /// barycentrically-interpolated t value, so linear RGB interpolation
    /// across each sub-edge stays close to the actual LUT curve.
    /// </summary>
    private static void AppendRefinedTriangle(
        int n0, int n1, int n2,
        double t0, double t1, double t2,
        int N,
        float[] posX, float[] posY, float[] posZ,
        int origCount,
        List<float> extraX, List<float> extraY, List<float> extraZ,
        List<int> triNodes, List<byte> triR, List<byte> triG, List<byte> triB)
    {
        int rows = N + 1;
        int total = rows * rows;
        var idxAt = new int[total];
        var rAt = new byte[total];
        var gAt = new byte[total];
        var bAt = new byte[total];

        int Idx(int i, int j) => i * rows + j;

        for (int i = 0; i <= N; i++)
        {
            for (int j = 0; j <= N - i; j++)
            {
                int s = Idx(i, j);
                int nodeIdx;
                if (i == 0 && j == 0) nodeIdx = n0;
                else if (i == N && j == 0) nodeIdx = n1;
                else if (i == 0 && j == N) nodeIdx = n2;
                else
                {
                    double a = 1.0 - (i + j) / (double)N;
                    double b = i / (double)N;
                    double c = j / (double)N;
                    float px = (float)(a * posX[n0] + b * posX[n1] + c * posX[n2]);
                    float py = (float)(a * posY[n0] + b * posY[n1] + c * posY[n2]);
                    float pz = (float)(a * posZ[n0] + b * posZ[n1] + c * posZ[n2]);
                    nodeIdx = origCount + extraX.Count;
                    extraX.Add(px); extraY.Add(py); extraZ.Add(pz);
                }
                idxAt[s] = nodeIdx;

                double aa = 1.0 - (i + j) / (double)N;
                double bb = i / (double)N;
                double cc = j / (double)N;
                double tv = aa * t0 + bb * t1 + cc * t2;
                var (cr, cg, cbl) = TurboColormap.Sample(tv);
                rAt[s] = (byte)(cr * 255);
                gAt[s] = (byte)(cg * 255);
                bAt[s] = (byte)(cbl * 255);
            }
        }

        void EmitTri(int sa, int sb, int sc)
        {
            triNodes.Add(idxAt[sa]); triR.Add(rAt[sa]); triG.Add(gAt[sa]); triB.Add(bAt[sa]);
            triNodes.Add(idxAt[sb]); triR.Add(rAt[sb]); triG.Add(gAt[sb]); triB.Add(bAt[sb]);
            triNodes.Add(idxAt[sc]); triR.Add(rAt[sc]); triG.Add(gAt[sc]); triB.Add(bAt[sc]);
        }

        for (int i = 0; i < N; i++)
        {
            for (int j = 0; j < N - i; j++)
            {
                EmitTri(Idx(i, j), Idx(i + 1, j), Idx(i, j + 1));
                if (i + j < N - 1)
                    EmitTri(Idx(i + 1, j), Idx(i + 1, j + 1), Idx(i, j + 1));
            }
        }
    }

    private static double Dist(double[] a, double[] b)
    {
        double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
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
