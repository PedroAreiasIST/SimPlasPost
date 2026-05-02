namespace SimPlasPost.Core.Geometry;

/// <summary>
/// Marching-triangles contour extraction and Chaikin curve smoothing.
/// </summary>
public static class ContourGenerator
{
    /// <summary>A contour segment: two 3D endpoints, the iso-level value, and a perpendicular vector.</summary>
    public record ContourSegment(double[] A, double[] B, double Level, double[] Perp);

    /// <summary>
    /// Compute contour segments by intersecting iso-levels with triangulated boundary faces.
    /// </summary>
    public static List<ContourSegment> ComputeSegments(
        List<int[]> bfaces, double[][] dp, double[]? fv,
        double fmin, double fmax, int nLevels)
    {
        var segs = new List<ContourSegment>();
        if (fv == null || nLevels < 1) return segs;

        var levels = new double[nLevels];
        for (int i = 0; i < nLevels; i++)
            levels[i] = fmin + (i + 1) * (fmax - fmin) / (nLevels + 1);

        foreach (var face in bfaces)
        {
            var tris = BoundaryExtractor.TriangulateFace(face);
            foreach (var tri in tris)
            {
                int n0 = tri[0], n1 = tri[1], n2 = tri[2];
                double f0 = fv[n0], f1 = fv[n1], f2 = fv[n2];
                var p0 = dp[n0]; var p1 = dp[n1]; var p2 = dp[n2];

                // Face normal
                double e1x = p1[0] - p0[0], e1y = p1[1] - p0[1], e1z = p1[2] - p0[2];
                double e2x = p2[0] - p0[0], e2y = p2[1] - p0[1], e2z = p2[2] - p0[2];
                double fnx = e1y * e2z - e1z * e2y;
                double fny = e1z * e2x - e1x * e2z;
                double fnz = e1x * e2y - e1y * e2x;

                foreach (double lv in levels)
                {
                    var crossings = new List<double[]>();
                    var edges = new (double fa, double fb, double[] pa, double[] pb)[]
                    {
                        (f0, f1, p0, p1),
                        (f1, f2, p1, p2),
                        (f2, f0, p2, p0),
                    };

                    foreach (var (fa, fb, pa, pb) in edges)
                    {
                        if ((fa - lv) * (fb - lv) < 0)
                        {
                            double t = (lv - fa) / (fb - fa);
                            crossings.Add(new[]
                            {
                                pa[0] + t * (pb[0] - pa[0]),
                                pa[1] + t * (pb[1] - pa[1]),
                                pa[2] + t * (pb[2] - pa[2]),
                            });
                        }
                    }

                    if (crossings.Count == 2)
                    {
                        var c0 = crossings[0]; var c1 = crossings[1];
                        double sdx = c1[0] - c0[0], sdy = c1[1] - c0[1], sdz = c1[2] - c0[2];
                        double px = sdy * fnz - sdz * fny;
                        double py = sdz * fnx - sdx * fnz;
                        double pz = sdx * fny - sdy * fnx;
                        double pl = Math.Sqrt(px * px + py * py + pz * pz);
                        var perp = pl > 1e-14
                            ? new[] { px / pl, py / pl, pz / pl }
                            : new[] { 0.0, 0.0, 1.0 };
                        segs.Add(new ContourSegment(c0, c1, lv, perp));
                    }
                }
            }
        }

        return segs;
    }

    /// <summary>One smoothed iso-line: a polyline at a single level.</summary>
    public record ContourPolyline(double Level, List<double[]> Points);

