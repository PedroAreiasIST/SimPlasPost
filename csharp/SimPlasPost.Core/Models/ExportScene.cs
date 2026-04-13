namespace SimPlasPost.Core.Models;

public class ProjectedFace
{
    public double[][] ScreenPts { get; set; } = Array.Empty<double[]>(); // [ptIndex] => [x, y]
    public double[][] Pts3D { get; set; } = Array.Empty<double[]>();     // [ptIndex] => [x, y, z]
    public double R { get; set; }
    public double G { get; set; }
    public double B { get; set; }
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
    public LinePreset Lp { get; set; } = LinePreset.Presets[2];
    public string? FieldName { get; set; }
    public double FMin { get; set; }
    public double FMax { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public DisplayMode Mode { get; set; }
    public double[] Rotation { get; set; } = CameraParams.Identity(); // 3x3 for triad
}
