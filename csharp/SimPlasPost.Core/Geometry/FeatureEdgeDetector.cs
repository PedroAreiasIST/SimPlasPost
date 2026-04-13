namespace SimPlasPost.Core.Geometry;

/// <summary>
/// Detects feature edges based on dihedral angle between adjacent faces.
/// </summary>
public static class FeatureEdgeDetector
{
    /// <summary>
    /// Extract feature edges from boundary faces using a dihedral angle threshold.
    /// Returns flat array of positions: [ax, ay, az, bx, by, bz, ...] (6 doubles per edge).
    /// </summary>
    public static double[] Extract(List<int[]> bfaces, double[][] dp, double angleDeg = 20.0)
    {
        double cosThresh = Math.Cos(angleDeg * Math.PI / 180.0);

        // Compute face normals
        var fNormals = new Vec3[bfaces.Count];
        for (int fi = 0; fi < bfaces.Count; fi++)
        {
            var face = bfaces[fi];
            var p0 = new Vec3(dp[face[0]][0], dp[face[0]][1], dp[face[0]][2]);
            var p1 = new Vec3(dp[face[1]][0], dp[face[1]][1], dp[face[1]][2]);
            int i2 = face.Length > 2 ? 2 : 1;
            var p2 = new Vec3(dp[face[i2]][0], dp[face[i2]][1], dp[face[i2]][2]);

            var u = p1 - p0;
            var v = p2 - p0;
            var n = Vec3.Cross(u, v);
            double l = n.Length;
            fNormals[fi] = l > 1e-14 ? new Vec3(n.X / l, n.Y / l, n.Z / l) : new Vec3(0, 0, 1);
        }

        // Build edge -> face adjacency
        var edgeFaces = new Dictionary<string, List<int>>();
        for (int fi = 0; fi < bfaces.Count; fi++)
        {
            var face = bfaces[fi];
            for (int j = 0; j < face.Length; j++)
            {
                int a = face[j], b = face[(j + 1) % face.Length];
                string key = a < b ? $"{a},{b}" : $"{b},{a}";
                if (!edgeFaces.ContainsKey(key))
                    edgeFaces[key] = new List<int>();
                edgeFaces[key].Add(fi);
            }
        }

        // Keep boundary edges + sharp dihedral angle edges
        var result = new List<double>();
        foreach (var (key, fis) in edgeFaces)
        {
            bool keep = false;
            if (fis.Count == 1)
            {
                keep = true; // boundary edge
            }
            else if (fis.Count == 2)
            {
                var n1 = fNormals[fis[0]];
                var n2 = fNormals[fis[1]];
                double dot = Vec3.Dot(n1, n2);
                if (dot < cosThresh) keep = true;
            }

            if (keep)
            {
                var parts = key.Split(',');
                int ia = int.Parse(parts[0]), ib = int.Parse(parts[1]);
                var a = dp[ia];
                var b = dp[ib];
                result.Add(a[0]); result.Add(a[1]); result.Add(a[2]);
                result.Add(b[0]); result.Add(b[1]); result.Add(b[2]);
            }
        }

        return result.ToArray();
    }
}
