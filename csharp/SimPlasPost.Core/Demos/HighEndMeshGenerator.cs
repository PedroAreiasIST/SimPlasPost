using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Demos;

/// <summary>
/// Ten high-end procedural meshes drawn from classical differential
/// geometry — the kind of surfaces you'd see in graphics textbooks and
/// model repositories.  All are generated analytically (no external mesh
/// downloads, no IP issues), and each carries an analytical scalar field
/// chosen to highlight the surface's geometry (Gaussian curvature,
/// distance from axis, height, etc.).
///
/// Each mesh targets ~10–20K triangles so the renderer's performance and
/// PDF Type-4 shading get a real workout, but the code stays pure C#
/// with no embedded data.
/// </summary>
public static class HighEndMeshGenerator
{
    // ───────────────────────────────────────────────────────────────────
    // 1. Geodesic sphere (icosahedron with 4 levels of midpoint
    //    subdivision → 20·4³ = 1280 ⇒ 5120 triangles after one more
    //    split, and we use 3 levels for ~5120-tri sphere).
    // ───────────────────────────────────────────────────────────────────
    public static MeshData GenGeodesicSphere()
    {
        // Start from a regular icosahedron.
        double t = (1.0 + Math.Sqrt(5.0)) / 2.0;
        var v0 = new List<double[]>
        {
            new[] { -1.0,  t,  0.0 }, new[] {  1.0,  t,  0.0 },
            new[] { -1.0, -t,  0.0 }, new[] {  1.0, -t,  0.0 },
            new[] {  0.0, -1.0,  t }, new[] {  0.0,  1.0,  t },
            new[] {  0.0, -1.0, -t }, new[] {  0.0,  1.0, -t },
            new[] {  t,  0.0, -1.0 }, new[] {  t,  0.0,  1.0 },
            new[] { -t,  0.0, -1.0 }, new[] { -t,  0.0,  1.0 },
        };
        // Project to unit sphere.
        for (int i = 0; i < v0.Count; i++)
        {
            double l = Math.Sqrt(v0[i][0]*v0[i][0] + v0[i][1]*v0[i][1] + v0[i][2]*v0[i][2]);
            v0[i] = new[] { v0[i][0]/l, v0[i][1]/l, v0[i][2]/l };
        }
        var f0 = new List<int[]>
        {
            new[] { 0,11, 5}, new[] { 0, 5, 1}, new[] { 0, 1, 7}, new[] { 0, 7,10}, new[] { 0,10,11},
            new[] { 1, 5, 9}, new[] { 5,11, 4}, new[] {11,10, 2}, new[] {10, 7, 6}, new[] { 7, 1, 8},
            new[] { 3, 9, 4}, new[] { 3, 4, 2}, new[] { 3, 2, 6}, new[] { 3, 6, 8}, new[] { 3, 8, 9},
            new[] { 4, 9, 5}, new[] { 2, 4,11}, new[] { 6, 2,10}, new[] { 8, 6, 7}, new[] { 9, 8, 1},
        };

        var nodes = v0;
        var faces = f0;
        // Subdivide n times: each triangle splits into 4 by introducing
        // midpoints of edges, then projecting them to the unit sphere.
        for (int iter = 0; iter < 4; iter++)
        {
            var midCache = new Dictionary<long, int>();
            var newFaces = new List<int[]>(faces.Count * 4);
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (midCache.TryGetValue(key, out int idx)) return idx;
                var pa = nodes[a]; var pb = nodes[b];
                var pm = new[] { (pa[0]+pb[0])/2, (pa[1]+pb[1])/2, (pa[2]+pb[2])/2 };
                double l = Math.Sqrt(pm[0]*pm[0] + pm[1]*pm[1] + pm[2]*pm[2]);
                pm[0]/=l; pm[1]/=l; pm[2]/=l;
                idx = nodes.Count;
                nodes.Add(pm);
                midCache[key] = idx;
                return idx;
            }
            foreach (var f in faces)
            {
                int m01 = Mid(f[0], f[1]);
                int m12 = Mid(f[1], f[2]);
                int m20 = Mid(f[2], f[0]);
                newFaces.Add(new[] { f[0], m01, m20 });
                newFaces.Add(new[] { f[1], m12, m01 });
                newFaces.Add(new[] { f[2], m20, m12 });
                newFaces.Add(new[] { m01, m12, m20 });
            }
            faces = newFaces;
        }

