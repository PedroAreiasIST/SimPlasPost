using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Geometry;

/// <summary>
/// Element topology tables: face definitions, Ensight type mappings, node counts.
/// </summary>
public static class FaceTable
{
    public record FaceEntry(Func<int[], int[][]> GetFaces, int Dim);

    public static readonly Dictionary<ElementType, FaceEntry> Faces = new()
    {
        [ElementType.Tri3] = new(c => new[] { c }, 2),
        [ElementType.Quad4] = new(c => new[] { c }, 2),
        [ElementType.Tet4] = new(c => new[]
        {
            new[] { c[0], c[2], c[1] },
            new[] { c[0], c[1], c[3] },
            new[] { c[1], c[2], c[3] },
            new[] { c[0], c[3], c[2] },
        }, 3),
        [ElementType.Hex8] = new(c => new[]
        {
            new[] { c[0], c[3], c[2], c[1] },
            new[] { c[4], c[5], c[6], c[7] },
            new[] { c[0], c[1], c[5], c[4] },
            new[] { c[2], c[3], c[7], c[6] },
            new[] { c[0], c[4], c[7], c[3] },
            new[] { c[1], c[2], c[6], c[5] },
        }, 3),
        [ElementType.Penta6] = new(c => new[]
        {
            new[] { c[0], c[2], c[1] },
            new[] { c[3], c[4], c[5] },
            new[] { c[0], c[1], c[4], c[3] },
            new[] { c[1], c[2], c[5], c[4] },
            new[] { c[0], c[3], c[5], c[2] },
        }, 3),
    };

    /// <summary>Maps Ensight element type names to internal types.</summary>
    public static readonly Dictionary<string, ElementType> EnsightTypeMap = new()
    {
        ["tria3"]   = ElementType.Tri3,
        ["tria6"]   = ElementType.Tri3,
        ["quad4"]   = ElementType.Quad4,
        ["quad8"]   = ElementType.Quad4,
        ["tetra4"]  = ElementType.Tet4,
        ["tetra10"] = ElementType.Tet4,
        ["hexa8"]   = ElementType.Hex8,
        ["hexa20"]  = ElementType.Hex8,
        ["penta6"]  = ElementType.Penta6,
        ["penta15"] = ElementType.Penta6,
    };

    /// <summary>Nodes per element for Ensight types.</summary>
    public static readonly Dictionary<string, int> EnsightNpn = new()
    {
        ["point"] = 1, ["bar2"] = 2, ["bar3"] = 3,
        ["tria3"] = 3, ["tria6"] = 6, ["quad4"] = 4, ["quad8"] = 8,
        ["tetra4"] = 4, ["tetra10"] = 10, ["hexa8"] = 8, ["hexa20"] = 20,
        ["penta6"] = 6, ["penta15"] = 15,
    };

    /// <summary>Corner (vertex) count for Ensight types (ignoring mid-edge nodes).</summary>
    public static readonly Dictionary<string, int> CornerCount = new()
    {
        ["tria3"] = 3, ["tria6"] = 3, ["quad4"] = 4, ["quad8"] = 4,
        ["tetra4"] = 4, ["tetra10"] = 4, ["hexa8"] = 8, ["hexa20"] = 8,
        ["penta6"] = 6, ["penta15"] = 6,
    };
}
