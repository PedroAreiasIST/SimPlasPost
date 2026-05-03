namespace SimPlasPost.Core.Models;

/// <summary>
/// Kind of geometric dimension recovered from a mesh.  Currently the
/// algorithm produces axis-aligned bounding-box extents (linear), 2D
/// circular hole diameters, 3D cylindrical-hole diameters, and spherical
/// diameters; angular dimensions are deferred until the feature-graph
/// pipeline learns to reason about non-orthogonal corners.
/// </summary>
public enum DimensionKind
{
    /// <summary>Length between two world-space points along an axis.</summary>
    Linear,
    /// <summary>Diameter of a fitted circle (2D hole or 3D cylinder rim).</summary>
    Diameter,
    /// <summary>Diameter of a fitted sphere (a sphere has no preferred
    /// in-plane direction, so the renderer always draws the diameter along
    /// the camera's right axis — i.e. the projected segment equals the
    /// world-space diameter for any view).</summary>
    SphericalDiameter,
}

/// <summary>
/// One mesh-derived dimension expressed in the renderer's normalised
/// [-1,1] frame, ready to be projected by the camera each frame.
///
/// Linear dimensions store the two endpoints in <see cref="P1"/> / <see cref="P2"/>;
/// circular dimensions (2D hole, 3D cylinder, sphere) store the centre in
/// <see cref="P1"/>, the world-space radius in <see cref="Radius"/>, and
/// (for 3D cylinders only) the cylinder axis in <see cref="Axis"/>.  The
/// renderer chooses an in-plane direction at projection time so the
/// diameter chord projects to a readable segment regardless of camera
/// orientation.
/// </summary>
public class DimensionWorld
{
    public DimensionKind Kind { get; init; }
    public string Label { get; init; } = "";
    public double Value { get; init; }
    public double[] P1 { get; init; } = new double[3];
    public double[] P2 { get; init; } = new double[3];
    /// <summary>World-space radius (set for Diameter / SphericalDiameter).</summary>
    public double Radius { get; init; }
    /// <summary>Unit cylinder axis in world space (set only for 3D cylinder
    /// diameters; identically zero for 2D circles and spheres so the
    /// renderer falls back to <c>cam.Right</c> for the chord direction).</summary>
    public double[] Axis { get; init; } = new double[3];
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
