using System.Globalization;
using System.Text;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Export;

/// <summary>
/// Generates a minimal valid PDF 1.4 with vector graphics.
/// No clipping — the mesh is auto-fitted to the page with margins.
/// Color bar labels are positioned clear of the gradient strip.
/// </summary>
public static class PdfExporter
{
    // Approximate Times-Bold character width at 1pt (average for E2 scientific notation)
    private const double AvgCharWidth = 0.55;

    public static byte[] Export(ExportScene scene)
    {
        var stream = new StringBuilder();

        // White background
        stream.AppendLine($"1 1 1 rg 0 0 {scene.W} {scene.H} re f");

        // Faces: build a single Type 4 (free-form Gouraud-shaded triangle
        // mesh) shading object so the PDF reader interpolates colours
        // across each triangle just like the on-screen renderer.  Faces are
        // already painter's-sorted; we fan-triangulate each in stream
        // order, so later triangles overdraw earlier ones inside the
        // shading itself.  The `sh /Sh1` operator below paints the whole
        // mesh at this point in the content stream — anything stroked
        // afterwards (edges, contours, bars, points, color bar, triad)
        // sits on top.
        var shadingBytes = BuildShadingStream(scene, out double xMin, out double xMax, out double yMin, out double yMax);
        bool hasShading = shadingBytes.Length > 0;
        if (hasShading)
            stream.AppendLine("/Sh1 sh");

        // Visible edges
        if (scene.VisibleEdges.Count > 0)
        {
            stream.AppendLine($"0.13 0.13 0.13 RG {F2(scene.Lp.SvgWidth)} w 1 j 1 J");
            foreach (var e in scene.VisibleEdges)
                stream.AppendLine($"{F2(e.P1[0])} {F2(scene.H - e.P1[1])} m {F2(e.P2[0])} {F2(scene.H - e.P2[1])} l S");
        }

        // Contour lines
        if (scene.Contours.Count > 0)
        {
            stream.AppendLine("1 w 1 J");
            foreach (var c in scene.Contours)
            {
                stream.AppendLine($"{F(c.R)} {F(c.G)} {F(c.B)} RG");
                stream.AppendLine($"{F2(c.P1[0])} {F2(scene.H - c.P1[1])} m {F2(c.P2[0])} {F2(scene.H - c.P2[1])} l S");
            }
        }

        // Bar2 elements: stand-alone line segments, drawn slightly thicker
        // than the silhouette/wire edges so they read as structural bars.
        if (scene.Bars.Count > 0)
        {
            stream.AppendLine("1.4 w 1 j 1 J");
            foreach (var b in scene.Bars)
            {
                stream.AppendLine($"{F(b.R)} {F(b.G)} {F(b.B)} RG");
                stream.AppendLine($"{F2(b.P1[0])} {F2(scene.H - b.P1[1])} m {F2(b.P2[0])} {F2(scene.H - b.P2[1])} l S");
            }
        }

        // Point1 elements: filled circles approximated with a 4-cubic-Bezier
        // path (PDF has no native circle).  k = 0.5522847498 is the standard
        // arc magic number for matching a circle with cubic Beziers.
        if (scene.Points.Count > 0)
        {
            const double R = 3.0;             // circle radius in PDF user units
            const double K = 0.5522847498 * R;
            foreach (var p in scene.Points)
            {
                double cx = p.P[0], cy = scene.H - p.P[1];
                stream.AppendLine($"{F(p.R)} {F(p.G)} {F(p.B)} rg");
                stream.AppendLine($"{F2(cx + R)} {F2(cy)} m");
                stream.AppendLine($"{F2(cx + R)} {F2(cy + K)} {F2(cx + K)} {F2(cy + R)} {F2(cx)} {F2(cy + R)} c");
                stream.AppendLine($"{F2(cx - K)} {F2(cy + R)} {F2(cx - R)} {F2(cy + K)} {F2(cx - R)} {F2(cy)} c");
                stream.AppendLine($"{F2(cx - R)} {F2(cy - K)} {F2(cx - K)} {F2(cy - R)} {F2(cx)} {F2(cy - R)} c");
                stream.AppendLine($"{F2(cx + K)} {F2(cy - R)} {F2(cx + R)} {F2(cy - K)} {F2(cx + R)} {F2(cy)} c");
                stream.AppendLine("h f");
            }
        }

        // Color bar with labels — labels LEFT of bar, field name RIGHT of bar
        if (!string.IsNullOrEmpty(scene.FieldName))
        {
            double fontSize = 14;
            double bw = 22;
            double bh = Math.Min(260, scene.H - 100);
            double byBot = (scene.H - bh) / 2.0;
            int nSteps = 64, nLabels = 6;

            // Estimate widest label width (e.g. "-1.23E+000" = 10 chars)
            double maxLabelW = 10 * AvgCharWidth * fontSize;

            // Layout: [margin] [labels] [gap] [bar] [gap] [fieldName] [margin]
            double rightMargin = 24;
            double barRight = scene.W - rightMargin - 24; // room for rotated field name
            double barX = barRight - bw;
            double labelRight = barX - 8; // 8pt gap between labels and bar

            // Gradient strips
            for (int i = 0; i < nSteps; i++)
            {
                double t = i / (double)(nSteps - 1);
                var (cr, cg, cb) = TurboColormap.Sample(t);
                double ry = byBot + i * bh / nSteps;
                stream.AppendLine($"{F(cr)} {F(cg)} {F(cb)} rg");
                stream.AppendLine($"{F2(barX)} {F2(ry)} {bw} {F2(bh / nSteps + 0.5)} re f");
            }

            // Border
            stream.AppendLine("0.33 0.33 0.33 RG 0.5 w");
            stream.AppendLine($"{F2(barX - 0.5)} {F2(byBot - 0.5)} {F2(bw + 1)} {F2(bh + 1)} re S");

            // Numeric labels — right-aligned to labelRight
            stream.AppendLine("0.2 0.2 0.2 rg");
            for (int i = 0; i < nLabels; i++)
            {
                double t = i / (double)(nLabels - 1);
                double v = scene.FMin + t * (scene.FMax - scene.FMin);
                double ly = byBot + t * bh - fontSize * 0.35;
                string label = Escape(v.ToString("E2", CultureInfo.InvariantCulture));
                double labelW = label.Length * AvgCharWidth * fontSize;
                double lx = labelRight - labelW;
                stream.AppendLine($"BT /F1 {fontSize} Tf 1 0 0 1 {F2(lx)} {F2(ly)} Tm ({label}) Tj ET");
            }

            // Field name — rotated 90 CCW, to the right of the bar
            string name = Escape(scene.FieldName);
            double fnSize = 16;
            double tx = barRight + 20;
            double ty = byBot + bh / 2.0;
            // Rotation matrix for 90 CCW: [0 1 -1 0 tx ty]
            stream.AppendLine($"BT /F1 {fnSize} Tf 0 1 -1 0 {F2(tx)} {F2(ty)} Tm ({name}) Tj ET");
        }

        // Axis triad (bottom-left corner, PDF coordinates: origin at bottom-left)
        DrawTriad(stream, scene.Rotation, 60, 60, 48);

        var content = stream.ToString();
        var contentBytes = Encoding.Latin1.GetBytes(content);

        // Build minimal valid PDF 1.4
        var pdfBytes = new MemoryStream();
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); pdfBytes.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        var offsets = new List<long>();

