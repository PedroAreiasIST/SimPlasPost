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

    // Per-step values (populated when the Ensight case has transient variables with "*" file patterns).
    public List<double[]>? StepScalars { get; set; }
    public List<double[][]>? StepVectors { get; set; }
}

public class MeshData
{
    public string Name { get; set; } = "Untitled";
    public int Dim { get; set; } = 3;
    public double[][] Nodes { get; set; } = Array.Empty<double[]>(); // [nodeIndex] => [x, y, z]
    public List<Element> Elements { get; set; } = new();
    public Dictionary<string, FieldData> Fields { get; set; } = new();

    // Multi-step (transient) support.
    public int StepCount { get; set; } = 1;
    public int CurrentStep { get; set; } = 0;
    public List<string> StepLabels { get; set; } = new();

    public FieldData? GetDisplacementField()
    {
        if (Fields.TryGetValue("Displacement", out var f)) return f;
        if (Fields.TryGetValue("displacement", out f)) return f;
        return null;
    }

    /// <summary>
    /// Switch all multi-step fields to the given step index. Fields that don't have
    /// a corresponding entry keep their current values.
    /// </summary>
    public void SetCurrentStep(int step)
    {
        if (step < 0) step = 0;
        if (step >= StepCount) step = StepCount - 1;
        CurrentStep = step;
        foreach (var f in Fields.Values)
        {
            if (f.IsVector)
            {
                if (f.StepVectors != null && f.StepVectors.Count > 0)
                    f.VectorValues = f.StepVectors[Math.Min(step, f.StepVectors.Count - 1)];
            }
            else
            {
                if (f.StepScalars != null && f.StepScalars.Count > 0)
                    f.ScalarValues = f.StepScalars[Math.Min(step, f.StepScalars.Count - 1)];
            }
        }
    }
}
