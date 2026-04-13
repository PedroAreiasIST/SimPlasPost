namespace SimPlasPost.Core.Rendering;

/// <summary>
/// Software Z-buffer for hidden-line removal in vector exports.
/// </summary>
public static class ZBufferRenderer
{
    /// <summary>
    /// Rasterize a single triangle into the z-buffer (depth only, no color).
    /// </summary>
    public static void RasterTri(float[] zbuf, int w, int h, double[] p0, double[] p1, double[] p2)
    {
        // Sort by Y coordinate
        double[] a = p0, b = p1, c = p2;
        if (a[1] > b[1]) (a, b) = (b, a);
        if (a[1] > c[1]) (a, c) = (c, a);
        if (b[1] > c[1]) (b, c) = (c, b);

        int yStart = Math.Max(0, (int)Math.Ceiling(a[1]));
        int yEnd = Math.Min(h - 1, (int)Math.Floor(c[1]));

        for (int y = yStart; y <= yEnd; y++)
        {
            // Long edge a->c
            double tac = (c[1] - a[1]) > 0.001 ? (y - a[1]) / (c[1] - a[1]) : 0;
            double x1 = a[0] + tac * (c[0] - a[0]);
            double z1 = a[2] + tac * (c[2] - a[2]);

            double x2, z2;
            if (y < b[1])
            {
                if (Math.Abs(b[1] - a[1]) < 0.001) continue;
                double t = (y - a[1]) / (b[1] - a[1]);
                x2 = a[0] + t * (b[0] - a[0]);
                z2 = a[2] + t * (b[2] - a[2]);
            }
            else
            {
                if (Math.Abs(c[1] - b[1]) < 0.001) continue;
                double t = (y - b[1]) / (c[1] - b[1]);
                x2 = b[0] + t * (c[0] - b[0]);
                z2 = b[2] + t * (c[2] - b[2]);
            }

            if (x1 > x2)
            {
                (x1, x2) = (x2, x1);
                (z1, z2) = (z2, z1);
            }

            int sx = Math.Max(0, (int)Math.Ceiling(x1));
            int ex = Math.Min(w - 1, (int)Math.Floor(x2));
            double dx = x2 - x1;

            for (int x = sx; x <= ex; x++)
            {
                double t = dx > 0.001 ? (x - x1) / dx : 0;
                float z = (float)(z1 + t * (z2 - z1));
                int idx = y * w + x;
                if (z < zbuf[idx]) zbuf[idx] = z;
            }
        }
    }

    /// <summary>
    /// Build a z-buffer from projected faces.
    /// </summary>
    public static float[] Build(IReadOnlyList<Models.ProjectedFace> faces, int w, int h)
    {
        var zbuf = new float[w * h];
        Array.Fill(zbuf, float.MaxValue);

        foreach (var f in faces)
        {
            var p = f.Pts3D;
            for (int i = 1; i < p.Length - 1; i++)
                RasterTri(zbuf, w, h, p[0], p[i], p[i + 1]);
        }

        return zbuf;
    }

    /// <summary>
    /// Test if a line segment is visible (not occluded by the z-buffer).
    /// Samples 5 points along the segment; visible if majority pass the z-test.
    /// </summary>
    public static bool IsSegmentVisible(double[] a, double[] b, float[] zbuf, int w, int h)
    {
        const int N = 5;
        int vis = 0;
        for (int i = 0; i < N; i++)
        {
            double t = (i + 0.5) / N;
            int x = (int)Math.Round(a[0] + t * (b[0] - a[0]));
            int y = (int)Math.Round(a[1] + t * (b[1] - a[1]));
            double z = a[2] + t * (b[2] - a[2]);
            if (x < 0 || x >= w || y < 0 || y >= h) continue;
            if (z <= zbuf[y * w + x] + 0.015) vis++;
        }
        return vis >= (N + 1) / 2;
    }
}
