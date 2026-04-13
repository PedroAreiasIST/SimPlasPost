using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Demos;

/// <summary>
/// Generates demo meshes with analytical field data for testing and demonstration.
/// </summary>
public static class DemoMeshGenerator
{
    public static MeshData GenPlateHole()
    {
        var nodes = new List<double[]>();
        var elements = new List<Element>();
        double R = 0.3, W = 1;
        int nr = 6, nth = 16, no = 6;

        for (int j = 0; j <= nth; j++)
        {
            double th = (j / (double)nth) * Math.PI * 0.5;
            for (int i = 0; i <= nr + no; i++)
            {
                double r = i <= nr
                    ? R + (W * 0.5 - R) * (i / (double)nr)
                    : W * 0.5 + (W - W * 0.5) * ((i - nr) / (double)no);
                nodes.Add(new[] { r * Math.Cos(th), r * Math.Sin(th), 0.0 });
            }
        }

        int c = nr + no + 1;
        for (int j = 0; j < nth; j++)
        for (int i = 0; i < nr + no; i++)
        {
            int n0 = j * c + i;
            elements.Add(new Element
            {
                Type = ElementType.Quad4,
                Conn = new[] { n0, n0 + 1, (j + 1) * c + i + 1, (j + 1) * c + i },
            });
        }

        // Mirror across Y axis
        int oN = nodes.Count, oE = elements.Count;
        var mx = new int[oN];
        int idx = oN;
        for (int i = 0; i < oN; i++)
        {
            if (nodes[i][0] > 1e-10)
            {
                nodes.Add(new[] { -nodes[i][0], nodes[i][1], 0.0 });
                mx[i] = idx++;
            }
            else mx[i] = i;
        }
        for (int e = 0; e < oE; e++)
        {
            var cn = elements[e].Conn.Select(n => mx[n]).ToArray();
            elements.Add(new Element { Type = ElementType.Quad4, Conn = new[] { cn[1], cn[0], cn[3], cn[2] } });
        }

        // Analytical Von Mises stress field
        var fv = new double[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            double x = nodes[i][0], y = nodes[i][1];
            double r = Math.Sqrt(x * x + y * y), th = Math.Atan2(y, x);
            fv[i] = Math.Max(0.2, Math.Min(3.2, 1 + 0.5 * 0.09 / (r * r) * (1 + Math.Cos(2 * th))));
        }

        // Displacement field
        var dv = new double[nodes.Count][];
        for (int i = 0; i < nodes.Count; i++)
        {
            double x = nodes[i][0], y = nodes[i][1];
            double f = Math.Max(0, 1 - Math.Sqrt(x * x + y * y)) * 0.05;
            dv[i] = new[] { x * f * 0.5, y * f, 0.0 };
        }

        return new MeshData
        {
            Name = "Plate with Hole (2D Quads)", Dim = 2,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields =
            {
                ["Von Mises"] = new FieldData { Name = "Von Mises", IsVector = false, ScalarValues = fv },
                ["Displacement"] = new FieldData { Name = "Displacement", IsVector = true, VectorValues = dv },
            },
        };
    }

    public static MeshData Gen3DBeam()
    {
        int nx = 12, ny = 3, nz = 3;
        double Lx = 4, Ly = 0.5, Lz = 0.5;
        var nodes = new List<double[]>();
        var elements = new List<Element>();

        for (int k = 0; k <= nz; k++)
        for (int j = 0; j <= ny; j++)
        for (int i = 0; i <= nx; i++)
            nodes.Add(new[] { (i / (double)nx) * Lx, (j / (double)ny) * Ly - Ly / 2, (k / (double)nz) * Lz - Lz / 2 });

        int Id(int i, int j, int k) => k * (ny + 1) * (nx + 1) + j * (nx + 1) + i;

        for (int k = 0; k < nz; k++)
        for (int j = 0; j < ny; j++)
        for (int i = 0; i < nx; i++)
        {
            elements.Add(new Element
            {
                Type = ElementType.Hex8,
                Conn = new[]
                {
                    Id(i, j, k), Id(i + 1, j, k), Id(i + 1, j + 1, k), Id(i, j + 1, k),
                    Id(i, j, k + 1), Id(i + 1, j, k + 1), Id(i + 1, j + 1, k + 1), Id(i, j + 1, k + 1),
                },
            });
        }

        var fv = new double[nodes.Count];
        var dv = new double[nodes.Count][];
        for (int i = 0; i < nodes.Count; i++)
        {
            double x = nodes[i][0], y = nodes[i][1];
            fv[i] = Math.Max(0, y * (Lx - x) * 4 + 0.5);
            double t = x / Lx;
            dv[i] = new[] { 0.0, -0.15 * t * t * (3 - 2 * t), 0.0 };
        }

        return new MeshData
        {
            Name = "Cantilever (3D Hex8)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields =
            {
                ["Bending Stress"] = new FieldData { Name = "Bending Stress", IsVector = false, ScalarValues = fv },
                ["Displacement"] = new FieldData { Name = "Displacement", IsVector = true, VectorValues = dv },
            },
        };
    }

    public static MeshData Gen2DTri()
    {
        int n = 10;
        var nodes = new List<double[]>();
        var elements = new List<Element>();

        for (int j = 0; j <= n; j++)
        for (int i = 0; i <= n; i++)
            nodes.Add(new[] { i / (double)n, j / (double)n, 0.0 });

        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int b = j * (n + 1) + i;
            elements.Add(new Element { Type = ElementType.Tri3, Conn = new[] { b, b + 1, (j + 1) * (n + 1) + i + 1 } });
            elements.Add(new Element { Type = ElementType.Tri3, Conn = new[] { b, (j + 1) * (n + 1) + i + 1, (j + 1) * (n + 1) + i } });
        }

        var fv = new double[nodes.Count];
        var dv = new double[nodes.Count][];
        for (int i = 0; i < nodes.Count; i++)
        {
            double x = nodes[i][0], y = nodes[i][1];
            fv[i] = Math.Sin(Math.PI * x) * Math.Sin(Math.PI * y);
            dv[i] = new[] { 0.03 * Math.Sin(Math.PI * x) * Math.Sin(Math.PI * y), 0.05 * Math.Sin(Math.PI * x) * Math.Sin(Math.PI * y), 0.0 };
        }

        return new MeshData
        {
            Name = "Unit Square (2D Tri3)", Dim = 2,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields =
            {
                ["Temperature"] = new FieldData { Name = "Temperature", IsVector = false, ScalarValues = fv },
                ["Displacement"] = new FieldData { Name = "Displacement", IsVector = true, VectorValues = dv },
            },
        };
    }

    public static MeshData[] AllDemos() => new[] { GenPlateHole(), Gen3DBeam(), Gen2DTri() };
}
