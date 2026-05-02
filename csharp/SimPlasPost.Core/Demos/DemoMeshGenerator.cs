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
        int nr = 24, nth = 64, no = 24;

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
        int nx = 160, ny = 16, nz = 16;
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
        int n = 40;
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

    public static MeshData[] AllDemos() =>
        new[]
        {
            GenPlateHole(),
            Gen3DBeam(),
            Gen2DTri(),
            GenAnnulusQuads(),
            GenMixedTriQuad(),
            GenTetBox(),
            GenHouseHexWedge(),
            GenToblerone(),
            GenAllElementsShowcase(),
            GenPerElementBeam(),
        }
        // Append the 10 classical-surface "high-end" demos so they appear
        // at the end of the Examples dropdown.  See HighEndMeshGenerator
        // and Demos/LICENSES.md for provenance.
        .Concat(HighEndMeshGenerator.All())
        .ToArray();

    // ─────────────────────────────────────────────────────────────────────
    // Demo of a per-element (real-valued) scalar field: a hex-meshed beam
    // where each element carries a value of its own, evaluated at the
    // element centroid.  Field values vary smoothly with position so
    // neighbouring cells have similar (but distinct) values; the renderer
    // and the PDF exporter both use sharp per-element shading, so the
    // element boundaries appear as crisp colour steps — exactly how
    // Paraview / Tecplot display element-based variables.
    // ─────────────────────────────────────────────────────────────────────
    public static MeshData GenPerElementBeam()
    {
        int nx = 48, ny = 12, nz = 8;
        double Lx = 2.4, Ly = 0.6, Lz = 0.5;
        var nodes = new List<double[]>();
        for (int k = 0; k <= nz; k++)
        for (int j = 0; j <= ny; j++)
        for (int i = 0; i <= nx; i++)
            nodes.Add(new[] { (i / (double)nx) * Lx, (j / (double)ny) * Ly - Ly / 2, (k / (double)nz) * Lz - Lz / 2 });

        int Id(int i, int j, int k) => k * (ny + 1) * (nx + 1) + j * (nx + 1) + i;

        var elements = new List<Element>();
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

        // Per-element scalar: an "element-averaged stress" stand-in.
        // Sampling at the element centroid means neighbouring cells get
        // close-but-different values — the resulting visualisation shows
        // the field's shape as a stepped piecewise-constant surface, with
        // sharp colour seams at every element boundary.
        var elemValues = new double[elements.Count];
        for (int e = 0; e < elements.Count; e++)
        {
            double cx = 0, cy = 0;
            foreach (int n in elements[e].Conn) { cx += nodes[n][0]; cy += nodes[n][1]; }
            cx /= elements[e].Conn.Length;
            cy /= elements[e].Conn.Length;
            elemValues[e] = Math.Sin(2.5 * cx / Lx * Math.PI) * Math.Cos(2 * cy / Ly * Math.PI) + 0.5 * cx / Lx;
        }

        return new MeshData
        {
            Name = "Per-Element Beam (Hex8, element scalar)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields =
            {
                ["Element Stress"] = new FieldData
                {
                    Name = "Element Stress",
                    IsVector = false,
                    IsPerElement = true,
                    ScalarValues = elemValues,
                },
            },
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // Showcase: a single mesh that contains one element of every type the
    // viewer supports (Point1, Bar2, Tri3, Quad4, Tet4, Penta6, Hex8).
    // The pieces are placed in distinct regions of space so each can be
    // identified at a glance, while sharing some structural nodes (the
    // hex top with the wedge base) to demonstrate that mixed-type meshes
    // can be conformal.
    // ─────────────────────────────────────────────────────────────────────
    public static MeshData GenAllElementsShowcase()
    {
        var nodes = new List<double[]>();
        var elements = new List<Element>();

        int N(double x, double y, double z)
        {
            nodes.Add(new[] { x, y, z });
            return nodes.Count - 1;
        }

        // Hex8 cube at the centre.
        int h0 = N(-0.5, -0.5, 0.0), h1 = N( 0.5, -0.5, 0.0);
        int h2 = N( 0.5,  0.5, 0.0), h3 = N(-0.5,  0.5, 0.0);
        int h4 = N(-0.5, -0.5, 1.0), h5 = N( 0.5, -0.5, 1.0);
        int h6 = N( 0.5,  0.5, 1.0), h7 = N(-0.5,  0.5, 1.0);
        elements.Add(new Element { Type = ElementType.Hex8, Conn = new[] { h0, h1, h2, h3, h4, h5, h6, h7 } });

        // Penta6 wedge above the hex, sharing the hex's top quad as its
        // base.  Two new ridge nodes are introduced; the wedge's bottom-quad
        // face has the same vertex set as the hex's top face, so boundary
        // extraction cancels the seam.
        int rb = N(0.0, -0.5, 1.5);
        int rf = N(0.0,  0.5, 1.5);
        elements.Add(new Element
        {
            Type = ElementType.Penta6,
            Conn = new[] { h4, h5, rb, h7, h6, rf },
        });

        // Tet4 to the +X side, separated from the hex.
        int t0 = N(1.6,  0.0, 0.0);
        int t1 = N(1.0, -0.5, 0.5);
        int t2 = N(1.0,  0.5, 0.5);
        int t3 = N(1.0,  0.0, 1.2);
        elements.Add(new Element { Type = ElementType.Tet4, Conn = new[] { t0, t1, t2, t3 } });

        // Quad4 plate on the -X side (vertical wall).
        int q0 = N(-1.6, -0.5, 0.0);
        int q1 = N(-1.6,  0.5, 0.0);
        int q2 = N(-1.6,  0.5, 1.0);
        int q3 = N(-1.6, -0.5, 1.0);
        elements.Add(new Element { Type = ElementType.Quad4, Conn = new[] { q0, q1, q2, q3 } });

        // Tri3 plate underneath.
        int tr0 = N( 0.0, -0.7, -0.5);
        int tr1 = N( 0.7,  0.7, -0.5);
        int tr2 = N(-0.7,  0.7, -0.5);
        elements.Add(new Element { Type = ElementType.Tri3, Conn = new[] { tr0, tr1, tr2 } });

        // Bar2 (a beam from one outer corner to another, crossing the scene
        // diagonally so it's clearly visible against the volumes).
        int b0 = N(-1.4, -1.2, 1.6);
        int b1 = N( 1.4,  1.2, 1.6);
        elements.Add(new Element { Type = ElementType.Bar2, Conn = new[] { b0, b1 } });

        // Point1 (a marker / load point off to the side).
        int p0 = N(0.0, 1.4, 0.6);
        elements.Add(new Element { Type = ElementType.Point1, Conn = new[] { p0 } });

        // Field: distance from the central axis plus a vertical bias.
        var fv = new double[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            double x = nodes[i][0], y = nodes[i][1], z = nodes[i][2];
            fv[i] = Math.Sqrt(x * x + y * y) + 0.3 * z;
        }

        return new MeshData
        {
            Name = "Showcase (All 7 Element Types)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields =
            {
                ["Field"] = new FieldData { Name = "Field", IsVector = false, ScalarValues = fv },
            },
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // i) 2D mesh built entirely from QUAD4 elements: a half-annulus
    //    (curved beam).  Uses a structured (nr × nth) grid in polar
    //    coordinates.
    // ─────────────────────────────────────────────────────────────────────
    public static MeshData GenAnnulusQuads()
    {
        int nr = 16, nth = 96;
        double R1 = 0.4, R2 = 1.0;
        var nodes = new List<double[]>();
        for (int j = 0; j <= nth; j++)
        for (int i = 0; i <= nr; i++)
        {
            double r = R1 + (R2 - R1) * (i / (double)nr);
            double th = (j / (double)nth) * Math.PI;
            nodes.Add(new[] { r * Math.Cos(th), r * Math.Sin(th), 0.0 });
        }

        int c = nr + 1;
        var elements = new List<Element>();
        for (int j = 0; j < nth; j++)
        for (int i = 0; i < nr; i++)
        {
            int n0 = j * c + i;
            elements.Add(new Element
            {
                Type = ElementType.Quad4,
                Conn = new[] { n0, n0 + 1, (j + 1) * c + i + 1, (j + 1) * c + i },
            });
        }

        var fv = new double[nodes.Count];
        var dv = new double[nodes.Count][];
        for (int i = 0; i < nodes.Count; i++)
        {
            double x = nodes[i][0], y = nodes[i][1];
            double r = Math.Sqrt(x * x + y * y);
            // hoop-stress-like analytical field, larger near the inner radius
            fv[i] = R1 * R1 / (r * r);
            // small radial breathing displacement
            double f = 0.04 * (R2 - r);
            dv[i] = new[] { x / r * f, y / r * f, 0.0 };
        }

        return new MeshData
        {
            Name = "Annular Beam (2D Quad4)", Dim = 2,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields =
            {
                ["Hoop Stress"] = new FieldData { Name = "Hoop Stress", IsVector = false, ScalarValues = fv },
                ["Displacement"] = new FieldData { Name = "Displacement", IsVector = true, VectorValues = dv },
            },
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // ii) 2D mesh mixing TRI3 and QUAD4 elements: a unit square where the
    //     left half is quads and the right half is each-quad-split-into-2
    //     triangles.  Both halves share their boundary nodes so the mesh
    //     is conformal across the seam.
    // ─────────────────────────────────────────────────────────────────────
    public static MeshData GenMixedTriQuad()
    {
        int n = 40;
        var nodes = new List<double[]>();
        for (int j = 0; j <= n; j++)
        for (int i = 0; i <= n; i++)
            nodes.Add(new[] { i / (double)n, j / (double)n, 0.0 });

        int Id(int i, int j) => j * (n + 1) + i;

        var elements = new List<Element>();
        int half = n / 2;
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int a = Id(i, j), b = Id(i + 1, j), c = Id(i + 1, j + 1), d = Id(i, j + 1);
            if (i < half)
            {
                elements.Add(new Element { Type = ElementType.Quad4, Conn = new[] { a, b, c, d } });
            }
            else
            {
                elements.Add(new Element { Type = ElementType.Tri3, Conn = new[] { a, b, c } });
                elements.Add(new Element { Type = ElementType.Tri3, Conn = new[] { a, c, d } });
            }
        }

        var fv = new double[nodes.Count];
        var dv = new double[nodes.Count][];
        for (int i = 0; i < nodes.Count; i++)
        {
            double x = nodes[i][0], y = nodes[i][1];
            fv[i] = Math.Sin(Math.PI * x) * Math.Cos(Math.PI * y);
            double s = 0.04 * Math.Sin(Math.PI * x) * Math.Sin(Math.PI * y);
            dv[i] = new[] { s, s, 0.0 };
        }

        return new MeshData
        {
            Name = "Mixed Patch (2D Tri3 + Quad4)", Dim = 2,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields =
            {
                ["Field"] = new FieldData { Name = "Field", IsVector = false, ScalarValues = fv },
                ["Displacement"] = new FieldData { Name = "Displacement", IsVector = true, VectorValues = dv },
            },
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // iii) 3D mesh of TET4 elements: a beam meshed by splitting each
    //      structured-grid hex into 6 tetrahedra around the c0–c6 main
    //      diagonal (uniform chirality so adjacent cells share their
    //      diagonal triangulation).
    // ─────────────────────────────────────────────────────────────────────
    public static MeshData GenTetBox()
    {
        int nx = 48, ny = 12, nz = 10;
        double Lx = 2.4, Ly = 0.6, Lz = 0.5;
        var nodes = new List<double[]>();
        for (int k = 0; k <= nz; k++)
        for (int j = 0; j <= ny; j++)
        for (int i = 0; i <= nx; i++)
            nodes.Add(new[] { (i / (double)nx) * Lx, (j / (double)ny) * Ly - Ly / 2, (k / (double)nz) * Lz - Lz / 2 });

        int Id(int i, int j, int k) => k * (ny + 1) * (nx + 1) + j * (nx + 1) + i;

        var elements = new List<Element>();
        for (int k = 0; k < nz; k++)
        for (int j = 0; j < ny; j++)
        for (int i = 0; i < nx; i++)
        {
            int c0 = Id(i, j, k),     c1 = Id(i + 1, j, k),     c2 = Id(i + 1, j + 1, k),     c3 = Id(i, j + 1, k);
            int c4 = Id(i, j, k + 1), c5 = Id(i + 1, j, k + 1), c6 = Id(i + 1, j + 1, k + 1), c7 = Id(i, j + 1, k + 1);
            elements.Add(new Element { Type = ElementType.Tet4, Conn = new[] { c0, c1, c2, c6 } });
            elements.Add(new Element { Type = ElementType.Tet4, Conn = new[] { c0, c2, c3, c6 } });
            elements.Add(new Element { Type = ElementType.Tet4, Conn = new[] { c0, c3, c7, c6 } });
            elements.Add(new Element { Type = ElementType.Tet4, Conn = new[] { c0, c7, c4, c6 } });
            elements.Add(new Element { Type = ElementType.Tet4, Conn = new[] { c0, c4, c5, c6 } });
            elements.Add(new Element { Type = ElementType.Tet4, Conn = new[] { c0, c5, c1, c6 } });
        }

        var fv = new double[nodes.Count];
        var dv = new double[nodes.Count][];
        for (int i = 0; i < nodes.Count; i++)
        {
            double x = nodes[i][0], y = nodes[i][1];
            fv[i] = Math.Max(0, y * (Lx - x) * 4 + 0.5);
            double t = x / Lx;
            dv[i] = new[] { 0.0, -0.12 * t * t * (3 - 2 * t), 0.0 };
        }

        return new MeshData
        {
            Name = "Tet Beam (3D Tet4)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields =
            {
                ["Bending Stress"] = new FieldData { Name = "Bending Stress", IsVector = false, ScalarValues = fv },
                ["Displacement"] = new FieldData { Name = "Displacement", IsVector = true, VectorValues = dv },
            },
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // iv) 3D mesh combining HEX8 (the rectangular base) and PENTA6 (the
    //     triangular-prism / "toblerone" roof): a longhouse with a pitched
    //     roof.  Each y-slice has 5 nodes (4 corners of the box + 1 ridge);
    //     between adjacent slices we emit one hex (the room) and one
    //     wedge (the roof).  The wedge's bottom quad face has the same
    //     vertex set as the hex's top face, so the seam is internal and
    //     the boundary extractor cancels it.
    // ─────────────────────────────────────────────────────────────────────
    public static MeshData GenHouseHexWedge()
    {
        int ny = 60;
        double W = 1.0, D = 4.0, H1 = 0.7, H2 = 1.2;
        var nodes = new List<double[]>();
        for (int j = 0; j <= ny; j++)
        {
            double y = (j / (double)ny) * D;
            nodes.Add(new[] { 0.0,     y, 0.0 });   // 0: bottom-left
            nodes.Add(new[] { W,       y, 0.0 });   // 1: bottom-right
            nodes.Add(new[] { 0.0,     y, H1  });   // 2: eaves-left
            nodes.Add(new[] { W,       y, H1  });   // 3: eaves-right
            nodes.Add(new[] { W / 2.0, y, H2  });   // 4: ridge
        }

        int Id(int local, int j) => j * 5 + local;

        var elements = new List<Element>();
        for (int j = 0; j < ny; j++)
        {
            // Hex: rectangular base from z=0 to z=H1 along this y-slab.
            elements.Add(new Element
            {
                Type = ElementType.Hex8,
                Conn = new[]
                {
                    Id(0, j), Id(1, j), Id(1, j + 1), Id(0, j + 1),     // bottom (z=0)
                    Id(2, j), Id(3, j), Id(3, j + 1), Id(2, j + 1),     // top (z=H1, shared with wedge bottom)
                },
            });

            // Wedge: triangular prism roof from z=H1 to the ridge.
            elements.Add(new Element
            {
                Type = ElementType.Penta6,
                Conn = new[]
                {
                    Id(2, j),     Id(3, j),     Id(4, j),       // back triangle (y=y_j)
                    Id(2, j + 1), Id(3, j + 1), Id(4, j + 1),   // front triangle (y=y_{j+1})
                },
            });
        }

        var fv = new double[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            double y = nodes[i][1], z = nodes[i][2];
            // height shaded by a sinusoidal bump along y so the roof reads clearly
            fv[i] = z + 0.25 * Math.Sin(Math.PI * y / D);
        }

        return new MeshData
        {
            Name = "House (3D Hex8 + Penta6)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields =
            {
                ["Height"] = new FieldData { Name = "Height", IsVector = false, ScalarValues = fv },
            },
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // v) 3D mesh mixing TET4 and PENTA6 elements: a triangular prism
    //    extruded along y, where every other slab is one wedge and the
    //    interleaved slabs are split into 3 tetrahedra.  Adjacent slabs
    //    share their triangular cap face (3 nodes) regardless of which
    //    decomposition was chosen, so the seam is conformal.
    // ─────────────────────────────────────────────────────────────────────
    public static MeshData GenToblerone()
    {
        int ny = 64;
        double W = 1.0, D = 4.0, H = 0.8;
        var nodes = new List<double[]>();
        for (int j = 0; j <= ny; j++)
        {
            double y = (j / (double)ny) * D;
            nodes.Add(new[] { 0.0,     y, 0.0 });
            nodes.Add(new[] { W,       y, 0.0 });
            nodes.Add(new[] { W / 2.0, y, H   });
        }

        int Id(int local, int j) => j * 3 + local;

        var elements = new List<Element>();
        for (int j = 0; j < ny; j++)
        {
            int a = Id(0, j),     b = Id(1, j),     c = Id(2, j);
            int A = Id(0, j + 1), B = Id(1, j + 1), C = Id(2, j + 1);
            if ((j & 1) == 0)
            {
                // Wedge slab.
                elements.Add(new Element
                {
                    Type = ElementType.Penta6,
                    Conn = new[] { a, b, c, A, B, C },
                });
            }
            else
            {
                // 3-tet decomposition of the same prism slab; the back/front
                // triangles {a,b,c} and {A,B,C} appear once each as boundary
                // candidates and match the neighbouring wedge's caps.
                elements.Add(new Element { Type = ElementType.Tet4, Conn = new[] { a, b, c, C } });
                elements.Add(new Element { Type = ElementType.Tet4, Conn = new[] { a, b, C, A } });
                elements.Add(new Element { Type = ElementType.Tet4, Conn = new[] { b, A, B, C } });
            }
        }

        var fv = new double[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            double y = nodes[i][1], z = nodes[i][2];
            fv[i] = z + 0.3 * Math.Sin(2 * Math.PI * y / D);
        }

        return new MeshData
        {
            Name = "Toblerone (3D Tet4 + Penta6)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields =
            {
                ["Field"] = new FieldData { Name = "Field", IsVector = false, ScalarValues = fv },
            },
        };
    }
}
