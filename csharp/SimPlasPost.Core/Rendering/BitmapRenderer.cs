namespace SimPlasPost.Core.Rendering;

/// <summary>
/// Software rasterizer with polygon offset for clean wireframe rendering.
/// Faces are pushed back slightly in z so edges on front faces always win
/// the depth test, while back-face edges remain occluded.
/// </summary>
public static class BitmapRenderer
{
    // Polygon offset: faces are stored at z + offset in the zbuffer.
    // Edges use true z, so they pass on their own face but not on far faces.
    private const float PolygonOffset = 0.005f;

    public static void RenderFaces(
        uint[] pixels, float[] zbuf, int w, int h,
        float[] sx, float[] sy, float[] sz,
        int[] faceOffsets, int[] faceVertices,
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
                RasterTri(pixels, zbuf, w, h,
                    x0, y0, z0 + PolygonOffset,
                    sx[v1], sy[v1], sz[v1] + PolygonOffset,
                    sx[v2], sy[v2], sz[v2] + PolygonOffset, color);
            }
        }
    }

    /// <summary>
    /// Render wireframe edges with strict z-test (no offset).
    /// Thanks to polygon offset on faces, edges pass on their own face
    /// but are occluded by faces in front.
    /// </summary>
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
                int a = faceVertices[start + j];
                int b = faceVertices[start + (j + 1) % nv];
                DrawLine(pixels, zbuf, w, h, sx[a], sy[a], sz[a], sx[b], sy[b], sz[b], edgeColor);
            }
        }
    }

    private static void RasterTri(uint[] px, float[] zb, int w, int h,
        float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz, uint col)
    {
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
                if (z <= zb[idx]) px[idx] = col; // strict: edge at true z vs face at z+offset
            }
            int e2 = 2 * err;
            if (e2 > -dyi) { err -= dyi; x0 += sxi; }
            if (e2 < dxi) { err += dxi; y0 += syi; }
        }
    }
}
