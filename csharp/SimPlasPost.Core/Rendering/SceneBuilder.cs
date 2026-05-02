using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Geometry;
using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Rendering;

/// <summary>
/// Builds a projected 2D scene from 3D mesh data, suitable for both
/// on-screen rendering (painter's algorithm) and vector export.
/// </summary>
public static class SceneBuilder
{
    public static ExportScene? Build(
        MeshData meshData, string? activeField, bool showDef, double defScale,
        CameraParams camParams, int w, int h, DisplayMode dMode, int contourN,
        double? eMinOverride, double? eMaxOverride)
    {
        if (meshData == null) return null;

        var ns = meshData.Nodes;

        // Compute bounding box and center
        double mnX = double.MaxValue, mnY = double.MaxValue, mnZ = double.MaxValue;
        double mxX = double.MinValue, mxY = double.MinValue, mxZ = double.MinValue;
        foreach (var n in ns)
        {
            mnX = Math.Min(mnX, n[0]); mxX = Math.Max(mxX, n[0]);
            mnY = Math.Min(mnY, n[1]); mxY = Math.Max(mxY, n[1]);
            mnZ = Math.Min(mnZ, n[2]); mxZ = Math.Max(mxZ, n[2]);
        }

        double cenX = (mnX + mxX) / 2, cenY = (mnY + mxY) / 2, cenZ = (mnZ + mxZ) / 2;
        double span = Math.Max(Math.Max(mxX - mnX, mxY - mnY), Math.Max(mxZ - mnZ, 1e-12));
        double sc = 2.0 / span;

        // Determine if mesh is 3D
        bool is3D = meshData.Dim == 3 || meshData.Elements.Any(e =>
            FaceTable.Faces.TryGetValue(e.Type, out var ft) && ft.Dim == 3);

        // Compute displaced positions
        var dispField = meshData.GetDisplacementField();
        var dp = new double[ns.Length][];
        for (int i = 0; i < ns.Length; i++)
        {
            var n = ns[i];
            double dx = 0, dy = 0, dz = 0;
            if (showDef && dispField is { IsVector: true, VectorValues: not null } && i < dispField.VectorValues.Length)
            {
                var d = dispField.VectorValues[i];
                dx = d[0] * defScale; dy = d[1] * defScale; dz = d[2] * defScale;
            }
            dp[i] = new[]
            {
                (n[0] + dx - cenX) * sc,
                (n[1] + dy - cenY) * sc,
                (n[2] + dz - cenZ) * sc,
            };
        }

        // Field values and range.  Both per-node and per-element scalar
        // fields share the ScalarValues storage; the consumer checks the
        // IsPerElement flag to know whether to look the value up by face
        // node index or by source element index.
        double[]? fv = null;
        bool isPerElement = false;
        double fmin = 0, fmax = 1;
        if (!string.IsNullOrEmpty(activeField) && meshData.Fields.TryGetValue(activeField, out var field) && !field.IsVector)
        {
            fv = field.ScalarValues;
            isPerElement = field.IsPerElement;
            if (fv != null && fv.Length > 0)
            {
                fmin = double.MaxValue; fmax = double.MinValue;
                foreach (double v in fv) { fmin = Math.Min(fmin, v); fmax = Math.Max(fmax, v); }
                if (Math.Abs(fmax - fmin) < 1e-15) fmax = fmin + 1;
            }
        }

        double efMin = eMinOverride ?? fmin;
        double efMax = eMaxOverride ?? fmax;
        double efSpan = Math.Abs(efMax - efMin) < 1e-15 ? 1 : efMax - efMin;

        // Lines mode collapses to Plot for per-element fields (iso-lines
        // can't usefully sample a piecewise-constant-per-element field).
        DisplayMode effectiveMode = (isPerElement && dMode == DisplayMode.Lines)
            ? DisplayMode.Plot
            : dMode;

        var bfacesSrc = BoundaryExtractor.ExtractWithSource(meshData.Elements, is3D);
        var bfaces = bfacesSrc.Select(t => t.Face).ToList();
        var cam = Camera.Build(camParams);
        double orthoHH = camParams.Dist;

        var exportFaces = new List<ProjectedFace>();
        var wireEdges3D = new List<(double[] a, double[] b)>();

        for (int fi = 0; fi < bfaces.Count; fi++)
        {
            var face = bfaces[fi];
            int elemIdx = bfacesSrc[fi].ElementIndex;
            var pts = new double[face.Length][];
            for (int i = 0; i < face.Length; i++)
                pts[i] = Camera.Project(dp[face[i]], cam, orthoHH, w, h);

            var screenPts = pts.Select(p => new[] { p[0], p[1] }).ToArray();
            var pts3D = pts.Select(p => new[] { p[0], p[1], p[2] }).ToArray();
            double avgZ = pts.Average(p => p[2]);

            // Per-vertex colours that drive the PDF Type 4 Gouraud shader.
            // For per-element fields every vertex of a face gets the same
            // colour (the owning element's), so the shading degenerates to
            // a flat fill bounded by element edges — sharp seams between
            // adjacent elements with different values.
            //
            // For per-node fields, the GL/PDF rasterizers interpolate RGB
            // linearly between vertices.  Linear RGB interpolation does NOT
            // trace the Turbo LUT curve: a face spanning blue (t≈0.1) to
            // yellow (t≈0.6) gets a muddy gray midpoint instead of the
            // Turbo green at t=0.35.  We fix this by barycentrically
            // subdividing the face so each sub-edge stays inside roughly
            // one LUT bin, where linear RGB ≈ LUT lookup.
            var rArr = new double[face.Length];
            var gArr = new double[face.Length];
            var bArr = new double[face.Length];
            double[]? tArr = null;
            int subdivN = 1;
            if (effectiveMode == DisplayMode.Wireframe)
            {
                for (int j = 0; j < face.Length; j++) { rArr[j] = 1; gArr[j] = 1; bArr[j] = 1; }
            }
            else if (fv != null && isPerElement && elemIdx >= 0 && elemIdx < fv.Length)
            {
                double t = (fv[elemIdx] - efMin) / efSpan;
                var (cr, cg, cb) = TurboColormap.Sample(t);
                for (int j = 0; j < face.Length; j++) { rArr[j] = cr; gArr[j] = cg; bArr[j] = cb; }
            }
            else if (fv != null && !isPerElement)
            {
                tArr = new double[face.Length];
                double tMin = double.MaxValue, tMax = double.MinValue;
                for (int j = 0; j < face.Length; j++)
                {
                    tArr[j] = (fv[face[j]] - efMin) / efSpan;
                    if (tArr[j] < tMin) tMin = tArr[j];
                    if (tArr[j] > tMax) tMax = tArr[j];
                    var (cr, cg, cb) = TurboColormap.Sample(tArr[j]);
                    rArr[j] = cr; gArr[j] = cg; bArr[j] = cb;
                }
                // Turbo LUT has ~31 bins (Δt ≈ 1/30).  Pick N so each
                // sub-edge spans at most one bin; cap at 8 to keep the
                // triangle count bounded on faces that straddle the full
                // range.
                subdivN = (int)Math.Clamp(Math.Ceiling((tMax - tMin) * 30.0), 1, 8);
            }
            else
            {
                for (int j = 0; j < face.Length; j++) { rArr[j] = 0.75; gArr[j] = 0.78; bArr[j] = 0.82; }
            }

            if (subdivN > 1 && tArr != null)
            {
                // Fan-triangulate and emit refined sub-triangles.  The
                // outer wireframe edges below still come from the original
                // polygon outline, so the user sees no extra interior lines.
                for (int k = 1; k < face.Length - 1; k++)
                {
                    EmitRefinedTriangle(
                        pts[0], pts[k], pts[k + 1],
                        tArr[0], tArr[k], tArr[k + 1],
                        subdivN, avgZ, exportFaces);
                }
            }
            else
            {
                exportFaces.Add(new ProjectedFace
                {
                    ScreenPts = screenPts, Pts3D = pts3D,
                    R = rArr, G = gArr, B = bArr, Depth = avgZ,
                });
            }

            if (effectiveMode == DisplayMode.Wireframe || effectiveMode == DisplayMode.Plot)
            {
                for (int j = 0; j < pts.Length; j++)
                    wireEdges3D.Add((pts[j], pts[(j + 1) % pts.Length]));
            }
        }

        // Painter's algorithm: sort back-to-front
        exportFaces.Sort((a, b) => b.Depth.CompareTo(a.Depth));

        var zbuf = ZBufferRenderer.Build(exportFaces, w, h);
        var visibleEdges = new List<ProjectedEdge>();

        // Wireframe/plot edges
        if (effectiveMode == DisplayMode.Wireframe || effectiveMode == DisplayMode.Plot)
        {
            foreach (var (a, b) in wireEdges3D)
            {
                if (ZBufferRenderer.IsSegmentVisible(a, b, zbuf, w, h))
                    visibleEdges.Add(new ProjectedEdge { P1 = new[] { a[0], a[1] }, P2 = new[] { b[0], b[1] } });
            }
        }

        // Feature edges for contour-lines mode
        if (effectiveMode == DisplayMode.Lines)
        {
            var featPos = FeatureEdgeDetector.Extract(bfaces, dp);
            for (int k = 0; k < featPos.Length; k += 6)
            {
                var a3 = new[] { featPos[k], featPos[k + 1], featPos[k + 2] };
                var b3 = new[] { featPos[k + 3], featPos[k + 4], featPos[k + 5] };
                var pa = Camera.Project(a3, cam, orthoHH, w, h);
                var pb = Camera.Project(b3, cam, orthoHH, w, h);
                if (ZBufferRenderer.IsSegmentVisible(pa, pb, zbuf, w, h))
                    visibleEdges.Add(new ProjectedEdge { P1 = new[] { pa[0], pa[1] }, P2 = new[] { pb[0], pb[1] } });
            }
        }

        // Contour iso-lines
        var contours = new List<ProjectedContour>();
        if (effectiveMode == DisplayMode.Lines && fv != null && !isPerElement)
        {
            var rawSegs = ContourGenerator.ComputeSegments(bfaces, dp, fv, efMin, efMax, contourN);
            var smoothed = ContourGenerator.Smooth(rawSegs, 2);
            foreach (var seg in smoothed)
            {
                var pa = Camera.Project(seg.A, cam, orthoHH, w, h);
                var pb = Camera.Project(seg.B, cam, orthoHH, w, h);
                if (ZBufferRenderer.IsSegmentVisible(pa, pb, zbuf, w, h))
                {
                    double t = (seg.Level - efMin) / efSpan;
                    var (cr, cg, cb) = TurboColormap.Sample(t);
                    contours.Add(new ProjectedContour
                    {
                        P1 = new[] { pa[0], pa[1] }, P2 = new[] { pb[0], pb[1] },
                        R = cr, G = cg, B = cb,
                    });
                }
            }
        }

        // Lines mode: faces should be white so the iso-lines and silhouette
        // edges carry all the colour information.  Reset every vertex slot.
        if (effectiveMode == DisplayMode.Lines)
        {
            foreach (var f in exportFaces)
            {
                for (int j = 0; j < f.R.Length; j++) { f.R[j] = 1; f.G[j] = 1; f.B[j] = 1; }
            }
        }

        int nEdges = bfaces.Sum(f => f.Length);
        var lp = LinePreset.Auto(nEdges);

        // Wireframe mode: no color bar, no field coloring
        string? displayFieldName = dMode == DisplayMode.Wireframe ? null : activeField;

        // Stand-alone Bar2 / Point1 elements: always exported, regardless
        // of display mode.  Color comes from the active scalar field at the
        // element's nodes (averaged for bars, sampled for points); when no
        // field is active, fall back to a neutral charcoal so they remain
        // legible against the white page.
        var bars = new List<ProjectedBar>();
        var points = new List<ProjectedPoint>();
        foreach (var conn in BoundaryExtractor.ExtractByDim(meshData.Elements, 1))
        {
            var pa = Camera.Project(dp[conn[0]], cam, orthoHH, w, h);
            var pb = Camera.Project(dp[conn[1]], cam, orthoHH, w, h);
            (double r, double g, double b) col = (0.13, 0.13, 0.13);
            if (fv != null)
            {
                double t = ((fv[conn[0]] + fv[conn[1]]) * 0.5 - efMin) / efSpan;
                col = TurboColormap.Sample(t);
            }
            bars.Add(new ProjectedBar
            {
                P1 = new[] { pa[0], pa[1] }, P2 = new[] { pb[0], pb[1] },
                R = col.r, G = col.g, B = col.b,
            });
        }
        foreach (var conn in BoundaryExtractor.ExtractByDim(meshData.Elements, 0))
        {
            var p = Camera.Project(dp[conn[0]], cam, orthoHH, w, h);
            (double r, double g, double b) col = (0.13, 0.13, 0.13);
            if (fv != null)
            {
                double t = (fv[conn[0]] - efMin) / efSpan;
                col = TurboColormap.Sample(t);
            }
            points.Add(new ProjectedPoint
            {
                P = new[] { p[0], p[1] },
                R = col.r, G = col.g, B = col.b,
            });
        }

        return new ExportScene
        {
            Faces = exportFaces, VisibleEdges = visibleEdges, Contours = contours,
            Bars = bars, Points = points,
            Lp = lp, FieldName = displayFieldName, FMin = efMin, FMax = efMax,
            W = w, H = h, Mode = dMode, Rotation = camParams.Rot,
        };
    }

