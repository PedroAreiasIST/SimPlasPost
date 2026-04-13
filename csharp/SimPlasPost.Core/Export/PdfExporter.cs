using System.Globalization;
using System.Text;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Export;

/// <summary>
/// Generates a minimal valid PDF 1.4 with vector graphics.
/// Uses painter's algorithm for face ordering and software z-buffer for hidden-line removal.
/// </summary>
public static class PdfExporter
{
    public static byte[] Export(ExportScene scene)
    {
        var ci = CultureInfo.InvariantCulture;
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
            {
                stream.AppendLine($"{F2(e.P1[0])} {F2(scene.H - e.P1[1])} m {F2(e.P2[0])} {F2(scene.H - e.P2[1])} l S");
            }
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

        // Color bar with labels
        if (!string.IsNullOrEmpty(scene.FieldName))
        {
            double bx = scene.W - 50;
            double bw = 18;
            double bh = Math.Min(220, scene.H - 60);
            double byBot = (scene.H - bh) / 2.0;
            int nSteps = 64, nLabels = 6;

            // Gradient strips
            for (int i = 0; i < nSteps; i++)
            {
                double t = i / (double)(nSteps - 1);
                var (cr, cg, cb) = TurboColormap.Sample(t);
                double ry = byBot + i * bh / nSteps;
                stream.AppendLine($"{F(cr)} {F(cg)} {F(cb)} rg");
                stream.AppendLine($"{F2(bx)} {F2(ry)} {bw} {F2(bh / nSteps + 0.5)} re f");
            }

            // Border
            stream.AppendLine("0.33 0.33 0.33 RG 0.5 w");
            stream.AppendLine($"{F2(bx - 0.5)} {F2(byBot - 0.5)} {bw + 1} {F2(bh + 1)} re S");

            // Numeric labels (right-aligned to the left of the bar)
            stream.AppendLine("0.2 0.2 0.2 rg");
            for (int i = 0; i < nLabels; i++)
            {
                double t = i / (double)(nLabels - 1);
                double v = scene.FMin + t * (scene.FMax - scene.FMin);
                double ly = byBot + t * bh;
                string label = Escape(v.ToString("E2", ci));
                // Horizontal text: Tm = [fontSize 0 0 fontSize tx ty]
                stream.AppendLine($"BT /F1 10 Tf 1 0 0 1 {F2(bx - 8)} {F2(ly - 3.5)} Tm ({label}) Tj ET");
            }

            // Field name label (rotated 90 degrees, to the right of the bar)
            string name = Escape(scene.FieldName);
            double tx = bx + bw + 14;
            double ty = byBot + bh / 2.0;
            // 90-deg CCW rotation matrix: [cos90 sin90 -sin90 cos90 tx ty] = [0 1 -1 0 tx ty]
            stream.AppendLine($"BT /F1 12 Tf 0 1 -1 0 {F2(tx)} {F2(ty)} Tm ({name}) Tj ET");
        }

        var content = stream.ToString();
        var contentBytes = Encoding.Latin1.GetBytes(content);

        // Build minimal valid PDF 1.4
        var pdfBytes = new MemoryStream();
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); pdfBytes.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");

        // Track object byte offsets
        var offsets = new List<long>();

        // Obj 1: Catalog
        offsets.Add(pdfBytes.Position);
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // Obj 2: Pages
        offsets.Add(pdfBytes.Position);
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Obj 3: Page
        offsets.Add(pdfBytes.Position);
        Write($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {scene.W} {scene.H}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        // Obj 4: Content stream
        offsets.Add(pdfBytes.Position);
        Write($"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
        pdfBytes.Write(contentBytes, 0, contentBytes.Length);
        Write("\nendstream\nendobj\n");

        // Obj 5: Font (Times-Bold for labels)
        offsets.Add(pdfBytes.Position);
        Write("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");

        // Cross-reference table
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
