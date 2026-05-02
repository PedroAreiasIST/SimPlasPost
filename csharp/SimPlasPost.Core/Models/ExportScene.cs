namespace SimPlasPost.Core.Models;

public class ProjectedFace
{
    public double[][] ScreenPts { get; set; } = Array.Empty<double[]>(); // [ptIndex] => [x, y]
    public double[][] Pts3D { get; set; } = Array.Empty<double[]>();     // [ptIndex] => [x, y, z]
    // Per-vertex colors (one entry per ScreenPts vertex).  The PDF exporter
    // uses these to drive Type 4 Gouraud shading so the page matches the
    // node-wise interpolation the on-screen renderer already does.  When
    // the scene has no active field, all entries hold the same neutral
    // colour and the shading degenerates to a flat fill.
    public double[] R { get; set; } = Array.Empty<double>();
    public double[] G { get; set; } = Array.Empty<double>();
    public double[] B { get; set; } = Array.Empty<double>();
    public double Depth { get; set; }
}

public class ProjectedEdge
{
    public double[] P1 { get; set; } = new double[2];
    public double[] P2 { get; set; } = new double[2];
}

public class ProjectedContour
{
    public double[] P1 { get; set; } = new double[2];
    public double[] P2 { get; set; } = new double[2];
    public double R { get; set; }
    public double G { get; set; }
    public double B { get; set; }
}

/// <summary>Stand-alone Bar2 line element projected to screen coordinates.</summary>
public class ProjectedBar
{
    public double[] P1 { get; set; } = new double[2];
    public double[] P2 { get; set; } = new double[2];
    public double R { get; set; }
    public double G { get; set; }
    public double B { get; set; }
}

/// <summary>Stand-alone Point1 element projected to screen coordinates.</summary>
public class ProjectedPoint
{
    public double[] P { get; set; } = new double[2];
    public double R { get; set; }
    public double G { get; set; }
    public double B { get; set; }
}

/// <summary>
/// Candidate position for a contour-line label, expressed in world space so
/// it can be re-projected each frame as the camera changes.  Produced by the
/// renderer once per geometry rebuild and consumed by the overlay's per-frame
/// label-placement pass (which projects, scores, and culls overlapping
/// labels).
/// </summary>
public class ContourLabelWorld
{
    /// <summary>Mid-curve world position of the label's anchor.</summary>
    public double[] Pos { get; set; } = new double[3];
    /// <summary>Unit-ish world direction along the curve at the anchor;
    /// used to derive the screen-space rotation each frame.</summary>
    public double[] TangentDir { get; set; } = new double[3];
    /// <summary>Pre-formatted level value (e.g. "1.23E+0").</summary>
    public string Text { get; set; } = "";
    /// <summary>World arc length of the source polyline.  The overlay sorts
    /// candidates by this descending so longer iso-lines win the placement
    /// race when two candidates would overlap.</summary>
    public double Length { get; set; }
}

/// <summary>
/// Output of the contour-label placement pass: a label that survived the
/// projection + greedy-non-overlap filter, in screen-space ready to draw.
/// </summary>
public class PlacedContourLabel
{
    /// <summary>Screen-space anchor position (centre of the label).</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Rotation in radians, measured in screen frame
    /// (atan2(dy, dx) with screen y growing downward), already flipped to
    /// keep the text right-side-up.</summary>
    public double Angle { get; set; }
    public string Text { get; set; } = "";
}

public class LinePreset
{
    public string Name { get; set; } = "";
    public double SvgWidth { get; set; }
    public double Opacity { get; set; }

    public static readonly LinePreset[] Presets =
    {
        new() { Name = "Hairline", SvgWidth = 0.08, Opacity = 0.07 },
        new() { Name = "X-Thin",   SvgWidth = 0.15, Opacity = 0.13 },
        new() { Name = "Thin",     SvgWidth = 0.25, Opacity = 0.22 },
        new() { Name = "Medium",   SvgWidth = 0.45, Opacity = 0.35 },
        new() { Name = "Thick",    SvgWidth = 0.80, Opacity = 0.55 },
        new() { Name = "Bold",     SvgWidth = 1.50, Opacity = 0.80 },
    };

    public static LinePreset Auto(int nEdges)
    {
        if (nEdges > 5000) return Presets[0];
        if (nEdges > 2000) return Presets[1];
        if (nEdges > 800)  return Presets[2];
        if (nEdges > 300)  return Presets[3];
        if (nEdges > 100)  return Presets[4];
        return Presets[5];
    }
}

public enum DisplayMode
{
    Wireframe,
    Plot,
    Lines
}

public class ExportScene
{
    public List<ProjectedFace> Faces { get; set; } = new();
    public List<ProjectedEdge> VisibleEdges { get; set; } = new();
    public List<ProjectedContour> Contours { get; set; } = new();
    public List<ProjectedBar> Bars { get; set; } = new();
    public List<ProjectedPoint> Points { get; set; } = new();
    public List<PlacedContourLabel> ContourLabels { get; set; } = new();
    public LinePreset Lp { get; set; } = LinePreset.Presets[2];
    public string? FieldName { get; set; }
    public double FMin { get; set; }
    public double FMax { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public DisplayMode Mode { get; set; }
    public double[] Rotation { get; set; } = CameraParams.Identity(); // 3x3 for triad
}
