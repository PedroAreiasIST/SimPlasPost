using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Geometry;

public static class BoundaryExtractor
{
    /// <summary>
    /// Extract boundary faces from elements.
    /// For 2D: returns all element faces directly.
    /// For 3D: returns faces that appear exactly once (true boundary).
    /// </summary>
    public static List<int[]> Extract(List<Element> elements, bool is3D)
    {
        if (!is3D)
        {
            // 2D mesh: every Tri3/Quad4 face is a surface face. Lower-D
            // elements (Point1/Bar2) are extracted separately by the caller
            // and must not flow into the triangulation path here.
            var faces = new List<int[]>();
            foreach (var el in elements)
            {
                if (!FaceTable.Faces.TryGetValue(el.Type, out var ft)) continue;
                if (ft.Dim < 2) continue;
                foreach (var f in ft.GetFaces(el.Conn))
                    faces.Add(f);
            }
            return faces;
        }

        // 3D mesh: faces of 3D elements (Tet4/Hex8/Penta6) are only kept
        // when they appear exactly once across the volume mesh; stand-alone
        // 2D elements (Tri3/Quad4 floating in a 3D mesh) are always
        // boundary because no enclosing volume can hide them.  Lower-D
        // elements (Point1/Bar2) are returned via ExtractByDim instead.
        var faceCount = new Dictionary<string, int>();
        var faceList = new List<(string Key, int[] Face)>();
        var standaloneFaces = new List<int[]>();

        foreach (var el in elements)
        {
            if (!FaceTable.Faces.TryGetValue(el.Type, out var ft)) continue;
            if (ft.Dim == 2)
            {
                foreach (var f in ft.GetFaces(el.Conn))
                    standaloneFaces.Add(f);
            }
            else if (ft.Dim == 3)
            {
                foreach (var f in ft.GetFaces(el.Conn))
                {
                    var sorted = (int[])f.Clone();
                    Array.Sort(sorted);
                    var key = string.Join(",", sorted);

                    faceCount[key] = faceCount.GetValueOrDefault(key, 0) + 1;
                    faceList.Add((key, f));
                }
            }
        }

        var result = faceList
            .Where(pair => faceCount[pair.Key] == 1)
            .Select(pair => pair.Face)
            .ToList();
        result.AddRange(standaloneFaces);
        return result;
    }

    /// <summary>
    /// Like <see cref="Extract"/>, but additionally returns the index of the
    /// element each boundary face came from.  Required for per-element
    /// shading: when colour comes from element values rather than node
    /// values, the renderer needs to look up the owning element so its
    /// value can be baked into the face's per-vertex colours.
    ///
    /// Element indices are positions into the input <paramref name="elements"/>
    /// list.  Stand-alone 2D elements (Tri3/Quad4 in a 3D mesh) carry the
    /// 2D element's own index, since the face IS the element.
    /// </summary>
    public static List<(int[] Face, int ElementIndex)> ExtractWithSource(List<Element> elements, bool is3D)
    {
        if (!is3D)
        {
            var faces = new List<(int[], int)>();
            for (int e = 0; e < elements.Count; e++)
            {
                var el = elements[e];
                if (!FaceTable.Faces.TryGetValue(el.Type, out var ft)) continue;
                if (ft.Dim < 2) continue;
                foreach (var f in ft.GetFaces(el.Conn))
                    faces.Add((f, e));
            }
            return faces;
        }

        var faceCount = new Dictionary<string, int>();
        var faceList = new List<(string Key, int[] Face, int Elem)>();
        var standalone = new List<(int[] Face, int Elem)>();

        for (int e = 0; e < elements.Count; e++)
        {
            var el = elements[e];
            if (!FaceTable.Faces.TryGetValue(el.Type, out var ft)) continue;
            if (ft.Dim == 2)
            {
                foreach (var f in ft.GetFaces(el.Conn))
                    standalone.Add((f, e));
            }
            else if (ft.Dim == 3)
            {
                foreach (var f in ft.GetFaces(el.Conn))
                {
                    var sorted = (int[])f.Clone();
                    Array.Sort(sorted);
                    var key = string.Join(",", sorted);
                    faceCount[key] = faceCount.GetValueOrDefault(key, 0) + 1;
                    faceList.Add((key, f, e));
                }
            }
        }

        var result = faceList
            .Where(t => faceCount[t.Key] == 1)
            .Select(t => (t.Face, t.Elem))
            .ToList();
        result.AddRange(standalone);
        return result;
    }

    /// <summary>
    /// Collect the connectivity of every element whose face-table dimension
    /// matches <paramref name="dim"/>.  Used to extract Point1 (Dim=0) and
    /// Bar2 (Dim=1) elements separately from the surface mesh: those are
    /// always visible and don't participate in face-cancellation.
    /// </summary>
    public static List<int[]> ExtractByDim(List<Element> elements, int dim)
    {
        var result = new List<int[]>();
        foreach (var el in elements)
        {
            if (FaceTable.Faces.TryGetValue(el.Type, out var ft) && ft.Dim == dim)
                result.Add(el.Conn);
        }
        return result;
    }

    /// <summary>
    /// Triangulate an n-gon face via fan triangulation.
    /// </summary>
    public static List<int[]> TriangulateFace(int[] face)
    {
        if (face.Length == 3)
            return new List<int[]> { face };

        if (face.Length == 4)
            return new List<int[]>
            {
                new[] { face[0], face[1], face[2] },
                new[] { face[0], face[2], face[3] },
            };

        var tris = new List<int[]>();
        for (int i = 1; i < face.Length - 1; i++)
            tris.Add(new[] { face[0], face[i], face[i + 1] });
        return tris;
    }
}