        var elements = faces.Select(f => new Element { Type = ElementType.Tri3, Conn = f }).ToList();
        var fv = new double[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            // Y_3,2-style spherical-harmonic-ish field for visual interest.
            double x = nodes[i][0], y = nodes[i][1], z = nodes[i][2];
            fv[i] = (x*x - y*y) * z;
        }
        return new MeshData
        {
            Name = "Geodesic Sphere (5120 tri)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields = { ["Y₃₂"] = new FieldData { Name = "Y₃₂", IsVector = false, ScalarValues = fv } },
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // 2. Torus (ring) — a parametric (u, v) grid on T² ↪ R³.
    // ───────────────────────────────────────────────────────────────────
    public static MeshData GenTorus()
    {
        int nu = 96, nv = 48;
        double R = 1.0, r = 0.32;
        var nodes = new List<double[]>();
        for (int j = 0; j <= nv; j++)
        for (int i = 0; i <= nu; i++)
        {
            double u = (i / (double)nu) * 2 * Math.PI;
            double v = (j / (double)nv) * 2 * Math.PI;
            double cx = (R + r * Math.Cos(v)) * Math.Cos(u);
            double cy = (R + r * Math.Cos(v)) * Math.Sin(u);
            double cz = r * Math.Sin(v);
            nodes.Add(new[] { cx, cy, cz });
        }
        int Id(int i, int j) => j * (nu + 1) + i;
        var elements = new List<Element>();
        for (int j = 0; j < nv; j++)
        for (int i = 0; i < nu; i++)
        {
            elements.Add(new Element
            {
                Type = ElementType.Quad4,
                Conn = new[] { Id(i, j), Id(i + 1, j), Id(i + 1, j + 1), Id(i, j + 1) },
            });
        }
        var fv = new double[nodes.Count];
        for (int k = 0; k < nodes.Count; k++)
        {
            double x = nodes[k][0], y = nodes[k][1], z = nodes[k][2];
            // Gaussian curvature K = cos v / (r·(R + r·cos v));
            // we shade with cos v which is monotone in K.
            double rho = Math.Sqrt(x*x + y*y);
            double cv = (rho - R) / r;
            fv[k] = cv;
        }
        return new MeshData
        {
            Name = "Torus (parametric, 9216 quads)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields = { ["Curvature ∝ cos v"] = new FieldData { Name = "Curvature ∝ cos v", IsVector = false, ScalarValues = fv } },
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // 3. Trefoil knot tube — parametric closed-curve sweep.
    // ───────────────────────────────────────────────────────────────────
    public static MeshData GenTrefoilKnot()
    {
        int nu = 256, nv = 24;
        double tubeR = 0.30;
        var nodes = new List<double[]>();
        // Parametric trefoil curve and its analytic Frenet-like frame.
        double[] Curve(double u)
        {
            return new[]
            {
                Math.Sin(u) + 2 * Math.Sin(2 * u),
                Math.Cos(u) - 2 * Math.Cos(2 * u),
                -Math.Sin(3 * u),
            };
        }
        for (int i = 0; i < nu; i++)
        {
            double u = (i / (double)nu) * 2 * Math.PI;
            var c = Curve(u);
            // Frame from finite-difference derivatives.
            var c1 = Curve(u + 1e-3); var c0 = Curve(u - 1e-3);
            var T = new[] { c1[0]-c0[0], c1[1]-c0[1], c1[2]-c0[2] };
            double tl = Math.Sqrt(T[0]*T[0]+T[1]*T[1]+T[2]*T[2]); T[0]/=tl;T[1]/=tl;T[2]/=tl;
            // Pick an arbitrary up not parallel to T.
            var up = Math.Abs(T[1]) < 0.9 ? new[] {0.0,1.0,0.0} : new[] {1.0,0.0,0.0};
            var N = new[] { up[1]*T[2]-up[2]*T[1], up[2]*T[0]-up[0]*T[2], up[0]*T[1]-up[1]*T[0] };
            double nl = Math.Sqrt(N[0]*N[0]+N[1]*N[1]+N[2]*N[2]); N[0]/=nl;N[1]/=nl;N[2]/=nl;
            var B = new[] { T[1]*N[2]-T[2]*N[1], T[2]*N[0]-T[0]*N[2], T[0]*N[1]-T[1]*N[0] };
            for (int j = 0; j < nv; j++)
            {
                double v = (j / (double)nv) * 2 * Math.PI;
                double cv = Math.Cos(v) * tubeR, sv = Math.Sin(v) * tubeR;
                nodes.Add(new[]
                {
                    c[0] + cv*N[0] + sv*B[0],
                    c[1] + cv*N[1] + sv*B[1],
                    c[2] + cv*N[2] + sv*B[2],
                });
            }
        }
        int Id(int i, int j) => (i % nu) * nv + (j % nv);
        var elements = new List<Element>();
        for (int i = 0; i < nu; i++)
        for (int j = 0; j < nv; j++)
        {
            elements.Add(new Element
            {
                Type = ElementType.Quad4,
                Conn = new[] { Id(i, j), Id(i + 1, j), Id(i + 1, j + 1), Id(i, j + 1) },
            });
        }
        var fv = new double[nodes.Count];
        for (int k = 0; k < nodes.Count; k++)
        {
            int i = k / nv;
            fv[k] = Math.Sin(3 * (i / (double)nu) * 2 * Math.PI);
        }
        return new MeshData
        {
            Name = "Trefoil Knot Tube (6144 quads)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields = { ["Phase"] = new FieldData { Name = "Phase", IsVector = false, ScalarValues = fv } },
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // 4. Möbius strip — non-orientable parametric surface.
    // ───────────────────────────────────────────────────────────────────
    public static MeshData GenMobiusStrip()
    {
        int nu = 200, nv = 16;
        double R = 1.0;
        var nodes = new List<double[]>();
        for (int j = 0; j <= nv; j++)
        for (int i = 0; i <= nu; i++)
        {
            double u = (i / (double)nu) * 2 * Math.PI;
            double v = -0.4 + (j / (double)nv) * 0.8;
            double x = (R + v * Math.Cos(u / 2)) * Math.Cos(u);
            double y = (R + v * Math.Cos(u / 2)) * Math.Sin(u);
            double z = v * Math.Sin(u / 2);
            nodes.Add(new[] { x, y, z });
        }
        int Id(int i, int j) => j * (nu + 1) + i;
        var elements = new List<Element>();
        for (int j = 0; j < nv; j++)
        for (int i = 0; i < nu; i++)
        {
            elements.Add(new Element
            {
                Type = ElementType.Quad4,
                Conn = new[] { Id(i, j), Id(i + 1, j), Id(i + 1, j + 1), Id(i, j + 1) },
            });
        }
        var fv = new double[nodes.Count];
        for (int k = 0; k < nodes.Count; k++)
        {
            int i = k % (nu + 1);
            int j = k / (nu + 1);
            double v = -0.4 + (j / (double)nv) * 0.8;
            fv[k] = v;
        }
        return new MeshData
        {
            Name = "Möbius Strip (3200 quads)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields = { ["v"] = new FieldData { Name = "v", IsVector = false, ScalarValues = fv } },
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // 5. Klein bottle — Lawson's "figure-8" immersion in R³.
    // ───────────────────────────────────────────────────────────────────
    public static MeshData GenKleinBottle()
    {
        int nu = 96, nv = 48;
        var nodes = new List<double[]>();
        for (int j = 0; j <= nv; j++)
        for (int i = 0; i <= nu; i++)
        {
            double u = (i / (double)nu) * 2 * Math.PI;
            double v = (j / (double)nv) * 2 * Math.PI;
            double r = 0.5 * (1 - Math.Cos(u) / 2);
            double x, y, z;
            if (u < Math.PI)
            {
                x = 3 * Math.Cos(u) * (1 + Math.Sin(u)) + 2 * (1 - Math.Cos(u) / 2) * Math.Cos(u) * Math.Cos(v);
                y = 8 * Math.Sin(u) + 2 * (1 - Math.Cos(u) / 2) * Math.Sin(u) * Math.Cos(v);
            }
            else
            {
                x = 3 * Math.Cos(u) * (1 + Math.Sin(u)) + 2 * (1 - Math.Cos(u) / 2) * Math.Cos(v + Math.PI);
                y = 8 * Math.Sin(u);
            }
            z = 2 * (1 - Math.Cos(u) / 2) * Math.Sin(v);
            nodes.Add(new[] { x * 0.12, y * 0.12, z * 0.12 });
        }
        int Id(int i, int j) => j * (nu + 1) + i;
        var elements = new List<Element>();
        for (int j = 0; j < nv; j++)
        for (int i = 0; i < nu; i++)
        {
            elements.Add(new Element
            {
                Type = ElementType.Quad4,
                Conn = new[] { Id(i, j), Id(i + 1, j), Id(i + 1, j + 1), Id(i, j + 1) },
            });
        }
        var fv = new double[nodes.Count];
        for (int k = 0; k < nodes.Count; k++) fv[k] = nodes[k][2];
        return new MeshData
        {
            Name = "Klein Bottle (4608 quads)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields = { ["z"] = new FieldData { Name = "z", IsVector = false, ScalarValues = fv } },
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // 6. Helicoid — minimal surface generated by a horizontal line
    //    rotating + translating along the z axis.
    // ───────────────────────────────────────────────────────────────────
    public static MeshData GenHelicoid()
    {
        int nu = 200, nv = 32;
        double turns = 3.0;
        var nodes = new List<double[]>();
        for (int j = 0; j <= nv; j++)
        for (int i = 0; i <= nu; i++)
        {
            double u = (i / (double)nu) * turns * 2 * Math.PI;
            double v = -1.0 + (j / (double)nv) * 2.0;
            double x = v * Math.Cos(u);
            double y = v * Math.Sin(u);
            double z = u * 0.2;
            nodes.Add(new[] { x, y, z });
        }
        int Id(int i, int j) => j * (nu + 1) + i;
        var elements = new List<Element>();
        for (int j = 0; j < nv; j++)
        for (int i = 0; i < nu; i++)
        {
            elements.Add(new Element
            {
                Type = ElementType.Quad4,
                Conn = new[] { Id(i, j), Id(i + 1, j), Id(i + 1, j + 1), Id(i, j + 1) },
            });
        }
        var fv = new double[nodes.Count];
        for (int k = 0; k < nodes.Count; k++)
        {
            double x = nodes[k][0], y = nodes[k][1];
            fv[k] = Math.Sqrt(x * x + y * y);
        }
        return new MeshData
        {
            Name = "Helicoid (3 turns, 6400 quads)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields = { ["radius"] = new FieldData { Name = "radius", IsVector = false, ScalarValues = fv } },
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // 7. Catenoid — the only minimal surface of revolution besides the
    //    plane.
    // ───────────────────────────────────────────────────────────────────
    public static MeshData GenCatenoid()
    {
        int nu = 96, nv = 48;
        double a = 0.5;
        var nodes = new List<double[]>();
        for (int j = 0; j <= nv; j++)
        for (int i = 0; i <= nu; i++)
        {
            double u = (i / (double)nu) * 2 * Math.PI;
            double v = -1.5 + (j / (double)nv) * 3.0;
            double r = a * Math.Cosh(v);
            double x = r * Math.Cos(u);
            double y = r * Math.Sin(u);
            double z = a * v;
            nodes.Add(new[] { x, y, z });
        }
        int Id(int i, int j) => j * (nu + 1) + i;
        var elements = new List<Element>();
        for (int j = 0; j < nv; j++)
        for (int i = 0; i < nu; i++)
        {
            elements.Add(new Element
            {
                Type = ElementType.Quad4,
                Conn = new[] { Id(i, j), Id(i + 1, j), Id(i + 1, j + 1), Id(i, j + 1) },
            });
        }
        var fv = new double[nodes.Count];
        for (int k = 0; k < nodes.Count; k++) fv[k] = nodes[k][2];
        return new MeshData
        {
            Name = "Catenoid (4608 quads)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields = { ["z"] = new FieldData { Name = "z", IsVector = false, ScalarValues = fv } },
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // 8. Boy's surface — immersion of the real projective plane RP² ↪ R³,
    //    using Bryant's parametrisation.  Self-intersecting; classical
    //    showcase for shaders that handle non-orientable surfaces.
    // ───────────────────────────────────────────────────────────────────
    public static MeshData GenBoysSurface()
    {
        int nu = 96, nv = 48;
        var nodes = new List<double[]>();
        for (int j = 0; j <= nv; j++)
        for (int i = 0; i <= nu; i++)
        {
            double u = (i / (double)nu) * 2 * Math.PI;
            double v = (j / (double)nv) * Math.PI / 2;
            double cosV = Math.Cos(v), sinV = Math.Sin(v);
            double cosU = Math.Cos(u), sinU = Math.Sin(u);
            double cos3U = Math.Cos(3 * u), sin3U = Math.Sin(3 * u);
            double denom = 2.0 - Math.Sqrt(2.0) * sinV * sin3U;
            if (Math.Abs(denom) < 1e-9) denom = 1e-9;
            double x = (Math.Sqrt(2.0) * sinV * sinV * Math.Cos(2 * u) + cosV * Math.Cos(u) * sinV) / denom;
            double y = (Math.Sqrt(2.0) * sinV * sinV * Math.Sin(2 * u) + cosV * Math.Sin(u) * sinV) / denom;
            double z = (3 * cosV * cosV) / denom;
            nodes.Add(new[] { x, y, z });
        }
        int Id(int i, int j) => j * (nu + 1) + i;
        var elements = new List<Element>();
        for (int j = 0; j < nv; j++)
        for (int i = 0; i < nu; i++)
        {
            elements.Add(new Element
            {
                Type = ElementType.Quad4,
                Conn = new[] { Id(i, j), Id(i + 1, j), Id(i + 1, j + 1), Id(i, j + 1) },
            });
        }
        var fv = new double[nodes.Count];
        for (int k = 0; k < nodes.Count; k++) fv[k] = nodes[k][2];
        return new MeshData
        {
            Name = "Boy's Surface (4608 quads)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields = { ["z"] = new FieldData { Name = "z", IsVector = false, ScalarValues = fv } },
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // 9. Spherical harmonic Y₄,₂ — a "flower" shape from the real part
    //    of the harmonic, embedded as r(θ,φ) = |Re Y_lm|.
    // ───────────────────────────────────────────────────────────────────
    public static MeshData GenSphericalHarmonic()
    {
        int nu = 96, nv = 96;
        var nodes = new List<double[]>();
        // Y₄,₂ ∝ sin²θ (7 cos²θ - 1) cos(2φ)
        double Y(double th, double ph)
        {
            double s = Math.Sin(th), c = Math.Cos(th);
            return s * s * (7 * c * c - 1) * Math.Cos(2 * ph);
        }
        for (int j = 0; j <= nv; j++)
        for (int i = 0; i <= nu; i++)
        {
            double th = (j / (double)nv) * Math.PI;
            double ph = (i / (double)nu) * 2 * Math.PI;
            double r = 0.7 + 0.6 * Math.Abs(Y(th, ph));
            double x = r * Math.Sin(th) * Math.Cos(ph);
            double y = r * Math.Sin(th) * Math.Sin(ph);
            double z = r * Math.Cos(th);
            nodes.Add(new[] { x, y, z });
        }
        int Id(int i, int j) => j * (nu + 1) + i;
        var elements = new List<Element>();
        for (int j = 0; j < nv; j++)
        for (int i = 0; i < nu; i++)
        {
            elements.Add(new Element
            {
                Type = ElementType.Quad4,
                Conn = new[] { Id(i, j), Id(i + 1, j), Id(i + 1, j + 1), Id(i, j + 1) },
            });
        }
        var fv = new double[nodes.Count];
        for (int k = 0; k < nodes.Count; k++)
        {
            int i = k % (nu + 1);
            int j = k / (nu + 1);
            double th = (j / (double)nv) * Math.PI;
            double ph = (i / (double)nu) * 2 * Math.PI;
            fv[k] = Y(th, ph);
        }
        return new MeshData
        {
            Name = "Spherical Harmonic Y₄₂ (9216 quads)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields = { ["Y₄₂"] = new FieldData { Name = "Y₄₂", IsVector = false, ScalarValues = fv } },
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // 10. DNA-like double helix — two intertwined tubes connected by
    //     "rungs" (Bar2 elements) representing the base pairs.
    // ───────────────────────────────────────────────────────────────────
    public static MeshData GenDoubleHelix()
    {
        int nu = 240, nv = 12;
        double tubeR = 0.10, helixR = 0.55, pitch = 0.18;
        var nodes = new List<double[]>();
        var elements = new List<Element>();
        // Two helical tubes (strand A and strand B) with a 180° offset.
        int[] strandStart = new int[2];
        for (int s = 0; s < 2; s++)
        {
            strandStart[s] = nodes.Count;
            double phase = s * Math.PI;
            for (int i = 0; i <= nu; i++)
            {
                double u = (i / (double)nu) * 6 * Math.PI;
                double cx = helixR * Math.Cos(u + phase);
                double cy = helixR * Math.Sin(u + phase);
                double cz = u * pitch;
                // Tangent to the helix.
                double tx = -helixR * Math.Sin(u + phase);
                double ty =  helixR * Math.Cos(u + phase);
                double tz =  pitch;
                double tl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                tx /= tl; ty /= tl; tz /= tl;
                // Build a frame: pick a normal not parallel to T.
                double[] up = { 0, 0, 1 };
                if (Math.Abs(tz) > 0.9) up = new[] { 1.0, 0.0, 0.0 };
                double nx = up[1] * tz - up[2] * ty;
                double ny = up[2] * tx - up[0] * tz;
                double nz = up[0] * ty - up[1] * tx;
                double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                nx /= nl; ny /= nl; nz /= nl;
                double bx = ty * nz - tz * ny;
                double by = tz * nx - tx * nz;
                double bz = tx * ny - ty * nx;
                for (int j = 0; j < nv; j++)
                {
                    double v = (j / (double)nv) * 2 * Math.PI;
                    double cv = Math.Cos(v) * tubeR, sv = Math.Sin(v) * tubeR;
                    nodes.Add(new[]
                    {
                        cx + cv * nx + sv * bx,
                        cy + cv * ny + sv * by,
                        cz + cv * nz + sv * bz,
                    });
                }
            }
            int s0 = strandStart[s];
            int Idx(int i, int j) => s0 + (i % (nu + 1)) * nv + (j % nv);
            for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                elements.Add(new Element
                {
                    Type = ElementType.Quad4,
                    Conn = new[] { Idx(i, j), Idx(i + 1, j), Idx(i + 1, j + 1), Idx(i, j + 1) },
                });
            }
        }
        // Bar2 "rungs" between strand centres at every 6th step — drop a
        // single representative node per strand at each rung index (we
        // index into the start of each cross-section ring).
        for (int i = 0; i <= nu; i += 6)
        {
            int a = strandStart[0] + i * nv;
            int b = strandStart[1] + i * nv;
            elements.Add(new Element { Type = ElementType.Bar2, Conn = new[] { a, b } });
        }
        var fv = new double[nodes.Count];
        for (int k = 0; k < nodes.Count; k++) fv[k] = nodes[k][2];
        return new MeshData
        {
            Name = "Double Helix (5760 quads + rungs)", Dim = 3,
            Nodes = nodes.ToArray(), Elements = elements,
            Fields = { ["z"] = new FieldData { Name = "z", IsVector = false, ScalarValues = fv } },
        };
    }

    public static IEnumerable<MeshData> All() => new[]
    {
        GenGeodesicSphere(),
        GenTorus(),
        GenTrefoilKnot(),
        GenMobiusStrip(),
        GenKleinBottle(),
        GenHelicoid(),
        GenCatenoid(),
        GenBoysSurface(),
        GenSphericalHarmonic(),
        GenDoubleHelix(),
    };
}
