namespace SimPlasPost.Core.Rendering;

/// <summary>
/// Software rasterizer with per-vertex color interpolation (smooth shading)
/// and support for supersampled rendering for anti-aliasing.
/// </summary>
public static class BitmapRenderer
{
    private const float PolygonOffset = 0.005f;

    /// <summary>
    /// Render faces with per-vertex color interpolation (Gouraud shading).
    /// Colors are smoothly interpolated across each triangle.
    /// </summary>
    public static void RenderFaces(
        uint[] pixels, float[] zbuf, int w, int h,
        float[] sx, float[] sy, float[] sz,
        int[] faceOffsets, int[] faceVertices,
        byte[] vertR, byte[] vertG, byte[] vertB) // PER-VERTEX colors
    {
        Array.Fill(pixels, 0xFFFFFFFFu);
        Array.Fill(zbuf, float.MaxValue);

        int nFaces = faceOffsets.Length - 1;
        for (int fi = 0; fi < nFaces; fi++)
        {
            int start = faceOffsets[fi], end = faceOffsets[fi + 1];
            int nv = end - start;
            if (nv < 3) continue;

            int v0 = faceVertices[start];
            for (int t = 1; t < nv - 1; t++)
            {
                int v1 = faceVertices[start + t], v2 = faceVertices[start + t + 1];
                RasterTriSmooth(pixels, zbuf, w, h,
                    sx[v0], sy[v0], sz[v0] + PolygonOffset, vertR[v0], vertG[v0], vertB[v0],
                    sx[v1], sy[v1], sz[v1] + PolygonOffset, vertR[v1], vertG[v1], vertB[v1],
                    sx[v2], sy[v2], sz[v2] + PolygonOffset, vertR[v2], vertG[v2], vertB[v2]);
            }
        }
    }

    public static void RenderEdges(
        uint[] pixels, int w, int h,
        float[] sx, float[] sy, float[] sz,
        int[] faceOffsets, int[] faceVertices, bool[] drawEdges,
        uint edgeColor, float[] zbuf)
    {
        int nFaces = faceOffsets.Length - 1;
        for (int fi = 0; fi < nFaces; fi++)
        {
            if (!drawEdges[fi]) continue;
            int start = faceOffsets[fi], end = faceOffsets[fi + 1];
            int nv = end - start;
            if (nv < 2) continue;

            for (int j = 0; j < nv; j++)
            {
                int a = faceVertices[start + j], b = faceVertices[start + (j + 1) % nv];
                float ax = sx[a], ay = sy[a], bx = sx[b], by = sy[b];
                if ((ax < -2 && bx < -2) || (ax > w + 2 && bx > w + 2)) continue;
                if ((ay < -2 && by < -2) || (ay > h + 2 && by > h + 2)) continue;
                DrawLine(pixels, zbuf, w, h, ax, ay, sz[a], bx, by, sz[b], edgeColor);
            }
        }
    }

    /// <summary>Downsample a 2x supersampled buffer to the target size (box filter).</summary>
    public static void Downsample2x(uint[] src, int srcW, int srcH, uint[] dst, int dstW, int dstH)
    {
        for (int y = 0; y < dstH; y++)
        {
            int sy = y * 2;
            for (int x = 0; x < dstW; x++)
            {
                int sx2 = x * 2;
                uint c00 = src[sy * srcW + sx2];
                uint c10 = src[sy * srcW + sx2 + 1];
                uint c01 = src[(sy + 1) * srcW + sx2];
                uint c11 = src[(sy + 1) * srcW + sx2 + 1];

                uint r = ((c00 >> 16 & 0xFF) + (c10 >> 16 & 0xFF) + (c01 >> 16 & 0xFF) + (c11 >> 16 & 0xFF)) >> 2;
                uint g = ((c00 >> 8 & 0xFF) + (c10 >> 8 & 0xFF) + (c01 >> 8 & 0xFF) + (c11 >> 8 & 0xFF)) >> 2;
                uint b = ((c00 & 0xFF) + (c10 & 0xFF) + (c01 & 0xFF) + (c11 & 0xFF)) >> 2;
                dst[y * dstW + x] = 0xFF000000u | (r << 16) | (g << 8) | b;
            }
        }
    }

