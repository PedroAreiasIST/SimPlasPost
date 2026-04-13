using System.Globalization;
using System.Text;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Export;

public static class PdfExporter
{
    public static byte[] Export(ExportScene scene)
    {
        var ci = CultureInfo.InvariantCulture;
        var stream = new StringBuilder();

        stream.AppendLine($"1 1 1 rg 0 0 {scene.W} {scene.H} re f");

        // Faces
        foreach (var f in scene.Faces)
        {
            stream.AppendLine($"{f.R.ToString("F4", ci)} {f.G.ToString("F4", ci)} {f.B.ToString("F4", ci)} rg");
            for (int i = 0; i < f.ScreenPts.Length; i++)
            {
                double px = f.ScreenPts[i][0], py = scene.H - f.ScreenPts[i][1];
                stream.AppendLine($"{px.ToString("F2", ci)} {py.ToString("F2", ci)} {(i == 0 ? "m" : "l")}");
            }
            stream.AppendLine("h f");
        }

        // Visible edges
        if (scene.VisibleEdges.Count > 0)
        {
            stream.AppendLine($"0.13 0.13 0.13 RG {scene.Lp.SvgWidth.ToString("F2", ci)} w 1 j 1 J");
            foreach (var e in scene.VisibleEdges)
            {
                stream.AppendLine($"{e.P1[0].ToString("F2", ci)} {(scene.H - e.P1[1]).ToString("F2", ci)} m " +
                    $"{e.P2[0].ToString("F2", ci)} {(scene.H - e.P2[1]).ToString("F2", ci)} l S");
            }
        }

        // Contour lines
        if (scene.Contours.Count > 0)
        {
            stream.AppendLine("1 w 1 J");
            foreach (var c in scene.Contours)
            {
                stream.AppendLine($"{c.R.ToString("F4", ci)} {c.G.ToString("F4", ci)} {c.B.ToString("F4", ci)} RG");
                stream.AppendLine($"{c.P1[0].ToString("F2", ci)} {(scene.H - c.P1[1]).ToString("F2", ci)} m " +
                    $"{c.P2[0].ToString("F2", ci)} {(scene.H - c.P2[1]).ToString("F2", ci)} l S");
            }
        }

        // Color bar
        if (!string.IsNullOrEmpty(scene.FieldName))
        {
            int bx = scene.W - 40, bw = 18, bh = 220, nSteps = 64, nLabels = 6;
            int byBot = 24;

            for (int i = 0; i < nSteps; i++)
            {
                double t = i / (double)(nSteps - 1);
                var (cr, cg, cb) = TurboColormap.Sample(t);
                double ry = byBot + i * bh / (double)nSteps;
                stream.AppendLine($"{cr.ToString("F4", ci)} {cg.ToString("F4", ci)} {cb.ToString("F4", ci)} rg");
                stream.AppendLine($"{bx} {ry.ToString("F2", ci)} {bw} {(bh / (double)nSteps + 0.5).ToString("F2", ci)} re f");
            }

            stream.AppendLine("0.33 0.33 0.33 RG 0 0 0 rg 0.5 w");
            stream.AppendLine($"{bx - 0.5} {byBot - 0.5} {bw + 1} {bh + 1} re S");
            stream.AppendLine("0.2 0.2 0.2 rg");

            for (int i = 0; i < nLabels; i++)
            {
                double t = i / (double)(nLabels - 1);
                double v = scene.FMin + t * (scene.FMax - scene.FMin);
                double ly = byBot + t * bh - 4;
                string escaped = ExportHelpers.EscapePs(v.ToString("E2", ci));
                stream.AppendLine($"BT /F1 12.5 Tf {bx - 5} {ly.ToString("F2", ci)} Td ({escaped}) Tj ET");
            }

            // FIX: correct 90-degree rotation matrix for PDF text
            // The original JS used "90 0 0 90 0 0 Tm" which is a 90x scale, not rotation.
            // Correct 90-deg rotation: cos(90)=0, sin(90)=1 → matrix [0 1 -1 0 tx ty]
            string escapedName = ExportHelpers.EscapePs(scene.FieldName);
            double tx = bx + bw + 16, ty = byBot + bh / 2.0;
            stream.AppendLine($"BT /F1 14 Tf {tx.ToString("F2", ci)} {ty.ToString("F2", ci)} Td 0 1 -1 0 {tx.ToString("F2", ci)} {ty.ToString("F2", ci)} Tm ({escapedName}) Tj ET");
        }

        var content = stream.ToString();
        var contentBytes = Encoding.UTF8.GetBytes(content);

        // Build minimal valid PDF 1.4
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        var objs = new string[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj",
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {scene.W} {scene.H}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj",
            $"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream\nendobj",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold >>\nendobj",
        };

        var offsets = new int[objs.Length];
        for (int i = 0; i < objs.Length; i++)
        {
            offsets[i] = Encoding.UTF8.GetByteCount(pdf.ToString());
            pdf.Append(objs[i] + "\n");
        }

        int xrefOff = Encoding.UTF8.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objs.Length + 1}\n");
        pdf.Append("0000000000 65535 f \n");
        foreach (int off in offsets)
            pdf.Append($"{off:D10} 00000 n \n");

        pdf.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\n");
        pdf.Append($"startxref\n{xrefOff}\n%%EOF");

        return Encoding.UTF8.GetBytes(pdf.ToString());
    }
}
