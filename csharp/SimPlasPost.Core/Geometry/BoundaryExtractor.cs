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
            var faces = new List<int[]>();
            foreach (var el in elements)
            {
                if (!FaceTable.Faces.TryGetValue(el.Type, out var ft)) continue;
                foreach (var f in ft.GetFaces(el.Conn))
                    faces.Add(f);
            }
            return faces;
        }

        // 3D: keep faces that appear exactly once
        var faceCount = new Dictionary<string, int>();
        var faceList = new List<(string Key, int[] Face)>();

        foreach (var el in elements)
        {
            if (!FaceTable.Faces.TryGetValue(el.Type, out var ft)) continue;
            if (ft.Dim != 3) continue;

            foreach (var f in ft.GetFaces(el.Conn))
            {
                var sorted = (int[])f.Clone();
                Array.Sort(sorted);
                var key = string.Join(",", sorted);

                faceCount[key] = faceCount.GetValueOrDefault(key, 0) + 1;
                faceList.Add((key, f));
            }
        }

        return faceList
            .Where(pair => faceCount[pair.Key] == 1)
            .Select(pair => pair.Face)
            .ToList();
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
