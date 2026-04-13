namespace SimPlasPost.Core.Models;

public enum ElementType
{
    Tri3, Quad4, Tet4, Hex8, Penta6
}

public class Element
{
    public ElementType Type { get; set; }
    public int[] Conn { get; set; } = Array.Empty<int>();
}

public class ScalarField
{
    public string Name { get; set; } = "";
    public double[] Values { get; set; } = Array.Empty<double>();
}

public class VectorField
{
    public string Name { get; set; } = "";
    public double[][] Values { get; set; } = Array.Empty<double[]>(); // [nodeIndex][0..2]
}

public class FieldData
{
    public string Name { get; set; } = "";
    public bool IsVector { get; set; }
    public double[]? ScalarValues { get; set; }
    public double[][]? VectorValues { get; set; } // [nodeIndex] => [x, y, z]
}

public class MeshData
{
    public string Name { get; set; } = "Untitled";
    public int Dim { get; set; } = 3;
    public double[][] Nodes { get; set; } = Array.Empty<double[]>(); // [nodeIndex] => [x, y, z]
    public List<Element> Elements { get; set; } = new();
    public Dictionary<string, FieldData> Fields { get; set; } = new();

    public FieldData? GetDisplacementField()
    {
        if (Fields.TryGetValue("Displacement", out var f)) return f;
        if (Fields.TryGetValue("displacement", out f)) return f;
        return null;
    }
}
