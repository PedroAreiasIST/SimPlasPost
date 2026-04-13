using System.Globalization;
using System.Text;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Export;

public static class SvgExporter
{
    public static string Export(ExportScene scene)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{scene.W}\" height=\"{scene.H}\" viewBox=\"0 0 {scene.W} {scene.H}\">");
        sb.AppendLine($"<rect width=\"{scene.W}\" height=\"{scene.H}\" fill=\"white\"/>");

        // Faces (filled polygons, painter's algorithm order)
        foreach (var f in scene.Faces)
        {
            var pts = string.Join(" ", f.ScreenPts.Select(p =>
                $"{p[0].ToString("F2", ci)},{p[1].ToString("F2", ci)}"));
            int r = (int)(f.R * 255), g = (int)(f.G * 255), b = (int)(f.B * 255);
            sb.AppendLine($"<polygon points=\"{pts}\" fill=\"rgb({r},{g},{b})\" stroke=\"none\"/>");
        }

        // Visible wireframe edges (hidden-line removed)
        if (scene.VisibleEdges.Count > 0)
        {
            sb.AppendLine($"<g stroke=\"#222\" stroke-width=\"{scene.Lp.SvgWidth.ToString("F2", ci)}\" stroke-linecap=\"round\" fill=\"none\">");
            foreach (var e in scene.VisibleEdges)
            {
                sb.AppendLine($"<line x1=\"{e.P1[0].ToString("F2", ci)}\" y1=\"{e.P1[1].ToString("F2", ci)}\" " +
                    $"x2=\"{e.P2[0].ToString("F2", ci)}\" y2=\"{e.P2[1].ToString("F2", ci)}\"/>");
            }
            sb.AppendLine("</g>");
        }

        // Contour lines
        if (scene.Contours.Count > 0)
        {
            foreach (var c in scene.Contours)
            {
                int r = (int)(c.R * 255), g = (int)(c.G * 255), b = (int)(c.B * 255);
                sb.AppendLine($"<line x1=\"{c.P1[0].ToString("F2", ci)}\" y1=\"{c.P1[1].ToString("F2", ci)}\" " +
                    $"x2=\"{c.P2[0].ToString("F2", ci)}\" y2=\"{c.P2[1].ToString("F2", ci)}\" " +
                    $"stroke=\"rgb({r},{g},{b})\" stroke-width=\"1\" stroke-linecap=\"round\"/>");
            }
        }

        // Color bar
        if (!string.IsNullOrEmpty(scene.FieldName))
        {
            int bx = scene.W - 40, bw = 18, bh = 220, nSteps = 64, nLabels = 6;
            int by = scene.H / 2 - 110;

            for (int i = 0; i < nSteps; i++)
            {
                double t = i / (double)(nSteps - 1);
                var (cr, cg, cb) = TurboColormap.Sample(t);
                double ry = by + bh - (i + 1) * bh / (double)nSteps;
                sb.AppendLine($"<rect x=\"{bx}\" y=\"{ry.ToString("F1", ci)}\" width=\"{bw}\" " +
                    $"height=\"{(bh / (double)nSteps + 0.5).ToString("F1", ci)}\" " +
                    $"fill=\"rgb({(int)(cr * 255)},{(int)(cg * 255)},{(int)(cb * 255)})\"/>");
            }

            sb.AppendLine($"<rect x=\"{bx - 0.5}\" y=\"{by - 0.5}\" width=\"{bw + 1}\" height=\"{bh + 1}\" fill=\"none\" stroke=\"#555\" stroke-width=\"0.5\"/>");

            for (int i = 0; i < nLabels; i++)
            {
                double t = i / (double)(nLabels - 1);
                double v = scene.FMax - t * (scene.FMax - scene.FMin);
                double ly = by + t * bh + 4.5;
                // FIX: properly quote font-family attribute + escape field text
                sb.AppendLine($"<text x=\"{bx - 5}\" y=\"{ly.ToString("F1", ci)}\" " +
                    $"font-family=\"{ExportHelpers.CmFontFamily}\" font-size=\"12.5\" font-weight=\"bold\" " +
                    $"fill=\"#333\" text-anchor=\"end\">{v.ToString("E2", ci)}</text>");
            }

            string escapedName = ExportHelpers.EscapeXml(scene.FieldName);
            sb.AppendLine($"<text x=\"{bx + bw + 16}\" y=\"{by + bh / 2}\" " +
                $"font-family=\"{ExportHelpers.CmFontFamily}\" font-size=\"14\" font-weight=\"bold\" " +
                $"fill=\"#333\" text-anchor=\"middle\" " +
                $"transform=\"rotate(90,{bx + bw + 16},{by + bh / 2})\">{escapedName}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
