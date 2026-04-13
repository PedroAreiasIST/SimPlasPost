using System.Globalization;
using System.Text;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Export;

public static class EpsExporter
{
    public static string Export(ExportScene scene)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("%!PS-Adobe-3.0 EPSF-3.0");
        sb.AppendLine($"%%BoundingBox: 0 0 {scene.W} {scene.H}");
        sb.AppendLine("%%Title: FE Export");
        sb.AppendLine("%%Creator: SIMPLAS Viewer");
        sb.AppendLine("%%EndComments");
        sb.AppendLine("/CMFont { /CMB10 findfont } stopped { /Times-Bold findfont } if def");
        sb.AppendLine("CMFont 12.5 scalefont setfont");
        sb.AppendLine($"1 1 1 setrgbcolor 0 0 {scene.W} {scene.H} rectfill");

        // Faces
        foreach (var f in scene.Faces)
        {
            sb.AppendLine($"{f.R.ToString("F4", ci)} {f.G.ToString("F4", ci)} {f.B.ToString("F4", ci)} setrgbcolor");
            sb.AppendLine("newpath");
            for (int i = 0; i < f.ScreenPts.Length; i++)
            {
                double px = f.ScreenPts[i][0], py = scene.H - f.ScreenPts[i][1];
                sb.AppendLine($"{px.ToString("F2", ci)} {py.ToString("F2", ci)} {(i == 0 ? "moveto" : "lineto")}");
            }
            sb.AppendLine("closepath fill");
        }

        // Visible edges
        if (scene.VisibleEdges.Count > 0)
        {
            sb.AppendLine($"0.13 0.13 0.13 setrgbcolor {scene.Lp.SvgWidth.ToString("F2", ci)} setlinewidth 1 setlinecap");
            foreach (var e in scene.VisibleEdges)
            {
                sb.AppendLine($"newpath {e.P1[0].ToString("F2", ci)} {(scene.H - e.P1[1]).ToString("F2", ci)} moveto " +
                    $"{e.P2[0].ToString("F2", ci)} {(scene.H - e.P2[1]).ToString("F2", ci)} lineto stroke");
            }
        }

        // Contour lines
        if (scene.Contours.Count > 0)
        {
            sb.AppendLine("1 setlinewidth 1 setlinecap");
            foreach (var c in scene.Contours)
            {
                sb.AppendLine($"{c.R.ToString("F4", ci)} {c.G.ToString("F4", ci)} {c.B.ToString("F4", ci)} setrgbcolor");
                sb.AppendLine($"newpath {c.P1[0].ToString("F2", ci)} {(scene.H - c.P1[1]).ToString("F2", ci)} moveto " +
                    $"{c.P2[0].ToString("F2", ci)} {(scene.H - c.P2[1]).ToString("F2", ci)} lineto stroke");
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
                sb.AppendLine($"{cr.ToString("F4", ci)} {cg.ToString("F4", ci)} {cb.ToString("F4", ci)} setrgbcolor");
                sb.AppendLine($"{bx} {ry.ToString("F2", ci)} {bw} {(bh / (double)nSteps + 0.5).ToString("F2", ci)} rectfill");
            }

            sb.AppendLine("0.33 0.33 0.33 setrgbcolor 0.5 setlinewidth");
            sb.AppendLine($"{bx - 0.5} {byBot - 0.5} {bw + 1} {bh + 1} rectstroke");
            sb.AppendLine("0.2 0.2 0.2 setrgbcolor");
            sb.AppendLine("CMFont 12.5 scalefont setfont");

            for (int i = 0; i < nLabels; i++)
            {
                double t = i / (double)(nLabels - 1);
                double v = scene.FMin + t * (scene.FMax - scene.FMin);
                double ly = byBot + t * bh - 4;
                // FIX: escape PostScript special characters
                sb.AppendLine($"{bx - 5} {ly.ToString("F2", ci)} moveto ({v.ToString("E2", ci)}) dup stringwidth pop neg 0 rmoveto show");
            }

            sb.AppendLine("CMFont 14 scalefont setfont");
            string escapedName = ExportHelpers.EscapePs(scene.FieldName);
            sb.AppendLine($"gsave {bx + bw + 16} {byBot + bh / 2} translate 90 rotate");
            sb.AppendLine($"({escapedName}) dup stringwidth pop 2 div neg 0 moveto show");
            sb.AppendLine("grestore");
        }

        sb.AppendLine("%%EOF");
        return sb.ToString();
    }
}