    /// <summary>
    /// Subdivide a triangle (p0, p1, p2) with per-vertex colormap parameters
    /// (t0, t1, t2) into N×N barycentric sub-triangles, sampling the Turbo
    /// LUT at every sub-vertex.  Each sub-triangle is emitted as its own
    /// <see cref="ProjectedFace"/>; depth is inherited from the parent so
    /// painter's-algorithm sorting remains stable across the cluster.
    /// </summary>
    private static void EmitRefinedTriangle(
        double[] p0, double[] p1, double[] p2,
        double t0, double t1, double t2,
        int N, double depth, List<ProjectedFace> output)
    {
        int rows = N + 1;
        var pos = new double[rows * rows][];
        var rgb = new (double R, double G, double B)[rows * rows];
        int Idx(int i, int j) => i * rows + j;

        for (int i = 0; i <= N; i++)
        {
            for (int j = 0; j <= N - i; j++)
            {
                double a = 1.0 - (i + j) / (double)N;
                double b = i / (double)N;
                double c = j / (double)N;
                int idx = Idx(i, j);
                pos[idx] = new[]
                {
                    a * p0[0] + b * p1[0] + c * p2[0],
                    a * p0[1] + b * p1[1] + c * p2[1],
                    a * p0[2] + b * p1[2] + c * p2[2],
                };
                double tv = a * t0 + b * t1 + c * t2;
                rgb[idx] = TurboColormap.Sample(tv);
            }
        }

        void Emit(int ia, int ib, int ic)
        {
            var pa = pos[ia]; var pb = pos[ib]; var pc = pos[ic];
            var (rA, gA, bA) = rgb[ia];
            var (rB, gB, bB) = rgb[ib];
            var (rC, gC, bC) = rgb[ic];
            output.Add(new ProjectedFace
            {
                ScreenPts = new[]
                {
                    new[] { pa[0], pa[1] },
                    new[] { pb[0], pb[1] },
                    new[] { pc[0], pc[1] },
                },
                Pts3D = new[]
                {
                    new[] { pa[0], pa[1], pa[2] },
                    new[] { pb[0], pb[1], pb[2] },
                    new[] { pc[0], pc[1], pc[2] },
                },
                R = new[] { rA, rB, rC },
                G = new[] { gA, gB, gC },
                B = new[] { bA, bB, bC },
                Depth = depth,
            });
        }

        for (int i = 0; i < N; i++)
        {
            for (int j = 0; j < N - i; j++)
            {
                Emit(Idx(i, j), Idx(i + 1, j), Idx(i, j + 1));
                if (i + j < N - 1)
                    Emit(Idx(i + 1, j), Idx(i + 1, j + 1), Idx(i, j + 1));
            }
        }
    }
}
