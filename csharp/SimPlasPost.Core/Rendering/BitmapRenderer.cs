namespace SimPlasPost.Core.Rendering;

/// <summary>
/// High-performance software rasterizer: flat float arrays for cache-friendly access,
/// scanline triangle fill with z-buffer, Bresenham lines with depth test.
/// </summary>
public static class BitmapRenderer
{
    public static void RenderFaces(
        uint[] pixels, float[] zbuf, int w, int h,
        float[] sx, float[] sy, float[] sz,   // per-vertex screen coords
        int[] faceOffsets, int[] faceVertices,  // packed face topology
        byte[] faceR, byte[] faceG, byte[] faceB)
    {
        Array.Fill(pixels, 0xFFFFFFFFu);
        Array.Fill(zbuf, float.MaxValue);

        int nFaces = faceOffsets.Length - 1;
        for (int fi = 0; fi < nFaces; fi++)
        {
            int start = faceOffsets[fi], end = faceOffsets[fi + 1];
            int nv = end - start;
            if (nv < 3) continue;

            uint color = 0xFF000000u | ((uint)faceR[fi] << 16) | ((uint)faceG[fi] << 8) | faceB[fi];
            int v0 = faceVertices[start];
            float x0 = sx[v0], y0 = sy[v0], z0 = sz[v0];

            for (int t = 1; t < nv - 1; t++)
            {
                int v1 = faceVertices[start + t], v2 = faceVertices[start + t + 1];
                RasterTri(pixels, zbuf, w, h, x0, y0, z0, sx[v1], sy[v1], sz[v1], sx[v2], sy[v2], sz[v2], color);
            }
        }
    }

    public static void RenderEdges(
        uint[] pixels, int w, int h,
        float[] sx, float[] sy, float[] sz,
        int[] edgePairs, uint edgeColor, float[] zbuf, float zBias)
    {
        for (int i = 0; i < edgePairs.Length; i += 2)
        {
            int a = edgePairs[i], b = edgePairs[i + 1];
            DrawLine(pixels, zbuf, w, h, sx[a], sy[a], sz[a] - zBias, sx[b], sy[b], sz[b] - zBias, edgeColor);
        }
    }

    private static void RasterTri(uint[] px, float[] zb, int w, int h,
        float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz, uint col)
    {
        // Sort by Y
        if (ay > by) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); (az, bz) = (bz, az); }
        if (ay > cy) { (ax, cx) = (cx, ax); (ay, cy) = (cy, ay); (az, cz) = (cz, az); }
        if (by > cy) { (bx, cx) = (cx, bx); (by, cy) = (cy, by); (bz, cz) = (cz, bz); }

        float dyAC = cy - ay;
        if (dyAC < 0.5f) return;
        float invAC = 1f / dyAC;

        int yStart = Math.Max(0, (int)Math.Ceiling(ay));
        int yEnd = Math.Min(h - 1, (int)cy);

        for (int y = yStart; y <= yEnd; y++)
        {
            float tac = (y - ay) * invAC;
            float x1 = ax + tac * (cx - ax), z1 = az + tac * (cz - az);
            float x2, z2;

            if (y < by)
            {
                float d = by - ay; if (d < 0.5f) continue;
                float t = (y - ay) / d; x2 = ax + t * (bx - ax); z2 = az + t * (bz - az);
            }
            else
            {
                float d = cy - by; if (d < 0.5f) continue;
                float t = (y - by) / d; x2 = bx + t * (cx - bx); z2 = bz + t * (cz - bz);
            }

            if (x1 > x2) { (x1, x2) = (x2, x1); (z1, z2) = (z2, z1); }
            int sxi = Math.Max(0, (int)Math.Ceiling(x1)), exi = Math.Min(w - 1, (int)x2);
            float dx = x2 - x1;
            float invDx = dx > 0.5f ? 1f / dx : 0f;
            int row = y * w;

            for (int x = sxi; x <= exi; x++)
            {
                float z = z1 + (x - x1) * invDx * (z2 - z1);
                int idx = row + x;
                if (z < zb[idx]) { zb[idx] = z; px[idx] = col; }
            }
        }
    }

    private static void DrawLine(uint[] px, float[] zb, int w, int h,
        float ax, float ay, float az, float bx, float by, float bz, uint col)
    {
        int x0 = (int)ax, y0 = (int)ay, x1 = (int)bx, y1 = (int)by;
        int dxi = Math.Abs(x1 - x0), dyi = Math.Abs(y1 - y0);
        int sxi = x0 < x1 ? 1 : -1, syi = y0 < y1 ? 1 : -1;
        int err = dxi - dyi, steps = dxi + dyi;
        if (steps == 0) return;
        float invS = 1f / steps;

        for (int s = 0; s <= steps; s++)
        {
            if ((uint)x0 < (uint)w && (uint)y0 < (uint)h)
            {
                float z = az + (bz - az) * s * invS;
                int idx = y0 * w + x0;
                if (z <= zb[idx] + 0.01f) px[idx] = col;
            }
            int e2 = 2 * err;
            if (e2 > -dyi) { err -= dyi; x0 += sxi; }
            if (e2 < dxi) { err += dxi; y0 += syi; }
        }
    }
}
