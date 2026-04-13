namespace SimPlasPost.Core.Rendering;

/// <summary>
/// High-performance software rasterizer: renders triangles with per-vertex color
/// and z-buffering directly to an ARGB pixel buffer. No GC allocations per frame.
/// Handles 300K+ triangles at interactive rates.
/// </summary>
public static class BitmapRenderer
{
    /// <summary>
    /// Render projected faces to a pixel buffer with z-buffering.
    /// pixels: uint[w*h] ARGB, zbuf: float[w*h] (caller-allocated, reused across frames).
    /// </summary>
    public static void RenderFaces(
        uint[] pixels, float[] zbuf, int w, int h,
        double[][] screenPts, // [vertexIndex] => [sx, sy, sz]
        int[] faceOffsets,     // face i uses vertices faceOffsets[i]..faceOffsets[i+1]-1
        int[] faceVertices,    // packed vertex indices into screenPts
        byte[] faceR, byte[] faceG, byte[] faceB) // per-face color
    {
        // Clear
        Array.Fill(pixels, 0xFFFFFFFFu); // white background
        Array.Fill(zbuf, float.MaxValue);

        int nFaces = faceOffsets.Length - 1;
        for (int fi = 0; fi < nFaces; fi++)
        {
            int start = faceOffsets[fi], end = faceOffsets[fi + 1];
            int nv = end - start;
            if (nv < 3) continue;

            byte cr = faceR[fi], cg = faceG[fi], cb = faceB[fi];
            uint color = 0xFF000000u | ((uint)cr << 16) | ((uint)cg << 8) | cb;

            // Fan triangulate and rasterize
            int v0 = faceVertices[start];
            var p0 = screenPts[v0];
            for (int t = 1; t < nv - 1; t++)
            {
                int v1 = faceVertices[start + t], v2 = faceVertices[start + t + 1];
                var p1 = screenPts[v1]; var p2 = screenPts[v2];
                RasterTriZ(pixels, zbuf, w, h, p0, p1, p2, color);
            }
        }
    }

    /// <summary>
    /// Render wireframe edges on top of the pixel buffer (no z-test, just overwrite).
    /// </summary>
    public static void RenderEdges(
        uint[] pixels, int w, int h,
        double[][] screenPts,
        int[] edgePairs, // packed [v0, v1, v0, v1, ...]
        uint edgeColor, float[] zbuf, double zBias)
    {
        for (int i = 0; i < edgePairs.Length; i += 2)
        {
            var a = screenPts[edgePairs[i]];
            var b = screenPts[edgePairs[i + 1]];
            DrawLineZ(pixels, zbuf, w, h, a, b, edgeColor, zBias);
        }
    }

    /// <summary>Scanline rasterizer: fill triangle with flat color + z-test.</summary>
    private static void RasterTriZ(uint[] pixels, float[] zbuf, int w, int h,
        double[] p0, double[] p1, double[] p2, uint color)
    {
        // Sort by Y
        double[] a = p0, b = p1, c = p2;
        if (a[1] > b[1]) (a, b) = (b, a);
        if (a[1] > c[1]) (a, c) = (c, a);
        if (b[1] > c[1]) (b, c) = (c, b);

        int yStart = Math.Max(0, (int)Math.Ceiling(a[1]));
        int yEnd = Math.Min(h - 1, (int)c[1]);

        double dyAC = c[1] - a[1];
        if (dyAC < 0.001) return;
        double invAC = 1.0 / dyAC;

        for (int y = yStart; y <= yEnd; y++)
        {
            double tac = (y - a[1]) * invAC;
            double x1 = a[0] + tac * (c[0] - a[0]);
            double z1 = a[2] + tac * (c[2] - a[2]);

            double x2, z2;
            if (y < b[1])
            {
                double dyAB = b[1] - a[1];
                if (dyAB < 0.001) continue;
                double t = (y - a[1]) / dyAB;
                x2 = a[0] + t * (b[0] - a[0]);
                z2 = a[2] + t * (b[2] - a[2]);
            }
            else
            {
                double dyBC = c[1] - b[1];
                if (dyBC < 0.001) continue;
                double t = (y - b[1]) / dyBC;
                x2 = b[0] + t * (c[0] - b[0]);
                z2 = b[2] + t * (c[2] - b[2]);
            }

            if (x1 > x2) { (x1, x2) = (x2, x1); (z1, z2) = (z2, z1); }

            int sx = Math.Max(0, (int)Math.Ceiling(x1));
            int ex = Math.Min(w - 1, (int)x2);
            double dx = x2 - x1;
            double invDx = dx > 0.001 ? 1.0 / dx : 0;

            int rowOff = y * w;
            for (int x = sx; x <= ex; x++)
            {
                float z = (float)(z1 + (x - x1) * invDx * (z2 - z1));
                int idx = rowOff + x;
                if (z < zbuf[idx])
                {
                    zbuf[idx] = z;
                    pixels[idx] = color;
                }
            }
        }
    }

    /// <summary>Bresenham line with z-test (for wireframe edges).</summary>
    private static void DrawLineZ(uint[] pixels, float[] zbuf, int w, int h,
        double[] a, double[] b, uint color, double zBias)
    {
        int x0 = (int)Math.Round(a[0]), y0 = (int)Math.Round(a[1]);
        int x1 = (int)Math.Round(b[0]), y1 = (int)Math.Round(b[1]);
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int steps = dx + dy;
        if (steps == 0) return;
        double za = a[2] - zBias, zb = b[2] - zBias;

        for (int s = 0; s <= steps; s++)
        {
            if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h)
            {
                float z = (float)(za + (zb - za) * s / (double)steps);
                int idx = y0 * w + x0;
                if (z <= zbuf[idx] + 0.01f)
                    pixels[idx] = color;
            }
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }
}
