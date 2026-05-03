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
        double? eMinOverride, double? eMaxOverride,
        bool showContourLabels = false,
        bool showMeshLines = true,
        bool showPlainMeshLines = false,
        bool showPlainGeometryEdges = true,
        bool showPlainLighting = true,
        bool showLinesLighting = true,
        bool showPlainDimensions = false,
        IList<DimensionWorld>? dimensionsWorld = null)
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

        // Per-vertex world-space normals for the Lambert grayscale path.
        // Same area-weighted accumulation as the GL renderer (sum of
        // incident face cross products, then normalise).  The path is
        // shared between Plain mode (lit faces are the only ones drawn)
        // and Lines mode with lighting on (lit faces sit underneath the
        // coloured iso-contour lines so the 3D shape stays readable).
        bool plainLit = effectiveMode == DisplayMode.Plain && showPlainLighting;
        bool linesLit = effectiveMode == DisplayMode.Lines && showLinesLighting;
        bool litFaces = plainLit || linesLit;
        bool needNormals = litFaces;
        double[] nx = needNormals ? new double[ns.Length] : Array.Empty<double>();
        double[] ny = needNormals ? new double[ns.Length] : Array.Empty<double>();
        double[] nz = needNormals ? new double[ns.Length] : Array.Empty<double>();
        if (needNormals)
        {
            foreach (var face in bfaces)
            {
                if (face.Length < 3) continue;
                int v0 = face[0], v1 = face[1], v2 = face[2];
                var p0 = dp[v0]; var p1 = dp[v1]; var p2 = dp[v2];
                double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2];
                double vx = p2[0] - p0[0], vy = p2[1] - p0[1], vz = p2[2] - p0[2];
                double fnx = uy * vz - uz * vy;
                double fny = uz * vx - ux * vz;
                double fnz = ux * vy - uy * vx;
                foreach (var v in face) { nx[v] += fnx; ny[v] += fny; nz[v] += fnz; }
            }
            for (int i = 0; i < ns.Length; i++)
            {
                double l = Math.Sqrt(nx[i] * nx[i] + ny[i] * ny[i] + nz[i] * nz[i]);
                if (l > 1e-12) { nx[i] /= l; ny[i] /= l; nz[i] /= l; }
            }
        }
        // Light direction (head-light) = camera forward in world space.
        double lx = cam.Forward.X, ly = cam.Forward.Y, lz = cam.Forward.Z;

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
            if (effectiveMode == DisplayMode.Plain && !plainLit)
            {
                // Unlit Plain (the old "Wireframe" behaviour): flat white
                // faces.  The page background is also white, so the fill
                // is invisible but still occludes back-edges.
                for (int j = 0; j < face.Length; j++) { rArr[j] = 1; gArr[j] = 1; bArr[j] = 1; }
            }
            else if (litFaces)
            {
                // Half-Lambert grayscale (head-light), two-sided via abs()
                // so back faces aren't black.  Half-Lambert (square the
                // wrapped dot) gives a softer terminator than plain
                // Lambert, which reads better on monochrome surfaces.
                // Same formulation runs in the GL per-frame pass so the
                // PDF and the screen agree pixel-for-pixel.  Used for
                // both Plain (lit) and Lines (lit) modes — for Lines the
                // grayscale faces sit under the coloured iso-contours.
                for (int j = 0; j < face.Length; j++)
                {
                    int v = face[j];
                    double dot = Math.Abs(nx[v] * lx + ny[v] * ly + nz[v] * lz);
                    double wrap = 0.5 + 0.5 * dot;
                    double gray = 0.18 + 0.82 * wrap * wrap;
                    if (gray > 1) gray = 1;
                    rArr[j] = gray; gArr[j] = gray; bArr[j] = gray;
                }
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

            if ((effectiveMode == DisplayMode.Plain && showPlainMeshLines) ||
                (effectiveMode == DisplayMode.Plot  && showMeshLines))
            {
                for (int j = 0; j < pts.Length; j++)
                    wireEdges3D.Add((pts[j], pts[(j + 1) % pts.Length]));
            }
        }

        // Painter's algorithm: sort back-to-front
        exportFaces.Sort((a, b) => b.Depth.CompareTo(a.Depth));

        var zbuf = ZBufferRenderer.Build(exportFaces, w, h);
        var visibleEdges = new List<ProjectedEdge>();

        // Plain/plot mesh-line edges
        if ((effectiveMode == DisplayMode.Plain && showPlainMeshLines) ||
                (effectiveMode == DisplayMode.Plot && showMeshLines))
        {
            foreach (var (a, b) in wireEdges3D)
            {
                if (ZBufferRenderer.IsSegmentVisible(a, b, zbuf, w, h))
                    visibleEdges.Add(new ProjectedEdge { P1 = new[] { a[0], a[1] }, P2 = new[] { b[0], b[1] } });
            }
        }

        // Feature (silhouette / sharp-fold) edges:
        //   • Lines mode    — 20° dihedral, kept as a backdrop for the
        //     iso-contours that follow (more silhouette + crease lines).
        //   • Plain mode    — 35° dihedral when the user enables
        //     "Geometry edges": only reasonably sharp creases survive,
        //     so a smooth organic mesh shows just its silhouette while
        //     a hard-edged engineering part keeps its crease lines.
        bool emitFeatureEdges = effectiveMode == DisplayMode.Lines ||
                                (effectiveMode == DisplayMode.Plain && showPlainGeometryEdges);
        bool emitContours = effectiveMode == DisplayMode.Lines;
        if (emitFeatureEdges)
        {
            double angleDeg = effectiveMode == DisplayMode.Plain ? 35.0 : 20.0;
            var featPos = FeatureEdgeDetector.Extract(bfaces, dp, angleDeg);
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

        // Contour iso-lines and (optionally) value labels.  We build via
        // SmoothPolylines (rather than Smooth) so the same connected
        // polyline structure feeds both the per-segment line rendering and
        // the label-candidate generation, keeping the PDF labels at the
        // same arc-midpoints as the on-screen overlay.
        var contours = new List<ProjectedContour>();
        var labelCandidates = new List<ContourLabelWorld>();
        if (emitContours && fv != null && !isPerElement)
        {
            var rawSegs = ContourGenerator.ComputeSegments(bfaces, dp, fv, efMin, efMax, contourN);
            var polylines = ContourGenerator.SmoothPolylines(rawSegs, 2);
            foreach (var pl in polylines)
            {
                double t = (pl.Level - efMin) / efSpan;
                var (cr, cg, cb) = TurboColormap.Sample(t);
                var pts = pl.Points;

                // Emit per-segment ProjectedContour for the line renderer,
                // gated by the same z-buffer visibility test as before.
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    var pa = Camera.Project(pts[i],     cam, orthoHH, w, h);
                    var pb = Camera.Project(pts[i + 1], cam, orthoHH, w, h);
                    if (!ZBufferRenderer.IsSegmentVisible(pa, pb, zbuf, w, h)) continue;
                    contours.Add(new ProjectedContour
                    {
                        P1 = new[] { pa[0], pa[1] }, P2 = new[] { pb[0], pb[1] },
                        R = cr, G = cg, B = cb,
                    });
                }

                // Label candidate at the polyline's arc midpoint, with a
                // chord-window tangent (robust against Chaikin wiggles).
                if (showContourLabels && pts.Count >= 2)
                {
                    double total = 0;
                    for (int i = 1; i < pts.Count; i++) total += Dist3(pts[i - 1], pts[i]);
                    if (total < 0.06) continue;
                    double half = total * 0.5, acc = 0;
                    int hit = 1;
                    for (int i = 1; i < pts.Count; i++)
                    {
                        double L = Dist3(pts[i - 1], pts[i]);
                        if (acc + L >= half) { hit = i; break; }
                        acc += L;
                    }
                    var aP = pts[hit - 1]; var bP = pts[hit];
                    double tt = Math.Max(0, Math.Min(1, (half - acc) / Math.Max(1e-12, Dist3(aP, bP))));
                    var mid = new[]
                    {
                        aP[0] + tt * (bP[0] - aP[0]),
                        aP[1] + tt * (bP[1] - aP[1]),
                        aP[2] + tt * (bP[2] - aP[2]),
                    };
                    int chord = Math.Min(3, Math.Min(hit, pts.Count - hit));
                    var lo = pts[Math.Max(0, hit - chord)];
                    var hi = pts[Math.Min(pts.Count - 1, hit - 1 + chord)];
                    var dir = new[] { hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2] };
                    double dl = Math.Sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
                    if (dl < 1e-12) dl = 1;
                    dir[0] /= dl; dir[1] /= dl; dir[2] /= dl;
                    labelCandidates.Add(new ContourLabelWorld
                    {
                        Pos = mid, TangentDir = dir,
                        Text = pl.Level.ToString("G3", System.Globalization.CultureInfo.InvariantCulture),
                        Length = total,
                    });
                }
            }
        }
        // Run the shared label placer once we have all candidates so PDF
        // labels land in the same screen positions the overlay computes
        // from the same world-space candidates.
        var placedLabels = labelCandidates.Count > 0
            ? ContourLabelPlacer.Place(labelCandidates, cam, orthoHH, w, h, fontSize: 11)
            : new List<PlacedContourLabel>();

        // Lines mode: when lighting is OFF, faces should be white so the
        // iso-lines and silhouette edges carry all the colour
        // information.  When lighting is ON, the lit-faces branch above
        // has already painted the faces with half-Lambert grayscale,
        // which we keep so the user sees 3D shape under the iso-lines.
        if (effectiveMode == DisplayMode.Lines && !linesLit)
        {
            foreach (var f in exportFaces)
            {
                for (int j = 0; j < f.R.Length; j++) { f.R[j] = 1; f.G[j] = 1; f.B[j] = 1; }
            }
        }

        int nEdges = bfaces.Sum(f => f.Length);
        var lp = LinePreset.Auto(nEdges);

        // Plain mode: no color bar, no field coloring.  The PDF colour
        // bar is keyed off scene.FieldName, so suppressing the field
        // name there mirrors the on-screen MeshOverlay behaviour.
        string? displayFieldName = dMode == DisplayMode.Plain
            ? null
            : activeField;

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

        // Plain-mode dimensioning overlay.  The toggle at the call site
        // already enforces the rule that dimensions are only available in
        // Plain mode with mesh lines off; this assertion is purely defensive
        // so the renderer can never observe an inconsistent flag set.
        var dimensions = new List<DimensionScreen>();
        if (showPlainDimensions && dMode == DisplayMode.Plain && !showPlainMeshLines && dimensionsWorld != null && dimensionsWorld.Count > 0)
        {
            dimensions = DimensionLayout.Project(
                dimensionsWorld, cam, orthoHH, w, h,
                bboxCenterWorld: new[] { 0.0, 0.0, 0.0 });
        }

        return new ExportScene
        {
            Faces = exportFaces, VisibleEdges = visibleEdges, Contours = contours,
            Bars = bars, Points = points, ContourLabels = placedLabels,
            Dimensions = dimensions,
            Lp = lp, FieldName = displayFieldName, FMin = efMin, FMax = efMax,
            W = w, H = h, Mode = dMode, Rotation = camParams.Rot,
        };
    }

    private static double Dist3(double[] a, double[] b)
    {
        double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
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
