using SimPlasPost.Core.Geometry;
using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Rendering;

/// <summary>
/// Pick the most informative viewing direction for a mesh by maximising
/// Vázquez's viewpoint entropy: the Shannon entropy of the projected-area
/// distribution across visible boundary faces.  Intuitively a high-entropy
/// view spreads the silhouette across many face orientations — the angle
/// that lets the viewer see the most surface variety at once, which
/// empirically maps well to "the most informative view" of an arbitrary
/// part.
///
/// References:
///   Vázquez, P-P. et al. "Viewpoint Selection using Viewpoint Entropy."
///   VMV 2001.
///
/// Implementation:
///   1. Pull boundary faces and triangulate them via fan; accumulate a
///      per-face area + outward unit normal.
///   2. Sample the unit sphere with a Fibonacci spiral (uniform coverage,
///      no clustering at the poles); 96 candidates by default.
///   3. For each candidate forward direction F, compute the projected
///      visible area a_i = max(0, -n_i · F) · A_i for every face, then
///      H = -Σ p_i · log p_i with p_i = a_i / Σ a_i.
///   4. Return the F with the highest H (with a tiny log(total area) tie-
///      breaker to penalise edge-on views that have similar entropy but
///      almost zero silhouette).
///
/// All math runs on the CPU using the same boundary-extraction pass the
/// renderer uses, so the cost is independent of viewport resolution and a
/// 50K-face mesh resolves in well under a frame on a modern laptop.
/// </summary>
public static class BestViewFinder
{
    /// <summary>
    /// Find the unit forward direction (camera-look-axis) that maximises
    /// viewpoint entropy on <paramref name="mesh"/>.  Falls back to
    /// (0,0,-1) if the mesh has no boundary faces (e.g. point cloud).
    /// </summary>
    public static double[] FindForward(MeshData mesh, int samples = 96)
    {
        bool is3D = mesh.Dim == 3 || mesh.Elements.Any(e =>
            FaceTable.Faces.TryGetValue(e.Type, out var ft) && ft.Dim == 3);
        var bfaces = BoundaryExtractor.Extract(mesh.Elements, is3D);

        // Collect (unit normal, area) for every triangulated face.
        var ns = mesh.Nodes;
        var nx = new List<double>(bfaces.Count);
        var ny = new List<double>(bfaces.Count);
        var nz = new List<double>(bfaces.Count);
        var areas = new List<double>(bfaces.Count);
        foreach (var f in bfaces)
        {
            if (f.Length < 3) continue;
            // Cross-product sum across a fan triangulation = twice the area
            // vector of the polygon.  Magnitude is 2A; direction is the
            // outward normal (inherits the boundary extractor's winding).
            double sx = 0, sy = 0, sz = 0;
            for (int k = 1; k < f.Length - 1; k++)
            {
                var p0 = ns[f[0]]; var p1 = ns[f[k]]; var p2 = ns[f[k + 1]];
                double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2];
                double vx = p2[0] - p0[0], vy = p2[1] - p0[1], vz = p2[2] - p0[2];
                sx += uy * vz - uz * vy;
                sy += uz * vx - ux * vz;
                sz += ux * vy - uy * vx;
            }
            double mag = Math.Sqrt(sx * sx + sy * sy + sz * sz);
            if (mag < 1e-14) continue;
            nx.Add(sx / mag); ny.Add(sy / mag); nz.Add(sz / mag);
            areas.Add(mag * 0.5);
        }

        int F = areas.Count;
        if (F == 0) return new[] { 0.0, 0.0, -1.0 };

        // Reuse one buffer for the per-face projected area across iterations.
        var proj = new double[F];

        // Fibonacci-spiral sphere sampling: φ_inc = π·(3-√5) is the golden
        // angle, which guarantees no clustering of the i-th sample with any
        // earlier one.  Together with y_i = 1 - (i+½)·2/N we get a
        // near-uniform tiling of the unit sphere in N points.
        double phiInc = Math.PI * (3.0 - Math.Sqrt(5.0));

        double bestScore = double.NegativeInfinity;
        double bestFx = 0, bestFy = 0, bestFz = -1;

        for (int i = 0; i < samples; i++)
        {
            double y = 1.0 - (i + 0.5) / samples * 2.0;
            double r = Math.Sqrt(Math.Max(0, 1.0 - y * y));
            double theta = phiInc * i;
            double fx = r * Math.Cos(theta);
            double fy = y;
            double fz = r * Math.Sin(theta);

            double total = 0;
            for (int k = 0; k < F; k++)
            {
                // Front-facing test: outward normal must oppose the view
                // direction.  Eye looks along F (cam.Forward), so a face
                // is visible when n · (-F) > 0 ↔ -(n · F) > 0.
                double dot = -(nx[k] * fx + ny[k] * fy + nz[k] * fz);
                if (dot > 0) { proj[k] = dot * areas[k]; total += proj[k]; }
                else proj[k] = 0;
            }
            if (total < 1e-14) continue;

            double inv = 1.0 / total;
            double entropy = 0;
            for (int k = 0; k < F; k++)
            {
                double p = proj[k] * inv;
                if (p > 1e-12) entropy -= p * Math.Log(p);
            }

            // log(total) tie-break: among views with similar entropy, prefer
            // the one with the largest visible silhouette so an edge-on or
            // grazing direction loses to a broadside.  Coefficient is small
            // so it never overrides a clear entropy winner.
            double score = entropy + 0.05 * Math.Log(total);
            if (score > bestScore)
            {
                bestScore = score;
                bestFx = fx; bestFy = fy; bestFz = fz;
            }
        }

        return new[] { bestFx, bestFy, bestFz };
    }
}