    /// <summary>
    /// Chain segments into polylines and apply Chaikin subdivision for
    /// smoothing.  Returns one polyline per connected component, grouped
    /// per level.  Used by the contour-label placer (which needs to know
    /// polyline length and tangent direction at the midpoint).
    /// </summary>
    public static List<ContourPolyline> SmoothPolylines(List<ContourSegment> segs, int nSubdiv)
    {
        var result = new List<ContourPolyline>();
        if (segs.Count == 0) return result;

        var byLevel = new Dictionary<string, List<ContourSegment>>();
        foreach (var s in segs)
        {
            string k = s.Level.ToString("F8");
            if (!byLevel.ContainsKey(k))
                byLevel[k] = new List<ContourSegment>();
            byLevel[k].Add(s);
        }

        foreach (var (_, lvSegs) in byLevel)
        {
            double lv = lvSegs[0].Level;

            var adj = new Dictionary<string, List<(int segIdx, int endIdx)>>();
            for (int i = 0; i < lvSegs.Count; i++)
            {
                string ka = HashPt(lvSegs[i].A);
                string kb = HashPt(lvSegs[i].B);
                if (!adj.ContainsKey(ka)) adj[ka] = new();
                if (!adj.ContainsKey(kb)) adj[kb] = new();
                adj[ka].Add((i, 0));
                adj[kb].Add((i, 1));
            }

            var used = new HashSet<int>();
            for (int si = 0; si < lvSegs.Count; si++)
            {
                if (used.Contains(si)) continue;
                used.Add(si);
                var chain = new List<double[]> { lvSegs[si].A, lvSegs[si].B };

                for (int safe = 0; safe < 100000; safe++)
                {
                    string k = HashPt(chain[^1]);
                    if (!adj.TryGetValue(k, out var nb)) break;
                    bool found = false;
                    foreach (var (ni, ne) in nb)
                    {
                        if (used.Contains(ni)) continue;
                        used.Add(ni);
                        chain.Add(ne == 0 ? lvSegs[ni].B : lvSegs[ni].A);
                        found = true;
                        break;
                    }
                    if (!found) break;
                }

                for (int safe = 0; safe < 100000; safe++)
                {
                    string k = HashPt(chain[0]);
                    if (!adj.TryGetValue(k, out var nb)) break;
                    bool found = false;
                    foreach (var (ni, ne) in nb)
                    {
                        if (used.Contains(ni)) continue;
                        used.Add(ni);
                        chain.Insert(0, ne == 0 ? lvSegs[ni].B : lvSegs[ni].A);
                        found = true;
                        break;
                    }
                    if (!found) break;
                }

                var pts = chain;
                for (int iter = 0; iter < nSubdiv; iter++)
                {
                    if (pts.Count < 3) break;
                    var np = new List<double[]> { pts[0] };
                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        var a = pts[i]; var b = pts[i + 1];
                        np.Add(new[] { a[0] * 0.75 + b[0] * 0.25, a[1] * 0.75 + b[1] * 0.25, a[2] * 0.75 + b[2] * 0.25 });
                        np.Add(new[] { a[0] * 0.25 + b[0] * 0.75, a[1] * 0.25 + b[1] * 0.75, a[2] * 0.25 + b[2] * 0.75 });
                    }
                    np.Add(pts[^1]);
                    pts = np;
                }

                result.Add(new ContourPolyline(lv, pts));
            }
        }

        return result;
    }

    /// <summary>
    /// Convenience wrapper: smooth + emit each polyline back as consecutive
    /// segments, matching the original Smooth contract.
    /// </summary>
    public static List<ContourSegment> Smooth(List<ContourSegment> segs, int nSubdiv)
    {
        var perp = new[] { 0.0, 0.0, 1.0 };
        var result = new List<ContourSegment>();
        foreach (var pl in SmoothPolylines(segs, nSubdiv))
        {
            for (int i = 0; i < pl.Points.Count - 1; i++)
                result.Add(new ContourSegment(pl.Points[i], pl.Points[i + 1], pl.Level, perp));
        }
        return result;
    }

    private static string HashPt(double[] p) =>
        $"{(int)(p[0] * 1e5)},{(int)(p[1] * 1e5)},{(int)(p[2] * 1e5)}";
}