        offsets.Add(pdfBytes.Position);
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets.Add(pdfBytes.Position);
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets.Add(pdfBytes.Position);
        // /Resources picks up /Font and (when there are filled faces)
        // /Shading so the page's `sh /Sh1` operator can resolve.
        string shadingResource = hasShading ? " /Shading << /Sh1 6 0 R >>" : "";
        Write($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {scene.W} {scene.H}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >>{shadingResource} >> >>\nendobj\n");

        offsets.Add(pdfBytes.Position);
        Write($"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
        pdfBytes.Write(contentBytes, 0, contentBytes.Length);
        Write("\nendstream\nendobj\n");

        offsets.Add(pdfBytes.Position);
        Write("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");

        if (hasShading)
        {
            offsets.Add(pdfBytes.Position);
            string decode = $"[{F2(xMin)} {F2(xMax)} {F2(yMin)} {F2(yMax)} 0 1 0 1 0 1]";
            Write($"6 0 obj\n<< /ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 16 /BitsPerComponent 8 /BitsPerFlag 8 /Decode {decode} /Length {shadingBytes.Length} >>\nstream\n");
            pdfBytes.Write(shadingBytes, 0, shadingBytes.Length);
            Write("\nendstream\nendobj\n");
        }

        long xrefOff = pdfBytes.Position;
        Write($"xref\n0 {offsets.Count + 1}\n");
        Write("0000000000 65535 f \n");
        foreach (var off in offsets)
            Write($"{off:D10} 00000 n \n");

        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOff}\n%%EOF");

        return pdfBytes.ToArray();
    }

