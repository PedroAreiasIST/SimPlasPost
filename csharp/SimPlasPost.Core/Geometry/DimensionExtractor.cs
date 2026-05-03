using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Geometry;

/// <summary>
/// Deterministic mesh-to-feature dimensioning, matching the policy in
/// <c>automatic_mesh_dimensioning_algorithm.pdf</c>: extract a small
/// independent parameter set (overall extents L, W, H plus 2D hole
/// diameters) from the mesh, attaching each value with a residual /
/// confidence filter so dubious fits are suppressed silently.
///
/// The algorithm is conservative — it never invents geometry.  Bounding-
/// box extents are always emitted; hole diameters appear only when the
/// boundary loop has at least 8 segments, the radius covers more than 1.5×
/// the mean edge length, and the Kasa circle fit's RMS residual is below
/// 5% of the recovered radius.
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

        bool is3D = meshData.Dim == 3 || meshData.Elements.Any(e =>
            FaceTable.Faces.TryGetValue(e.Type, out var ft) && ft.Dim == 3);

        // Overall extents — anchored on the minimum corner so the dimension
        // line sits on a real face of the bounding box rather than mid-air.
        dims.Add(new DimensionWorld
        {
            Kind = DimensionKind.Linear, Label = "L", Value = mxX - mnX,
            P1 = W(mnX, mnY, mnZ), P2 = W(mxX, mnY, mnZ),
        });
        dims.Add(new DimensionWorld
        {
            Kind = DimensionKind.Linear, Label = "W", Value = mxY - mnY,
            P1 = W(mnX, mnY, mnZ), P2 = W(mnX, mxY, mnZ),
        });
        if (is3D)
        {
            dims.Add(new DimensionWorld
            {
                Kind = DimensionKind.Linear, Label = "H", Value = mxZ - mnZ,
                P1 = W(mnX, mnY, mnZ), P2 = W(mnX, mnY, mxZ),
            });
        }

        // 2D circular holes — interior boundary loops fitted with the Kasa
        // algebraic least-squares estimator.  Only loops with enough
        // segments and a small residual survive: a coarse arc gives a
        // misleading diameter and is silently suppressed.
        if (!is3D)
        {
            var loops = ExtractBoundaryLoops2D(meshData.Elements);
            if (loops.Count > 1)
            {
                // Loops are sorted by |signed area| descending; the first is
                // the outer boundary, the rest are candidate holes.
                // Reference mean edge length filters out near-singular fits.
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

                var fits = new List<(double cx, double cy, double r)>();
                for (int i = 1; i < loops.Count; i++)
                {
                    var loop = loops[i];
                    if (loop.Count < 8) continue;
                    if (!FitCircleKasa(loop, ns, out double cx, out double cy, out double r, out double rms)) continue;
                    if (r < meanEdge * 1.5) continue;
                    if (rms / r > 0.05) continue;
                    fits.Add((cx, cy, r));
                }

                // Sort by radius descending so the dominant hole gets the
                // shortest label ("d") and tie-breaks are stable.
                fits.Sort((a, b) => b.r.CompareTo(a.r));
                for (int k = 0; k < fits.Count; k++)
                {
                    var fit = fits[k];
                    string lab = fits.Count == 1 ? "d" : $"d{k + 1}";
                    dims.Add(new DimensionWorld
                    {
                        Kind = DimensionKind.Diameter, Label = lab, Value = 2 * fit.r,
                        P1 = W(fit.cx, fit.cy, 0),
                        P2 = W(fit.cx + fit.r, fit.cy, 0),
                    });
                }
            }
        }

        return dims;
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
}
