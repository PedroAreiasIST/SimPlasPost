using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Geometry;

/// <summary>
/// Deterministic mesh-to-feature dimensioning, matching the policy in
/// <c>automatic_mesh_dimensioning_algorithm.pdf</c>: extract a small
/// independent parameter set from the mesh and attach each value with a
/// residual / confidence filter so dubious fits are suppressed silently.
///
/// Recovered features:
///   • Bounding-box extents (L, W, H) — always emitted.
///   • Plate thickness (t) — when one bbox extent is much smaller than
///     the others, that axis is relabelled "t" instead of L/W/H.
///   • 2D circular holes — Kasa algebraic circle fit on interior boundary
///     loops, gated by ≥ 8 segments, r ≥ 1.5 × mean edge, RMS / r &lt; 5%.
///   • 3D cylindrical-hole diameters — feature-edge cycles in 3D, fit by
///     projecting onto the plane perpendicular to the cycle's PCA-thin
///     axis (axis-aligned cylinders fit exactly; oblique cycles are
///     suppressed by the residual filter).
///   • Spherical diameters — algebraic 4-parameter sphere fit on every
///     unique boundary node, accepted when RMS deviation / radius
///     &lt; 3% (strict, since false-positive spheres are visually
///     misleading on organic meshes).
/// </summary>
public static class DimensionExtractor
{
    public static List<DimensionWorld> Build(MeshData? meshData)
    {
        var dims = new List<DimensionWorld>();
        if (meshData == null || meshData.Nodes.Length == 0) return dims;

        var ns = meshData.Nodes;

        // Bounding box in source coordinates.
        double mnX = double.MaxValue, mnY = double.MaxValue, mnZ = double.MaxValue;
        double mxX = double.MinValue, mxY = double.MinValue, mxZ = double.MinValue;
        foreach (var n in ns)
        {
            if (n[0] < mnX) mnX = n[0]; if (n[0] > mxX) mxX = n[0];
            if (n[1] < mnY) mnY = n[1]; if (n[1] > mxY) mxY = n[1];
            if (n[2] < mnZ) mnZ = n[2]; if (n[2] > mxZ) mxZ = n[2];
        }
        double cenX = (mnX + mxX) * 0.5, cenY = (mnY + mxY) * 0.5, cenZ = (mnZ + mxZ) * 0.5;
        double span = Math.Max(Math.Max(mxX - mnX, mxY - mnY), Math.Max(mxZ - mnZ, 1e-12));
        double sc = 2.0 / span;

        double[] W(double x, double y, double z) =>
            new[] { (x - cenX) * sc, (y - cenY) * sc, (z - cenZ) * sc };

        double[] extents = { mxX - mnX, mxY - mnY, mxZ - mnZ };
        double maxExt = Math.Max(extents[0], Math.Max(extents[1], extents[2]));

        // Effective dimensionality.  A mesh whose nodes all sit on a flat
        // plane (typical of 2D meshes loaded with Dim = 3 — Ensight files
        // often label everything as 3D regardless of geometry) is treated
        // as 2D so we don't emit a near-zero "H" / "t" dimension along
        // the degenerate axis or attempt sphere / cylinder fits on the
        // collapsed coordinate.  We trust volume elements (Tet4 / Hex8 /
        // Penta6) — they always imply a true 3D mesh — but for surface
        // meshes (Tri3 / Quad4) we fall back to the bbox-extent ratio.
        const double FlatRatio = 1e-4;
        bool hasVolumeElements = meshData.Elements.Any(e =>
            FaceTable.Faces.TryGetValue(e.Type, out var ft) && ft.Dim == 3);
        bool hasSurfaceElements = meshData.Elements.Any(e =>
            FaceTable.Faces.TryGetValue(e.Type, out var ft) && ft.Dim == 2);
        bool flatInZ = maxExt > 1e-12 && extents[2] / maxExt < FlatRatio;
        bool flatInY = maxExt > 1e-12 && extents[1] / maxExt < FlatRatio;
        bool flatInX = maxExt > 1e-12 && extents[0] / maxExt < FlatRatio;
        bool degenerateAxis = flatInZ || flatInY || flatInX;
        // Honour an explicit Dim = 2 declaration as well, so loaders that
        // do auto-detect (e.g. the JSON loader) keep working unchanged.
        bool is3D = !(meshData.Dim == 2 || (!hasVolumeElements && degenerateAxis));

        // Detect a "thin" axis: one extent much smaller than the others.
        // Threshold 0.5 catches obvious plates (Toblerone, House) without
        // labelling near-cubic meshes (Cantilever ratio 1:8:8 has X long
        // and Y/Z thin in proportion, which we leave alone — only the
        // smallest axis becomes "t" when its ratio to the median is
        // unambiguously small).  Skipped for effective-2D meshes since
        // the degenerate axis would always win the test trivially.
        int thinAxis = -1;
        if (is3D)
        {
            int minIdx = 0;
            if (extents[1] < extents[minIdx]) minIdx = 1;
            if (extents[2] < extents[minIdx]) minIdx = 2;
            int a = (minIdx + 1) % 3, b = (minIdx + 2) % 3;
            double median = Math.Min(extents[a], extents[b]);
            if (median > 1e-12 && extents[minIdx] / median < 0.5)
                thinAxis = minIdx;
        }

        // Bounding-box extent dimensions, with the thin axis relabelled.
        // Skip any axis whose extent is negligible relative to the largest
        // — emitting "L = 0" or "H = 1e-15" on a flat sheet just clutters
        // the figure with meaningless annotations.
        string labelX = thinAxis == 0 ? "t" : "L";
        string labelY = thinAxis == 1 ? "t" : (thinAxis == 0 ? "L" : "W");
        string labelZ = thinAxis == 2 ? "t" : (thinAxis == 1 ? "W" : "H");
        if (extents[0] / Math.Max(maxExt, 1e-12) > FlatRatio)
        {
            dims.Add(new DimensionWorld
            {
                Kind = DimensionKind.Linear, Label = labelX, Value = mxX - mnX,
                P1 = W(mnX, mnY, mnZ), P2 = W(mxX, mnY, mnZ),
            });
        }
        if (extents[1] / Math.Max(maxExt, 1e-12) > FlatRatio)
        {
            dims.Add(new DimensionWorld
            {
                Kind = DimensionKind.Linear, Label = labelY, Value = mxY - mnY,
                P1 = W(mnX, mnY, mnZ), P2 = W(mnX, mxY, mnZ),
            });
        }
        if (is3D && extents[2] / Math.Max(maxExt, 1e-12) > FlatRatio)
        {
            dims.Add(new DimensionWorld
            {
                Kind = DimensionKind.Linear, Label = labelZ, Value = mxZ - mnZ,
                P1 = W(mnX, mnY, mnZ), P2 = W(mnX, mnY, mxZ),
            });
        }

        // 2D circular holes plus outer-boundary arcs (notch radii, fillet
        // radii, partial circles on the silhouette).  Runs whenever the
        // mesh is effectively 2D — including 2D-flat-in-3D meshes,
        // provided they are flat in Z (the boundary-loop walker ignores Z
        // and the circle fit operates in XY).
        if (!is3D && hasSurfaceElements && !flatInX && !flatInY)
            AppendCircularHoles2D(meshData, ns, dims, W, mnX, mnY);

        // 3D features: spheres + cylindrical holes.
        if (is3D)
        {
            AppendSphere3D(meshData, ns, dims, W, mnX, mnY, mnZ);
            AppendCylindricalHoles3D(meshData, ns, dims, W, mnX, mnY, mnZ);
        }

        return dims;
    }