    /// <summary>Draw an axis triad at (cx, cy) in PDF coordinates.</summary>
    private static void DrawTriad(StringBuilder s, double[] rot, double cx, double cy, double axLen)
    {
        var axes = new (double dx, double dy, string r, string g, string b, string label)[]
        {
            // X axis (1,0,0): screen dx = rot[0]*axLen, screen dy = rot[3]*axLen (up in PDF = +Y)
            (rot[0] * axLen,  rot[3] * axLen, "0.86", "0.16", "0.16", "X1"),
            // Y axis (0,1,0)
            (rot[1] * axLen,  rot[4] * axLen, "0.16", "0.67", "0.16", "X2"),
            // Z axis (0,0,1)
            (rot[2] * axLen,  rot[5] * axLen, "0.16", "0.31", "0.86", "X3"),
        };

        foreach (var (dx, dy, cr, cg, cb, label) in axes)
        {
            double tipX = cx + dx, tipY = cy + dy;
            // Arrow shaft
            s.AppendLine($"{cr} {cg} {cb} RG 2.5 w 1 J");
            s.AppendLine($"{F2(cx)} {F2(cy)} m {F2(tipX)} {F2(tipY)} l S");

            // Arrowhead (filled triangle)
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 3)
            {
                double ux = dx / len, uy = dy / len;
                double px = -uy, py = ux;
                double hs = 8;
                double h1x = tipX - ux * hs + px * hs * 0.35;
                double h1y = tipY - uy * hs + py * hs * 0.35;
                double h2x = tipX - ux * hs - px * hs * 0.35;
                double h2y = tipY - uy * hs - py * hs * 0.35;
                s.AppendLine($"{cr} {cg} {cb} rg");
                s.AppendLine($"{F2(tipX)} {F2(tipY)} m {F2(h1x)} {F2(h1y)} l {F2(h2x)} {F2(h2y)} l h f");
            }

            // Label
            double lx = tipX + (len > 3 ? dx / len * 12 : 6);
            double ly = tipY + (len > 3 ? dy / len * 12 : 6) - 4.5;
            s.AppendLine($"BT /F1 13 Tf {cr} {cg} {cb} rg 1 0 0 1 {F2(lx)} {F2(ly)} Tm ({Escape(label)}) Tj ET");
        }
    }

    private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string Escape(string s) => ExportHelpers.EscapePs(s);

    /// <summary>
    /// Build the binary data stream for a PDF Type 4 (free-form Gouraud-shaded
    /// triangle mesh) shading.  Each face is fan-triangulated; for every
    /// triangle we emit three independent vertices (flag = 0), each carrying
    /// quantised x/y (16 bits) and r/g/b (8 bits).
    ///
    /// The output coordinate range is the scene's bounding rectangle in PDF
    /// user-space (origin bottom-left), which the caller writes into the
    /// shading dictionary's Decode array.
    /// </summary>
    private static byte[] BuildShadingStream(
        ExportScene scene,
        out double xMin, out double xMax,
        out double yMin, out double yMax)
    {
        xMin = 0; xMax = scene.W;
        yMin = 0; yMax = scene.H;
        if (scene.Faces.Count == 0) return Array.Empty<byte>();

        // Pre-count triangles for buffer sizing.
        int triCount = 0;
        foreach (var face in scene.Faces)
        {
            int n = face.ScreenPts.Length;
            if (n >= 3) triCount += n - 2;
        }
        if (triCount == 0) return Array.Empty<byte>();

        // 1 flag + 2 bytes x + 2 bytes y + 3 bytes rgb = 8 bytes per vertex.
        const int VertexBytes = 8;
        var buf = new byte[triCount * 3 * VertexBytes];
        int p = 0;

        double xSpan = xMax - xMin; if (xSpan < 1e-12) xSpan = 1;
        double ySpan = yMax - yMin; if (ySpan < 1e-12) ySpan = 1;

        void Vertex(double x, double y, double r, double g, double b)
        {
            // PDF y is bottom-up; our screen y is top-down.
            double pdfY = scene.H - y;
            ushort xq = (ushort)Math.Round(Math.Clamp((x   - xMin) / xSpan, 0, 1) * 65535);
            ushort yq = (ushort)Math.Round(Math.Clamp((pdfY - yMin) / ySpan, 0, 1) * 65535);
            byte rq = (byte)Math.Round(Math.Clamp(r, 0, 1) * 255);
            byte gq = (byte)Math.Round(Math.Clamp(g, 0, 1) * 255);
            byte bq = (byte)Math.Round(Math.Clamp(b, 0, 1) * 255);
            buf[p++] = 0;                      // flag = independent triangle vertex
            buf[p++] = (byte)(xq >> 8); buf[p++] = (byte)(xq & 0xFF);
            buf[p++] = (byte)(yq >> 8); buf[p++] = (byte)(yq & 0xFF);
            buf[p++] = rq; buf[p++] = gq; buf[p++] = bq;
        }

        foreach (var face in scene.Faces)
        {
            int n = face.ScreenPts.Length;
            if (n < 3) continue;
            // If R/G/B arrays are short (e.g. legacy data), fall back to the
            // first entry so the stream is still well-formed.
            int cn = face.R.Length;
            for (int i = 1; i < n - 1; i++)
            {
                int i0 = 0, i1 = i, i2 = i + 1;
                int c0 = Math.Min(i0, cn - 1);
                int c1 = Math.Min(i1, cn - 1);
                int c2 = Math.Min(i2, cn - 1);
                Vertex(face.ScreenPts[i0][0], face.ScreenPts[i0][1], face.R[c0], face.G[c0], face.B[c0]);
                Vertex(face.ScreenPts[i1][0], face.ScreenPts[i1][1], face.R[c1], face.G[c1], face.B[c1]);
                Vertex(face.ScreenPts[i2][0], face.ScreenPts[i2][1], face.R[c2], face.G[c2], face.B[c2]);
            }
        }

        // Trim to the actually-written length (Math.Clamp may not have
        // affected count, but stay defensive).
        if (p == buf.Length) return buf;
        var trimmed = new byte[p];
        Buffer.BlockCopy(buf, 0, trimmed, 0, p);
        return trimmed;
    }
}
