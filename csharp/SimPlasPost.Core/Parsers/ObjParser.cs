using System.Globalization;
using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Parsers;

/// <summary>
/// Minimal Wavefront OBJ parser.  Handles the subset of OBJ that classical
/// 3D model repositories (Stanford, Keenan Crane, McGuire archive, etc.)
/// commonly use:
///
///   v   x y z [w]                  vertex position (w ignored)
///   vn  nx ny nz                   vertex normal      — IGNORED
///   vt  u v [w]                    texture coordinate — IGNORED
///   f   v1[/vt1[/vn1]] v2 v3 ...   face (3+ vertices, 1-based indices,
///                                  negative indices are relative-to-end)
///   o, g, s, mtllib, usemtl        all ignored (groups, smoothing, materials)
///   #  ...                         comment
///
/// Triangles become Tri3 elements, quads become Quad4, and n-gons (n &gt; 4)
/// are fan-triangulated into Tri3.  No fields are produced by default; the
/// renderer falls back to its neutral "no field" colour, which is fine for
/// loading raw geometry meshes.
/// </summary>
public static class ObjParser
{
    public static MeshData Parse(string source, string name = "OBJ mesh")
    {
        var nodes = new List<double[]>();
        var elements = new List<Element>();
        var ci = CultureInfo.InvariantCulture;

        // OBJ uses 1-based indices and supports negative indices counted
        // from the current vertex list size; we resolve to 0-based here
        // for direct use as Element.Conn entries.
        int Resolve(string token, int currentCount)
        {
            // Each face token may be "v", "v/vt", "v//vn", or "v/vt/vn" —
            // we only care about the leading position index.
            int slash = token.IndexOf('/');
            string head = slash < 0 ? token : token.Substring(0, slash);
            int idx = int.Parse(head, ci);
            if (idx < 0) return currentCount + idx;       // relative
            return idx - 1;                                // 1-based → 0-based
        }

        using var rdr = new StringReader(source);
        string? line;
        while ((line = rdr.ReadLine()) != null)
        {
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash);
            line = line.Trim();
            if (line.Length == 0) continue;

            var tok = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length == 0) continue;

            switch (tok[0])
            {
                case "v":
                    if (tok.Length < 4) continue;
                    nodes.Add(new[]
                    {
                        double.Parse(tok[1], ci),
                        double.Parse(tok[2], ci),
                        double.Parse(tok[3], ci),
                    });
                    break;
                case "f":
                    if (tok.Length < 4) continue;
                    int n = tok.Length - 1;
                    var conn = new int[n];
                    for (int i = 0; i < n; i++)
                        conn[i] = Resolve(tok[i + 1], nodes.Count);

                    if (n == 3)
                    {
                        elements.Add(new Element { Type = ElementType.Tri3, Conn = conn });
                    }
                    else if (n == 4)
                    {
                        elements.Add(new Element { Type = ElementType.Quad4, Conn = conn });
                    }
                    else
                    {
                        // Fan-triangulate n-gons.
                        for (int i = 1; i < n - 1; i++)
                        {
                            elements.Add(new Element
                            {
                                Type = ElementType.Tri3,
                                Conn = new[] { conn[0], conn[i], conn[i + 1] },
                            });
                        }
                    }
                    break;
                // Everything else (vn, vt, o, g, s, mtllib, usemtl) is ignored.
            }
        }

        // Determine 2D vs 3D from the actual geometry: if every Z is ~0,
        // treat the mesh as planar so the camera defaults pick the 2D
        // preset (face-on view).
        double zMin = double.MaxValue, zMax = double.MinValue;
        foreach (var p in nodes)
        {
            if (p[2] < zMin) zMin = p[2];
            if (p[2] > zMax) zMax = p[2];
        }
        int dim = (nodes.Count == 0 || zMax - zMin < 1e-9) ? 2 : 3;

        return new MeshData
        {
            Name = name,
            Dim = dim,
            Nodes = nodes.ToArray(),
            Elements = elements,
        };
    }
}
