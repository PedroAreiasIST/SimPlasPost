using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Rendering;

/// <summary>
/// Projects world-space dimensions to screen, offsets the dimension line
/// outward (away from the bounding-box centre) and places the value
/// label.  Tiny dimensions (under a few pixels long) are silently dropped
/// so the overlay never draws unreadable annotations.
/// </summary>
public static class DimensionLayout
{
    /// <summary>
    /// Project dimensions to screen and lay out their leader lines.
    /// </summary>
    /// <param name="bboxCenterWorld">Mesh centre in the same normalised
    /// frame as the input dimensions; used to choose which side of each
    /// dimension line the leader extends to.</param>
    /// <param name="offsetPx">Extension distance from the mesh edge, in
    /// pixels.  40 px gives the larger drafting-style labels enough
    /// breathing room above the silhouette without crowding the colour
    /// bar on the right.</param>
    public static List<DimensionScreen> Project(
        IList<DimensionWorld> dims, CameraState cam, double orthoHH,
        int w, int h, double[] bboxCenterWorld, double offsetPx = 40.0)
    {
        var result = new List<DimensionScreen>();
        if (dims == null || dims.Count == 0) return result;

        var sCenter = Camera.Project(bboxCenterWorld, cam, orthoHH, w, h);
        double cxScreen = sCenter[0], cyScreen = sCenter[1];

        foreach (var d in dims)
        {
            if (d.Kind == DimensionKind.Linear)
            {
                var sa = Camera.Project(d.P1, cam, orthoHH, w, h);
                var sb = Camera.Project(d.P2, cam, orthoHH, w, h);
                double dx = sb[0] - sa[0], dy = sb[1] - sa[1];
                double L = Math.Sqrt(dx * dx + dy * dy);
                if (L < 4) continue; // too foreshortened to be readable

                // Screen-space normal; flipped to point AWAY from the mesh
                // centre so the dimension line clears the silhouette.
                double nx = -dy / L, ny = dx / L;
                double mxs = (sa[0] + sb[0]) * 0.5, mys = (sa[1] + sb[1]) * 0.5;
                double sign = nx * (mxs - cxScreen) + ny * (mys - cyScreen);
                if (sign < 0) { nx = -nx; ny = -ny; }

                double[] dim1 = { sa[0] + nx * offsetPx, sa[1] + ny * offsetPx };
                double[] dim2 = { sb[0] + nx * offsetPx, sb[1] + ny * offsetPx };
                // Push the label out one cap-height above the dim line so
                // it never clips the heavier 1.6 px stroke of the dim line
                // itself; the value scales loosely with the new font size.
                double[] labelPos =
                {
                    (dim1[0] + dim2[0]) * 0.5 + nx * 14,
                    (dim1[1] + dim2[1]) * 0.5 + ny * 14,
                };
                double rot = Math.Atan2(dim2[1] - dim1[1], dim2[0] - dim1[0]) * 180.0 / Math.PI;
                if (rot > 90) rot -= 180;
                else if (rot < -90) rot += 180;

                result.Add(new DimensionScreen
                {
                    Kind = d.Kind, Label = d.Label, Value = d.Value,
                    Ext1 = new[] { sa[0], sa[1] }, Ext2 = new[] { sb[0], sb[1] },
                    Dim1 = dim1, Dim2 = dim2,
                    LabelPos = labelPos, Rot = rot,
                });
            }
            else if (d.Kind == DimensionKind.Diameter)
            {
                var sc = Camera.Project(d.P1, cam, orthoHH, w, h);
                var se = Camera.Project(d.P2, cam, orthoHH, w, h);
                double dx = se[0] - sc[0], dy = se[1] - sc[1];
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r < 6) continue;

                double[] a = { sc[0] - r, sc[1] };
                double[] b = { sc[0] + r, sc[1] };
                double[] labelPos = { sc[0] + r + 12, sc[1] - 10 };
                result.Add(new DimensionScreen
                {
                    Kind = d.Kind, Label = d.Label, Value = d.Value,
                    Ext1 = new[] { sc[0], sc[1] }, Ext2 = new[] { sc[0], sc[1] },
                    Dim1 = a, Dim2 = b,
                    LabelPos = labelPos, Rot = 0,
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Format a dimension value for display.  Uses a fixed-decimal style
    /// in the common range (0.01–999) and falls back to scientific notation
    /// at the extremes so the label never overflows a few characters.
    /// </summary>
    public static string FormatValue(double v)
    {
        double a = Math.Abs(v);
        if (a == 0) return "0";
        if (a >= 100) return v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        if (a >= 1)   return v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        if (a >= 0.01) return v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        return v.ToString("E2", System.Globalization.CultureInfo.InvariantCulture);
    }
}
