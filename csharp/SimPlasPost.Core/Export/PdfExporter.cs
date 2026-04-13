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

        // Faces (painter's algorithm order)
        foreach (var f in scene.Faces)
        {
            stream.AppendLine($"{F(f.R)} {F(f.G)} {F(f.B)} rg");
            for (int i = 0; i < f.ScreenPts.Length; i++)
            {
                double px = f.ScreenPts[i][0], py = scene.H - f.ScreenPts[i][1];
                stream.AppendLine($"{F2(px)} {F2(py)} {(i == 0 ? "m" : "l")}");
            }
            stream.AppendLine("h f");
        }

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

        // Color bar with labels — labels LEFT of bar, field name RIGHT of bar
        if (!string.IsNullOrEmpty(scene.FieldName))
        {
            double fontSize = 9;
            double bw = 16;
            double bh = Math.Min(220, scene.H - 80);
            double byBot = (scene.H - bh) / 2.0;
            int nSteps = 64, nLabels = 6;

            // Estimate widest label width (e.g. "-1.23E+000" = 10 chars)
            double maxLabelW = 10 * AvgCharWidth * fontSize;

            // Layout: [margin] [labels] [gap] [bar] [gap] [fieldName] [margin]
            double rightMargin = 20;
            double barRight = scene.W - rightMargin - 16; // room for rotated field name
            double barX = barRight - bw;
            double labelRight = barX - 6; // 6pt gap between labels and bar

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
            double fnSize = 10;
            double tx = barRight + 14;
            double ty = byBot + bh / 2.0;
            // Rotation matrix for 90 CCW: [0 1 -1 0 tx ty]
            stream.AppendLine($"BT /F1 {fnSize} Tf 0 1 -1 0 {F2(tx)} {F2(ty)} Tm ({name}) Tj ET");
        }

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
        Write($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {scene.W} {scene.H}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        offsets.Add(pdfBytes.Position);
        Write($"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
        pdfBytes.Write(contentBytes, 0, contentBytes.Length);
        Write("\nendstream\nendobj\n");

        offsets.Add(pdfBytes.Position);
        Write("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");

        long xrefOff = pdfBytes.Position;
        Write($"xref\n0 {offsets.Count + 1}\n");
        Write("0000000000 65535 f \n");
        foreach (var off in offsets)
            Write($"{off:D10} 00000 n \n");

        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOff}\n%%EOF");

        return pdfBytes.ToArray();
    }

    private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string Escape(string s) => ExportHelpers.EscapePs(s);
}