    /// <summary>Scanline rasterizer with per-vertex color interpolation.</summary>
    private static void RasterTriSmooth(uint[] px, float[] zb, int w, int h,
        float ax, float ay, float az, byte ar, byte ag, byte ab,
        float bx, float by, float bz, byte br, byte bg, byte bb,
        float cx, float cy, float cz, byte cr, byte cg, byte cb)
    {
        // Sort by Y, keeping colors with vertices
        if (ay > by)
        {
            (ax, bx) = (bx, ax); (ay, by) = (by, ay); (az, bz) = (bz, az);
            (ar, br) = (br, ar); (ag, bg) = (bg, ag); (ab, bb) = (bb, ab);
        }
        if (ay > cy)
        {
            (ax, cx) = (cx, ax); (ay, cy) = (cy, ay); (az, cz) = (cz, az);
            (ar, cr) = (cr, ar); (ag, cg) = (cg, ag); (ab, cb) = (cb, ab);
        }
        if (by > cy)
        {
            (bx, cx) = (cx, bx); (by, cy) = (cy, by); (bz, cz) = (cz, bz);
            (br, cr) = (cr, br); (bg, cg) = (cg, bg); (bb, cb) = (cb, bb);
        }

        float dyAC = cy - ay;
        if (dyAC < 0.5f) return;
        float invAC = 1f / dyAC;

        int yStart = Math.Max(0, (int)Math.Ceiling(ay));
        int yEnd = Math.Min(h - 1, (int)cy);

        for (int y = yStart; y <= yEnd; y++)
        {
            float tac = (y - ay) * invAC;
            // Long edge A→C
            float x1 = ax + tac * (cx - ax), z1 = az + tac * (cz - az);
            float r1 = ar + tac * (cr - ar), g1 = ag + tac * (cg - ag), b1 = ab + tac * (cb - ab);

            float x2, z2, r2, g2, b2;
            if (y < by)
            {
                float d = by - ay; if (d < 0.5f) continue;
                float t = (y - ay) / d;
                x2 = ax + t * (bx - ax); z2 = az + t * (bz - az);
                r2 = ar + t * (br - ar); g2 = ag + t * (bg - ag); b2 = ab + t * (bb - ab);
            }
            else
            {
                float d = cy - by; if (d < 0.5f) continue;
                float t = (y - by) / d;
                x2 = bx + t * (cx - bx); z2 = bz + t * (cz - bz);
                r2 = br + t * (cr - br); g2 = bg + t * (cg - bg); b2 = bb + t * (cb - bb);
            }

            if (x1 > x2)
            {
                (x1, x2) = (x2, x1); (z1, z2) = (z2, z1);
                (r1, r2) = (r2, r1); (g1, g2) = (g2, g1); (b1, b2) = (b2, b1);
            }

            int sxi = Math.Max(0, (int)Math.Ceiling(x1)), exi = Math.Min(w - 1, (int)x2);
            float dx = x2 - x1;
            float invDx = dx > 0.5f ? 1f / dx : 0f;
            int row = y * w;

            for (int x = sxi; x <= exi; x++)
            {
                float t = (x - x1) * invDx;
                float z = z1 + t * (z2 - z1);
                int idx = row + x;
                if (z < zb[idx])
                {
                    zb[idx] = z;
                    uint ri = (uint)Math.Clamp(r1 + t * (r2 - r1), 0, 255);
                    uint gi = (uint)Math.Clamp(g1 + t * (g2 - g1), 0, 255);
                    uint bi = (uint)Math.Clamp(b1 + t * (b2 - b1), 0, 255);
                    px[idx] = 0xFF000000u | (ri << 16) | (gi << 8) | bi;
                }
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
                // Only draw on pixels where a face exists (zbuf was written)
                if (z <= zb[idx] && zb[idx] < 1e9f) px[idx] = col;
            }
            int e2 = 2 * err;
            if (e2 > -dyi) { err -= dyi; x0 += sxi; }
            if (e2 < dxi) { err += dxi; y0 += syi; }
        }
    }
}
