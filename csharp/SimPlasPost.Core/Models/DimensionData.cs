namespace SimPlasPost.Core.Models;

/// <summary>
/// Kind of geometric dimension recovered from a mesh.  Currently the
/// algorithm produces axis-aligned bounding-box extents (linear) and 2D
/// circular hole diameters; angular dimensions are deferred until the
/// feature-graph pipeline learns to reason about non-orthogonal corners.
/// </summary>
public enum DimensionKind
{
    /// <summary>Length between two world-space points along an axis.</summary>
    Linear,
    /// <summary>Diameter of a fitted circle (2D hole detection).</summary>
    Diameter,
}

/// <summary>
/// One mesh-derived dimension expressed in the renderer's normalised
/// [-1,1] frame, ready to be projected by the camera each frame.
///
/// Linear dimensions store the two endpoints in <see cref="P1"/> / <see cref="P2"/>;
/// diameter dimensions store the circle centre in <see cref="P1"/> and a
/// point on the rim in <see cref="P2"/>, so the renderer can derive the
/// projected radius without a separate scaling pass.
/// </summary>
public class DimensionWorld
{
    public DimensionKind Kind { get; init; }
    public string Label { get; init; } = "";
    public double Value { get; init; }
    public double[] P1 { get; init; } = new double[3];
    public double[] P2 { get; init; } = new double[3];
}

/// <summary>
/// One dimension annotation projected to screen space and laid out with
/// the dimension line offset outside the mesh silhouette.  Same data
/// structure feeds the live overlay (Avalonia DrawingContext) and the PDF
/// exporter so the two stay byte-identical.
/// </summary>
public class DimensionScreen
{
    public DimensionKind Kind { get; init; }
    public string Label { get; init; } = "";
    public double Value { get; init; }

    /// <summary>Endpoints of the measured feature on screen (extension-line tails).</summary>
    public double[] Ext1 { get; init; } = new double[2];
    public double[] Ext2 { get; init; } = new double[2];

    /// <summary>Endpoints of the offset dimension line (extension-line tips).</summary>
    public double[] Dim1 { get; init; } = new double[2];
    public double[] Dim2 { get; init; } = new double[2];

    /// <summary>Anchor for the value label, slightly outside the dimension line.</summary>
    public double[] LabelPos { get; init; } = new double[2];

    /// <summary>Label rotation in degrees (screen frame, y-down).  For
    /// diameters the value is 0 — the label always sits horizontally to the
    /// right of the rim.</summary>
    public double Rot { get; init; }
}