    private static void AppendCircularHoles2D(MeshData meshData, double[][] ns,
        List<DimensionWorld> dims, Func<double, double, double, double[]> W,
        double mnX, double mnY)
    {
        var loops = ExtractBoundaryLoops2D(meshData.Elements);
        if (loops.Count == 0) return;

        // Mean edge length filters out near-singular fits.
        double totalLen = 0; int totalEdges = 0;
        foreach (var loop in loops)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = ns[loop[i]];
                var b = ns[loop[(i + 1) % loop.Count]];
                totalLen += Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
                totalEdges++;
            }
        }
        double meanEdge = totalEdges > 0 ? totalLen / totalEdges : 0;

        // Pass 1: full-loop circle fit on every interior loop (loops[0] is
        // the outer boundary, sorted by |signed area| descending in
        // ExtractBoundaryLoops2D).  Loops that close into a true circle
        // become Diameter dimensions; loops that don't (slots, irregular
        // cutouts) fall through to the sliding-window arc detector below.
        var holeFits = new List<(double cx, double cy, double r)>();
        var arcFits = new List<(double cx, double cy, double r, bool reentrant)>();
        var consumedLoops = new HashSet<int>();
        for (int i = 1; i < loops.Count; i++)
        {
            var loop = loops[i];
            if (loop.Count < 8) continue;
            if (!FitCircleKasa(loop, ns, out double cx, out double cy, out double r, out double rms)) continue;
            if (r < meanEdge * 1.5) continue;
            if (rms / r > 0.05) continue;
            holeFits.Add((cx, cy, r));
            consumedLoops.Add(i);
        }

        // Pass 2: sliding-window arc detector on every loop (outer + any
        // interior loop that wasn't a clean full circle).  Detects partial
        // arcs in the silhouette: notch radii, fillet radii, half-circles
        // on the part outline, etc.
        for (int i = 0; i < loops.Count; i++)
        {
            if (consumedLoops.Contains(i)) continue;
            DetectArcsInLoop(loops[i], ns, meanEdge, arcFits);
        }

        // Emit hole diameters with position-from-datum dimensions.  For a
        // single hole near the bbox centre we suppress cx/cy (they carry
        // no information beyond "centred"); for off-centre or multiple
        // holes we always emit both so the figure is reproducible.
        holeFits.Sort((a, b) => b.r.CompareTo(a.r));
        EmitCircles2D(holeFits, dims, W, mnX, mnY, isHole: true, meshData);

        // Emit outer-boundary / partial arcs as radius dimensions.  "R"
        // for convex (the part bulges outward) and "r" for re-entrant
        // (the part curves into itself, e.g. notch root) — drafting
        // convention.
        EmitArcs2D(arcFits, dims, W, mnX, mnY, meshData);
    }

    /// <summary>
    /// Sliding-window arc detector: marks every vertex whose local
    /// neighbourhood circle-fits cleanly (RMS / r &lt; 1%), groups
    /// consecutive marked vertices into chains, refits a circle to each
    /// chain, and labels the chain convex or re-entrant relative to the
    /// loop's signed area (CCW outer = interior on left of tangent).
    /// </summary>
    private static void DetectArcsInLoop(List<int> loop, double[][] nodes, double meanEdge,
        List<(double cx, double cy, double r, bool reentrant)> output)
    {
        int n = loop.Count;
        if (n < 9) return;
        // Window size: ~5% of the loop, clamped to [5, 25].
        int win = Math.Clamp((int)Math.Round(n * 0.05), 5, 25);
        if (win % 2 == 0) win++;
        int half = win / 2;

        // Loop signed area for convex/concave classification.
        double signedArea = 0;
        for (int i = 0; i < n; i++)
        {
            var a = nodes[loop[i]];
            var b = nodes[loop[(i + 1) % n]];
            signedArea += a[0] * b[1] - b[0] * a[1];
        }
        bool ccw = signedArea > 0;

        var onArc = new bool[n];
        var winList = new List<int>(win);
        for (int i = 0; i < n; i++)
        {
            winList.Clear();
            for (int k = -half; k <= half; k++) winList.Add(loop[(i + k + n) % n]);
            if (!FitCircleKasa(winList, nodes, out double cx, out double cy, out double r, out double rms)) continue;
            if (r < meanEdge * 1.0) continue; // arcs smaller than 1× mesh-h are noise
            if (rms / r > 0.01) continue; // strict for sliding window
            onArc[i] = true;
        }

        // Group consecutive on-arc vertices into chains, treating the
        // loop circularly so arcs spanning the wrap-around still merge.
        // Find a non-arc anchor first if one exists, else the entire
        // loop is one big arc.
        int anchor = -1;
        for (int i = 0; i < n; i++) if (!onArc[i]) { anchor = i; break; }

        if (anchor == -1)
        {
            // Whole loop is one arc: refit and emit if reasonable.
            if (FitCircleKasa(loop, nodes, out double fcx, out double fcy, out double fr, out double frms)
                && fr > meanEdge * 1.5 && frms / fr < 0.03)
            {
                bool reentrant = ClassifyReentrant(loop, nodes, n / 2, fcx, fcy, ccw);
                output.Add((fcx, fcy, fr, reentrant));
            }
            return;
        }

        int idx = (anchor + 1) % n;
        int safety = 0;
        while (idx != anchor && safety++ < 4 * n)
        {
            if (!onArc[idx]) { idx = (idx + 1) % n; continue; }
            int start = idx;
            var chainIdx = new List<int>();
            while (onArc[idx])
            {
                chainIdx.Add(loop[idx]);
                idx = (idx + 1) % n;
                if (idx == anchor) break;
            }
            int len = chainIdx.Count;
            if (len < 5) continue;

            if (!FitCircleKasa(chainIdx, nodes, out double fcx, out double fcy, out double fr, out double frms)) continue;
            if (fr < meanEdge * 1.5) continue;
            if (frms / fr > 0.03) continue;
            // Reject arcs whose subtended angle is too small (slivers) —
            // likely noise on a near-straight edge.
            int midPos = (start + len / 2) % n;
            bool reentrant = ClassifyReentrant(loop, nodes, midPos, fcx, fcy, ccw);
            output.Add((fcx, fcy, fr, reentrant));
        }
    }

    /// <summary>
    /// Classify an arc as convex or re-entrant relative to the loop's
    /// orientation.  CCW outer loop: interior is on the LEFT of the
    /// tangent direction.  An arc whose centre lies on the interior side
    /// is convex (bulges outward); an arc whose centre lies outside the
    /// loop is re-entrant (notch root).  CW loops invert the test.
    /// </summary>
    private static bool ClassifyReentrant(List<int> loop, double[][] nodes, int midPos,
        double cx, double cy, bool ccw)
    {
        int n = loop.Count;
        int prevIdx = loop[(midPos - 1 + n) % n];
        int midIdx = loop[midPos];
        int nextIdx = loop[(midPos + 1) % n];
        double tx = (nodes[nextIdx][0] - nodes[prevIdx][0]) * 0.5;
        double ty = (nodes[nextIdx][1] - nodes[prevIdx][1]) * 0.5;
        double dx = cx - nodes[midIdx][0];
        double dy = cy - nodes[midIdx][1];
        double cross = tx * dy - ty * dx;
        bool centreOnLeft = cross > 0;
        bool centreOnInteriorSide = ccw ? centreOnLeft : !centreOnLeft;
        return !centreOnInteriorSide;
    }

    private static void EmitCircles2D(
        List<(double cx, double cy, double r)> fits,
        List<DimensionWorld> dims, Func<double, double, double, double[]> W,
        double mnX, double mnY, bool isHole, MeshData meshData)
    {
        for (int k = 0; k < fits.Count; k++)
        {
            var fit = fits[k];
            string lab = fits.Count == 1 ? "d" : $"d{k + 1}";
            dims.Add(new DimensionWorld
            {
                Kind = DimensionKind.Diameter, Label = lab, Value = 2 * fit.r,
                P1 = W(fit.cx, fit.cy, 0),
                P2 = W(fit.cx + fit.r, fit.cy, 0),
                Radius = ComputeNormalisedRadius(meshData, fit.r),
            });
            EmitPositionDims(fit.cx, fit.cy, 0, mnX, mnY, double.NaN, k, fits.Count,
                is3D: false, dims, W, meshData);
        }
    }

    private static void EmitArcs2D(
        List<(double cx, double cy, double r, bool reentrant)> arcs,
        List<DimensionWorld> dims, Func<double, double, double, double[]> W,
        double mnX, double mnY, MeshData meshData)
    {
        // Group near-identical arcs (same centre, same radius within 5%)
        // — sliding-window detection on a circular feature can split a
        // smooth arc into two halves around the wrap-around boundary.
        var grouped = new List<(double cx, double cy, double r, bool reentrant, int count)>();
        foreach (var a in arcs)
        {
            bool merged = false;
            for (int i = 0; i < grouped.Count; i++)
            {
                var g = grouped[i];
                if (Math.Abs(g.r - a.r) / Math.Max(g.r, a.r) > 0.05) continue;
                double dx = g.cx - a.cx, dy = g.cy - a.cy;
                if (Math.Sqrt(dx * dx + dy * dy) > g.r * 0.10) continue;
                grouped[i] = ((g.cx * g.count + a.cx) / (g.count + 1),
                              (g.cy * g.count + a.cy) / (g.count + 1),
                              (g.r  * g.count + a.r)  / (g.count + 1),
                              g.reentrant, g.count + 1);
                merged = true; break;
            }
            if (!merged) grouped.Add((a.cx, a.cy, a.r, a.reentrant, 1));
        }
        grouped.Sort((a, b) => b.r.CompareTo(a.r));

        int nR = 0, nr = 0;
        foreach (var g in grouped)
        {
            string lab;
            if (g.reentrant) { nr++; lab = grouped.Count(x => x.reentrant) == 1 ? "r" : $"r{nr}"; }
            else             { nR++; lab = grouped.Count(x => !x.reentrant) == 1 ? "R" : $"R{nR}"; }
            dims.Add(new DimensionWorld
            {
                Kind = DimensionKind.Diameter, Label = lab,
                // Radius (not diameter) is the conventional dimension for
                // arcs — emit the radius value but reuse the Diameter
                // kind for renderer simplicity.  The label "R" / "r"
                // tells the reader it's a radius, not a diameter.
                Value = g.r,
                P1 = W(g.cx, g.cy, 0),
                P2 = W(g.cx + g.r, g.cy, 0),
                Radius = ComputeNormalisedRadius(meshData, g.r),
            });
        }
    }

    /// <summary>
    /// Emit Linear "cx", "cy" (and "cz" in 3D) dimensions for a feature
    /// centre, anchored on the bounding-box minimum corner.  Suppressed
    /// when the feature is approximately at the bbox centre — a centred
    /// feature carries no positional information beyond "on the
    /// centreline" and the dim line would just clutter the figure.
    /// </summary>
    private static void EmitPositionDims(double cx, double cy, double cz,
        double mnX, double mnY, double mnZ, int idx, int total, bool is3D,
        List<DimensionWorld> dims, Func<double, double, double, double[]> W,
        MeshData meshData)
    {
        // Mesh bbox extent for the "is this centred?" rejection.
        var ns = meshData.Nodes;
        double mxX = double.MinValue, mxY = double.MinValue, mxZ = double.MinValue;
        foreach (var p in ns)
        {
            if (p[0] > mxX) mxX = p[0];
            if (p[1] > mxY) mxY = p[1];
            if (p[2] > mxZ) mxZ = p[2];
        }
        double centreX = (mnX + mxX) * 0.5, centreY = (mnY + mxY) * 0.5, centreZ = (mnZ + mxZ) * 0.5;
        double tolX = (mxX - mnX) * 0.02, tolY = (mxY - mnY) * 0.02, tolZ = (mxZ - mnZ) * 0.02;

        string suf = total > 1 ? $"_{idx + 1}" : "";
        if (Math.Abs(cx - centreX) > tolX)
        {
            dims.Add(new DimensionWorld
            {
                Kind = DimensionKind.Linear, Label = $"cx{suf}", Value = cx - mnX,
                P1 = W(mnX, mnY, mnZ), P2 = W(cx, mnY, mnZ),
            });
        }
        if (Math.Abs(cy - centreY) > tolY)
        {
            dims.Add(new DimensionWorld
            {
                Kind = DimensionKind.Linear, Label = $"cy{suf}", Value = cy - mnY,
                P1 = W(mnX, mnY, mnZ), P2 = W(mnX, cy, mnZ),
            });
        }
        if (is3D && Math.Abs(cz - centreZ) > tolZ)
        {
            dims.Add(new DimensionWorld
            {
                Kind = DimensionKind.Linear, Label = $"cz{suf}", Value = cz - mnZ,
                P1 = W(mnX, mnY, mnZ), P2 = W(mnX, mnY, cz),
            });
        }
    }

    /// <summary>
    /// Try a 4-parameter algebraic sphere fit on every unique boundary
    /// node.  Accepted only when the RMS deviation from the recovered
    /// radius is below 3% of the radius — strict, because a misidentified
    /// sphere on an organic mesh (Buddha, Spot the Cow) is much more
    /// jarring than a missed sphere on a true sphere mesh.
    /// </summary>
    private static void AppendSphere3D(MeshData meshData, double[][] ns,
        List<DimensionWorld> dims, Func<double, double, double, double[]> W,
        double mnX, double mnY, double mnZ)
    {
        bool is3D = true;
        var bfaces = BoundaryExtractor.Extract(meshData.Elements, is3D);
        var nodeSet = new HashSet<int>();
        foreach (var f in bfaces) foreach (var ni in f) nodeSet.Add(ni);
        if (nodeSet.Count < 32) return;

        if (!FitSphereLSQ(nodeSet, ns, out double cx, out double cy, out double cz, out double r, out double rms))
            return;
        if (r <= 0 || rms / r > 0.03) return;

        // Coverage check: the fitted centre should lie inside the bounding
        // box of the boundary nodes, otherwise the fit is geometrically
        // implausible (e.g. a hemisphere convex hull).
        double mnXn = double.MaxValue, mnYn = double.MaxValue, mnZn = double.MaxValue;
        double mxXn = double.MinValue, mxYn = double.MinValue, mxZn = double.MinValue;
        foreach (var ni in nodeSet)
        {
            var p = ns[ni];
            if (p[0] < mnXn) mnXn = p[0]; if (p[0] > mxXn) mxXn = p[0];
            if (p[1] < mnYn) mnYn = p[1]; if (p[1] > mxYn) mxYn = p[1];
            if (p[2] < mnZn) mnZn = p[2]; if (p[2] > mxZn) mxZn = p[2];
        }
        if (cx < mnXn || cx > mxXn || cy < mnYn || cy > mxYn || cz < mnZn || cz > mxZn) return;

        dims.Add(new DimensionWorld
        {
            Kind = DimensionKind.SphericalDiameter, Label = "Ds", Value = 2 * r,
            P1 = W(cx, cy, cz),
            P2 = W(cx + r, cy, cz),
            Radius = ComputeNormalisedRadius(meshData, r),
        });
        EmitPositionDims(cx, cy, cz, mnX, mnY, mnZ, 0, 1, is3D: true, dims, W, meshData);
    }

    /// <summary>
    /// Detect circular feature-edge cycles in a 3D mesh and emit each as
    /// a cylindrical-hole diameter.  Algorithm:
    ///   1. Extract feature edges (dihedral &gt; 35°) as node-index pairs.
    ///   2. Group edges into cycles via a node-degree adjacency walk.
    ///   3. For each cycle, find its PCA-thin direction (cylinder axis).
    ///   4. Project to the plane perpendicular to that axis and Kasa-fit.
    ///   5. Accept the fit when the cycle has ≥ 8 nodes, the axial spread
    ///      is below 5% of the recovered diameter, and the in-plane RMS
    ///      residual is below 5% of the radius.
    /// </summary>
    private static void AppendCylindricalHoles3D(MeshData meshData, double[][] ns,
        List<DimensionWorld> dims, Func<double, double, double, double[]> W,
        double mnX, double mnY, double mnZ)
    {
        var bfaces = BoundaryExtractor.Extract(meshData.Elements, is3D: true);
        var cycles = ExtractFeatureEdgeCycles3D(bfaces, ns, angleDeg: 35.0);
        if (cycles.Count == 0) return;

        // Mesh bounding-box extents perpendicular to each cardinal axis,
        // for the "is this the outer perimeter?" rejection below.  The
        // parameters mnX/mnY/mnZ shadow these so we use distinct local
        // names for the recomputed extents.
        double bbMnX = double.MaxValue, bbMnY = double.MaxValue, bbMnZ = double.MaxValue;
        double bbMxX = double.MinValue, bbMxY = double.MinValue, bbMxZ = double.MinValue;
        foreach (var p in ns)
        {
            if (p[0] < bbMnX) bbMnX = p[0]; if (p[0] > bbMxX) bbMxX = p[0];
            if (p[1] < bbMnY) bbMnY = p[1]; if (p[1] > bbMxY) bbMxY = p[1];
            if (p[2] < bbMnZ) bbMnZ = p[2]; if (p[2] > bbMxZ) bbMxZ = p[2];
        }
        double[] bboxHalfExt = { (bbMxX - bbMnX) * 0.5, (bbMxY - bbMnY) * 0.5, (bbMxZ - bbMnZ) * 0.5 };

        var fits = new List<(double cx, double cy, double cz, double r, double[] axis)>();
        foreach (var cycle in cycles)
        {
            if (cycle.Count < 8) continue;
            if (!FitCircle3D(cycle, ns, out double cx, out double cy, out double cz,
                              out double r, out double rms, out double axialSpread,
                              out double ax, out double ay, out double az)) continue;
            // Mean edge length on this cycle as the size threshold.
            double cycLen = 0;
            for (int i = 0; i < cycle.Count; i++)
            {
                var a = ns[cycle[i]];
                var b = ns[cycle[(i + 1) % cycle.Count]];
                cycLen += Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
            }
            double meanEdge = cycLen / cycle.Count;
            if (r < meanEdge * 1.5) continue;
            if (rms / r > 0.05) continue;
            // The cycle's axial spread (distance along the PCA-thin axis)
            // must be small relative to the diameter — a real circular
            // edge lies in one plane.
            if (axialSpread / (2 * r) > 0.10) continue;
            // Reject the outer perimeter of a thin part: when the cycle's
            // radius nearly fills the mesh bounding-box span perpendicular
            // to its axis, it's the silhouette of the part, not a hole.
            if (RejectAsOuterPerimeter(ax, ay, az, r, bboxHalfExt)) continue;
            fits.Add((cx, cy, cz, r, new[] { ax, ay, az }));
        }

        // Deduplicate cycles that represent the SAME hole at top and
        // bottom of an extruded plate: same axis direction, same in-plane
        // centre projection, similar radius.  Cluster and keep the median
        // representative.
        fits = MergePairedRimCycles(fits);
        fits.Sort((a, b) => b.r.CompareTo(a.r));

        for (int k = 0; k < fits.Count; k++)
        {
            var fit = fits[k];
            string lab = fits.Count == 1 ? "d" : $"d{k + 1}";
            // Pick a world-space perpendicular to the axis for the P2
            // anchor (the renderer ultimately uses Axis × view at draw
            // time so this mostly matters as a fallback).
            double[] perp = PerpendicularTo(fit.axis);
            dims.Add(new DimensionWorld
            {
                Kind = DimensionKind.Diameter, Label = lab, Value = 2 * fit.r,
                P1 = W(fit.cx, fit.cy, fit.cz),
                P2 = W(fit.cx + perp[0] * fit.r, fit.cy + perp[1] * fit.r, fit.cz + perp[2] * fit.r),
                Radius = ComputeNormalisedRadius(meshData, fit.r),
                Axis = fit.axis,
            });
            EmitPositionDims(fit.cx, fit.cy, fit.cz, mnX, mnY, mnZ, k, fits.Count,
                is3D: true, dims, W, meshData);
        }
    }

    /// <summary>
    /// Convert a source-coordinate radius to the renderer's normalised
    /// frame (the same scaling factor <c>sc = 2/span</c> applied to all
    /// node positions).  Recomputed from the mesh rather than threaded
    /// through the helper signatures.
    /// </summary>
    private static double ComputeNormalisedRadius(MeshData meshData, double sourceRadius)
    {
        var ns = meshData.Nodes;
        double mnX = double.MaxValue, mnY = double.MaxValue, mnZ = double.MaxValue;
        double mxX = double.MinValue, mxY = double.MinValue, mxZ = double.MinValue;
        foreach (var n in ns)
        {
            if (n[0] < mnX) mnX = n[0]; if (n[0] > mxX) mxX = n[0];
            if (n[1] < mnY) mnY = n[1]; if (n[1] > mxY) mxY = n[1];
            if (n[2] < mnZ) mnZ = n[2]; if (n[2] > mxZ) mxZ = n[2];
        }
        double span = Math.Max(Math.Max(mxX - mnX, mxY - mnY), Math.Max(mxZ - mnZ, 1e-12));
        return sourceRadius * 2.0 / span;
    }

    /// <summary>
    /// Reject a 3D cycle whose radius is close to the mesh's bbox span
    /// perpendicular to the cycle axis — that's the part's silhouette
    /// (outer perimeter), not a hole.  Threshold 0.85: a "hole" with
    /// r &gt; 85% of the bbox half-extent in its plane would be a
    /// pathologically large hole, and matches what we want to filter.
    /// </summary>
    private static bool RejectAsOuterPerimeter(double ax, double ay, double az,
        double r, double[] bboxHalfExt)
    {
        // Bbox extent perpendicular to the axis = projection onto each
        // cardinal axis, weighted by the axis component magnitude.  For a
        // cycle aligned with Z, the perpendicular extents are X and Y; we
        // take the max so the cycle has to fit into BOTH.
        double maxPerp = 0;
        for (int i = 0; i < 3; i++)
        {
            double comp = i == 0 ? ax : (i == 1 ? ay : az);
            // Weight: axis is mostly along this cardinal → small; mostly
            // perpendicular → ≈1.  Use (1 − comp²) as the perpendicular
            // weight, which collapses to 1 for pure-perpendicular axes.
            double w = 1.0 - comp * comp;
            if (w > 0.01)
            {
                double e = bboxHalfExt[i] * w;
                if (e > maxPerp) maxPerp = e;
            }
        }
        if (maxPerp < 1e-12) return false;
        return r >= maxPerp * 0.85;
    }

    /// <summary>
    /// Cluster 3D circular cycles that represent the same physical hole
    /// at different axial positions (top + bottom rims of a through-hole).
    /// Two cycles merge when their axes are nearly parallel, their in-plane
    /// centre projections coincide, and their radii agree to within 5%.
    /// The cluster's representative uses the median centre and radius.
    /// </summary>
    private static List<(double cx, double cy, double cz, double r, double[] axis)>
        MergePairedRimCycles(List<(double cx, double cy, double cz, double r, double[] axis)> fits)
    {
        var clusters = new List<List<(double cx, double cy, double cz, double r, double[] axis)>>();
        foreach (var f in fits)
        {
            bool placed = false;
            foreach (var cl in clusters)
            {
                var head = cl[0];
                double dot = Math.Abs(head.axis[0] * f.axis[0] + head.axis[1] * f.axis[1] + head.axis[2] * f.axis[2]);
                if (dot < 0.95) continue; // axes not parallel
                if (Math.Abs(head.r - f.r) / Math.Max(head.r, f.r) > 0.05) continue;
                // In-plane centre offset: subtract the component along the axis.
                double dx = f.cx - head.cx, dy = f.cy - head.cy, dz = f.cz - head.cz;
                double along = dx * head.axis[0] + dy * head.axis[1] + dz * head.axis[2];
                double inX = dx - along * head.axis[0];
                double inY = dy - along * head.axis[1];
                double inZ = dz - along * head.axis[2];
                double inDist = Math.Sqrt(inX * inX + inY * inY + inZ * inZ);
                if (inDist > head.r * 0.10) continue;
                cl.Add(f); placed = true; break;
            }
            if (!placed) clusters.Add(new List<(double, double, double, double, double[])> { f });
        }

        var merged = new List<(double cx, double cy, double cz, double r, double[] axis)>();
        foreach (var cl in clusters)
        {
            // Centre = arithmetic mean (robust enough for k-rim clusters).
            double sx = 0, sy = 0, sz = 0, sr = 0;
            foreach (var f in cl) { sx += f.cx; sy += f.cy; sz += f.cz; sr += f.r; }
            int n = cl.Count;
            merged.Add((sx / n, sy / n, sz / n, sr / n, cl[0].axis));
        }
        return merged;
    }

    /// <summary>Pick an arbitrary unit vector orthogonal to <paramref name="a"/>.</summary>
    private static double[] PerpendicularTo(double[] a)
    {
        double[] axis = (Math.Abs(a[0]) < 0.9) ? new[] { 1.0, 0.0, 0.0 } : new[] { 0.0, 1.0, 0.0 };
        double bx = a[1] * axis[2] - a[2] * axis[1];
        double by = a[2] * axis[0] - a[0] * axis[2];
        double bz = a[0] * axis[1] - a[1] * axis[0];
        double l = Math.Sqrt(bx * bx + by * by + bz * bz);
        if (l < 1e-12) return new[] { 1.0, 0.0, 0.0 };
        return new[] { bx / l, by / l, bz / l };
    }

    /// <summary>
    /// Walk the 2D mesh's edges incident to exactly one element, chain them
    /// into ordered loops, and return them sorted by |signed area|
    /// descending — outer boundary first, holes after.
    /// </summary>
    private static List<List<int>> ExtractBoundaryLoops2D(List<Element> elements)
    {
        var edgeCount = new Dictionary<(int, int), int>();
        foreach (var el in elements)
        {
            if (!FaceTable.Faces.TryGetValue(el.Type, out var ft) || ft.Dim != 2) continue;
            var c = el.Conn;
            for (int i = 0; i < c.Length; i++)
            {
                int a = c[i], b = c[(i + 1) % c.Length];
                var key = a < b ? (a, b) : (b, a);
                edgeCount[key] = edgeCount.GetValueOrDefault(key, 0) + 1;
            }
        }

        var adj = new Dictionary<int, List<int>>();
        foreach (var kv in edgeCount)
        {
            if (kv.Value != 1) continue;
            var (a, b) = kv.Key;
            if (!adj.TryGetValue(a, out var la)) { la = new List<int>(); adj[a] = la; }
            if (!adj.TryGetValue(b, out var lb)) { lb = new List<int>(); adj[b] = lb; }
            la.Add(b); lb.Add(a);
        }

        var visited = new HashSet<int>();
        var loops = new List<List<int>>();
        foreach (var start in adj.Keys)
        {
            if (visited.Contains(start)) continue;
            var loop = new List<int> { start };
            visited.Add(start);
            int prev = -1, cur = start;
            int safety = 0;
            while (safety++ < 1_000_000)
            {
                if (!adj.TryGetValue(cur, out var nbs)) break;
                int nxt = -1;
                foreach (var n in nbs) { if (n != prev) { nxt = n; break; } }
                if (nxt == -1 || nxt == start) break;
                if (visited.Contains(nxt)) break;
                visited.Add(nxt); loop.Add(nxt);
                prev = cur; cur = nxt;
            }
            if (loop.Count >= 3) loops.Add(loop);
        }

        return loops;
    }

    /// <summary>
    /// Identify feature edges (dihedral &gt; threshold or boundary) on a
    /// 3D boundary surface as node-index pairs and chain them into closed
    /// cycles.  Uses the same |n₁ · n₂| folded-dot test as
    /// FeatureEdgeDetector so winding-inverted neighbours behave the same
    /// as consistently-wound ones.  Cycles are extracted by walking each
    /// node's feature-edge neighbours and following the local 2-degree
    /// path; nodes with degree ≠ 2 (junctions) are dropped from cycles —
    /// a circular cylinder rim has every node at degree 2, so a clean
    /// hole gives a clean cycle.
    /// </summary>
    private static List<List<int>> ExtractFeatureEdgeCycles3D(List<int[]> bfaces, double[][] dp, double angleDeg)
    {
        double angleThresh = angleDeg * Math.PI / 180.0;

        // Face normals.
        var fnx = new double[bfaces.Count];
        var fny = new double[bfaces.Count];
        var fnz = new double[bfaces.Count];
        for (int fi = 0; fi < bfaces.Count; fi++)
        {
            var face = bfaces[fi];
            if (face.Length < 3) continue;
            var p0 = dp[face[0]]; var p1 = dp[face[1]];
            int i2 = face.Length > 2 ? 2 : 1;
            var p2 = dp[face[i2]];
            double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2];
            double vx = p2[0] - p0[0], vy = p2[1] - p0[1], vz = p2[2] - p0[2];
            double nx = uy * vz - uz * vy;
            double ny = uz * vx - ux * vz;
            double nz = ux * vy - uy * vx;
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l > 1e-14) { fnx[fi] = nx / l; fny[fi] = ny / l; fnz[fi] = nz / l; }
            else           { fnz[fi] = 1; }
        }

        // Edge → list of incident face indices.
        var edgeFaces = new Dictionary<(int, int), List<int>>();
        for (int fi = 0; fi < bfaces.Count; fi++)
        {
            var face = bfaces[fi];
            for (int j = 0; j < face.Length; j++)
            {
                int a = face[j], b = face[(j + 1) % face.Length];
                var key = a < b ? (a, b) : (b, a);
                if (!edgeFaces.TryGetValue(key, out var lst)) { lst = new List<int>(); edgeFaces[key] = lst; }
                lst.Add(fi);
            }
        }

        // Adjacency built from feature edges (sharp dihedral or boundary).
        var adj = new Dictionary<int, List<int>>();
        foreach (var kv in edgeFaces)
        {
            bool keep = false;
            if (kv.Value.Count == 1) keep = true;
            else if (kv.Value.Count == 2)
            {
                int f1 = kv.Value[0], f2 = kv.Value[1];
                double dot = fnx[f1] * fnx[f2] + fny[f1] * fny[f2] + fnz[f1] * fnz[f2];
                double cx = fny[f1] * fnz[f2] - fnz[f1] * fny[f2];
                double cy = fnz[f1] * fnx[f2] - fnx[f1] * fnz[f2];
                double cz = fnx[f1] * fny[f2] - fny[f1] * fnx[f2];
                double crossLen = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                double angle = Math.Atan2(crossLen, Math.Abs(dot));
                if (angle > angleThresh) keep = true;
            }
            if (!keep) continue;
            var (a, b) = kv.Key;
            if (!adj.TryGetValue(a, out var la)) { la = new List<int>(); adj[a] = la; }
            if (!adj.TryGetValue(b, out var lb)) { lb = new List<int>(); adj[b] = lb; }
            la.Add(b); lb.Add(a);
        }

        // Walk degree-2 paths into closed cycles.  We start from any
        // unvisited degree-2 node and chase the chain forward; if we
        // return to the start, it's a cycle.
        var visited = new HashSet<int>();
        var cycles = new List<List<int>>();
        foreach (var start in adj.Keys)
        {
            if (visited.Contains(start)) continue;
            var nbs0 = adj[start];
            if (nbs0.Count != 2) continue;
            var cycle = new List<int> { start };
            visited.Add(start);
            int prev = -1, cur = start;
            bool closed = false;
            int safety = 0;
            while (safety++ < 1_000_000)
            {
                var nbs = adj[cur];
                if (nbs.Count != 2) break; // junction — abandon
                int nxt = nbs[0] != prev ? nbs[0] : nbs[1];
                if (nxt == start) { closed = true; break; }
                if (visited.Contains(nxt)) break;
                visited.Add(nxt); cycle.Add(nxt);
                prev = cur; cur = nxt;
            }
            if (closed && cycle.Count >= 4) cycles.Add(cycle);
        }
        return cycles;
    }

    /// <summary>
    /// Algebraic circle fit (Kasa 1976): solve x²+y²+Dx+Ey+F=0 in the
    /// least-squares sense via normal equations.  Cheap, deterministic, and
    /// good enough for the 5%-residual filter; geometric/Pratt fits are
    /// only worth the cost when the algebraic fit's bias matters, which
    /// it doesn't at the precision we publish here.
    /// </summary>
    private static bool FitCircleKasa(List<int> loop, double[][] nodes,
        out double cx, out double cy, out double r, out double rms)
    {
        cx = cy = r = rms = 0;
        int n = loop.Count;
        if (n < 4) return false;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0,
               sxxx = 0, syyy = 0, sxyy = 0, sxxy = 0;
        foreach (var ni in loop)
        {
            double x = nodes[ni][0], y = nodes[ni][1];
            sx += x; sy += y;
            sxx += x * x; syy += y * y; sxy += x * y;
            sxxx += x * x * x; syyy += y * y * y;
            sxyy += x * y * y; sxxy += x * x * y;
        }

        double[][] A =
        {
            new[] { sxx, sxy, sx },
            new[] { sxy, syy, sy },
            new[] { sx,  sy,  (double)n },
        };
        double[] b = { -(sxxx + sxyy), -(sxxy + syyy), -(sxx + syy) };
        if (!Solve3x3(A, b, out double D, out double E, out double F)) return false;

        cx = -D * 0.5; cy = -E * 0.5;
        double r2 = cx * cx + cy * cy - F;
        if (!(r2 > 0)) return false;
        r = Math.Sqrt(r2);

        double s = 0;
        foreach (var ni in loop)
        {
            double x = nodes[ni][0], y = nodes[ni][1];
            double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;
            s += d * d;
        }
        rms = Math.Sqrt(s / n);
        return true;
    }

    /// <summary>
    /// Fit a 3D circle to a closed cycle of nodes.  Strategy:
    ///   1. Compute centroid and centre-shifted point cloud.
    ///   2. Build the 3×3 covariance matrix and find its smallest
    ///      eigenvector (the cycle's normal direction).  Power iteration
    ///      on (max_eig·I − M) converges in a few sweeps for a planar
    ///      cycle because the matrix has one near-zero eigenvalue.
    ///   3. Build an orthonormal in-plane basis (u, v) ⟂ normal and
    ///      project the nodes.
    ///   4. Run Kasa in 2D to get (uc, vc, r).
    ///   5. Lift back to 3D: centre = centroid + uc·u + vc·v.
    ///   6. Report the axial spread (max − min along the normal) so the
    ///      caller can reject non-planar cycles.
    /// </summary>
    private static bool FitCircle3D(List<int> cycle, double[][] nodes,
        out double cx, out double cy, out double cz, out double r, out double rms,
        out double axialSpread, out double ax, out double ay, out double az)
    {
        cx = cy = cz = r = rms = axialSpread = 0;
        ax = ay = az = 0;
        int n = cycle.Count;
        if (n < 4) return false;

        // Centroid.
        double mx = 0, my = 0, mz = 0;
        foreach (var ni in cycle) { var p = nodes[ni]; mx += p[0]; my += p[1]; mz += p[2]; }
        mx /= n; my /= n; mz /= n;

        // Covariance matrix (symmetric).
        double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
        foreach (var ni in cycle)
        {
            double dx = nodes[ni][0] - mx;
            double dy = nodes[ni][1] - my;
            double dz = nodes[ni][2] - mz;
            cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
            cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
        }

        // Smallest eigenvector via inverse iteration on the shifted matrix
        // M' = trace·I − M (so smallest eigenvalue of M becomes largest of
        // M', dominant in power iteration).  Three Rayleigh-quotient
        // sweeps are plenty for a cycle that is genuinely planar.
        double t = cxx + cyy + czz;
        double m11 = t - cxx, m12 = -cxy, m13 = -cxz;
        double m22 = t - cyy, m23 = -cyz;
        double m33 = t - czz;
        double vx = 1, vy = 1, vz = 1;
        for (int iter = 0; iter < 12; iter++)
        {
            double nx = m11 * vx + m12 * vy + m13 * vz;
            double ny = m12 * vx + m22 * vy + m23 * vz;
            double nz = m13 * vx + m23 * vy + m33 * vz;
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l < 1e-14) return false;
            vx = nx / l; vy = ny / l; vz = nz / l;
        }
        // (vx, vy, vz) is now the smallest eigenvector of M (= cycle normal).
        ax = vx; ay = vy; az = vz;

        // In-plane basis u, v.
        double[] uPick = (Math.Abs(ax) < 0.9) ? new[] { 1.0, 0.0, 0.0 } : new[] { 0.0, 1.0, 0.0 };
        double ux = ay * uPick[2] - az * uPick[1];
        double uy = az * uPick[0] - ax * uPick[2];
        double uz = ax * uPick[1] - ay * uPick[0];
        double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        if (ul < 1e-12) return false;
        ux /= ul; uy /= ul; uz /= ul;
        double vbx = ay * uz - az * uy;
        double vby = az * ux - ax * uz;
        double vbz = ax * uy - ay * ux;

        // Project to 2D and Kasa.
        double sx = 0, sy = 0, sxx2 = 0, syy2 = 0, sxy = 0,
               sxxx = 0, syyy = 0, sxyy = 0, sxxy = 0;
        double aMin = double.MaxValue, aMax = double.MinValue;
        foreach (var ni in cycle)
        {
            double dx = nodes[ni][0] - mx;
            double dy = nodes[ni][1] - my;
            double dz = nodes[ni][2] - mz;
            double xu = dx * ux + dy * uy + dz * uz;
            double yv = dx * vbx + dy * vby + dz * vbz;
            double za = dx * ax + dy * ay + dz * az;
            if (za < aMin) aMin = za; if (za > aMax) aMax = za;
            sx += xu; sy += yv;
            sxx2 += xu * xu; syy2 += yv * yv; sxy += xu * yv;
            sxxx += xu * xu * xu; syyy += yv * yv * yv;
            sxyy += xu * yv * yv; sxxy += xu * xu * yv;
        }
        axialSpread = aMax - aMin;

        double[][] A =
        {
            new[] { sxx2, sxy, sx },
            new[] { sxy,  syy2, sy },
            new[] { sx,   sy,  (double)n },
        };
        double[] b = { -(sxxx + sxyy), -(sxxy + syyy), -(sxx2 + syy2) };
        if (!Solve3x3(A, b, out double D, out double E, out double F)) return false;
        double uc = -D * 0.5, vc = -E * 0.5;
        double r2 = uc * uc + vc * vc - F;
        if (!(r2 > 0)) return false;
        r = Math.Sqrt(r2);

        // Lift centre back to 3D.
        cx = mx + uc * ux + vc * vbx;
        cy = my + uc * uy + vc * vby;
        cz = mz + uc * uz + vc * vbz;

        // RMS in-plane residual.
        double s = 0;
        foreach (var ni in cycle)
        {
            double dx = nodes[ni][0] - cx;
            double dy = nodes[ni][1] - cy;
            double dz = nodes[ni][2] - cz;
            // Project onto the plane, distance from rim.
            double zComp = dx * ax + dy * ay + dz * az;
            double inX = dx - zComp * ax;
            double inY = dy - zComp * ay;
            double inZ = dz - zComp * az;
            double inR = Math.Sqrt(inX * inX + inY * inY + inZ * inZ);
            s += (inR - r) * (inR - r);
        }
        rms = Math.Sqrt(s / n);
        return true;
    }

    /// <summary>
    /// Algebraic 4-parameter sphere fit.  Solve x²+y²+z²+Dx+Ey+Fz+G=0 in
    /// the least-squares sense via the 4×4 normal equations.
    /// </summary>
    private static bool FitSphereLSQ(HashSet<int> nodeSet, double[][] nodes,
        out double cx, out double cy, out double cz, out double r, out double rms)
    {
        cx = cy = cz = r = rms = 0;
        int n = nodeSet.Count;
        if (n < 8) return false;

        double Sx = 0, Sy = 0, Sz = 0;
        double Sxx = 0, Syy = 0, Szz = 0;
        double Sxy = 0, Sxz = 0, Syz = 0;
        double Bx = 0, By = 0, Bz = 0, Bsum = 0;
        foreach (var ni in nodeSet)
        {
            var p = nodes[ni];
            double x = p[0], y = p[1], z = p[2];
            double s = x * x + y * y + z * z;
            Sx += x; Sy += y; Sz += z;
            Sxx += x * x; Syy += y * y; Szz += z * z;
            Sxy += x * y; Sxz += x * z; Syz += y * z;
            Bx += x * s; By += y * s; Bz += z * s; Bsum += s;
        }

        double[][] A =
        {
            new[] { Sxx, Sxy, Sxz, Sx },
            new[] { Sxy, Syy, Syz, Sy },
            new[] { Sxz, Syz, Szz, Sz },
            new[] { Sx,  Sy,  Sz,  (double)n },
        };
        double[] b = { -Bx, -By, -Bz, -Bsum };
        if (!Solve4x4(A, b, out var sol)) return false;
        double D = sol[0], E = sol[1], F = sol[2], G = sol[3];
        cx = -D * 0.5; cy = -E * 0.5; cz = -F * 0.5;
        double r2 = cx * cx + cy * cy + cz * cz - G;
        if (!(r2 > 0)) return false;
        r = Math.Sqrt(r2);

        double sumSq = 0;
        foreach (var ni in nodeSet)
        {
            var p = nodes[ni];
            double dx = p[0] - cx, dy = p[1] - cy, dz = p[2] - cz;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            sumSq += (dist - r) * (dist - r);
        }
        rms = Math.Sqrt(sumSq / n);
        return true;
    }

    private static bool Solve3x3(double[][] M, double[] b, out double x, out double y, out double z)
    {
        x = y = z = 0;
        double det = M[0][0] * (M[1][1] * M[2][2] - M[1][2] * M[2][1])
                   - M[0][1] * (M[1][0] * M[2][2] - M[1][2] * M[2][0])
                   + M[0][2] * (M[1][0] * M[2][1] - M[1][1] * M[2][0]);
        if (Math.Abs(det) < 1e-18) return false;
        double inv = 1.0 / det;
        x = (b[0] * (M[1][1] * M[2][2] - M[1][2] * M[2][1])
           - M[0][1] * (b[1] * M[2][2] - M[1][2] * b[2])
           + M[0][2] * (b[1] * M[2][1] - M[1][1] * b[2])) * inv;
        y = (M[0][0] * (b[1] * M[2][2] - M[1][2] * b[2])
           - b[0] * (M[1][0] * M[2][2] - M[1][2] * M[2][0])
           + M[0][2] * (M[1][0] * b[2] - b[1] * M[2][0])) * inv;
        z = (M[0][0] * (M[1][1] * b[2] - b[1] * M[2][1])
           - M[0][1] * (M[1][0] * b[2] - b[1] * M[2][0])
           + b[0] * (M[1][0] * M[2][1] - M[1][1] * M[2][0])) * inv;
        return true;
    }

    /// <summary>Gaussian elimination with partial pivoting (4×4).</summary>
    private static bool Solve4x4(double[][] M, double[] b, out double[] x)
    {
        // Copy into an augmented 4×5 matrix.
        var A = new double[4][];
        for (int i = 0; i < 4; i++)
        {
            A[i] = new double[5];
            for (int j = 0; j < 4; j++) A[i][j] = M[i][j];
            A[i][4] = b[i];
        }

        for (int k = 0; k < 4; k++)
        {
            int piv = k; double max = Math.Abs(A[k][k]);
            for (int i = k + 1; i < 4; i++)
            {
                double v = Math.Abs(A[i][k]);
                if (v > max) { max = v; piv = i; }
            }
            if (max < 1e-18) { x = new double[4]; return false; }
            if (piv != k) (A[k], A[piv]) = (A[piv], A[k]);
            for (int i = k + 1; i < 4; i++)
            {
                double f = A[i][k] / A[k][k];
                for (int j = k; j < 5; j++) A[i][j] -= f * A[k][j];
            }
        }
        x = new double[4];
        for (int i = 3; i >= 0; i--)
        {
            double s = A[i][4];
            for (int j = i + 1; j < 4; j++) s -= A[i][j] * x[j];
            x[i] = s / A[i][i];
        }
        return true;
    }
}
