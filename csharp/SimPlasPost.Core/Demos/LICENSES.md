# Demo mesh provenance & licensing

The post-processor itself (and all source code in this repository) is
licensed under the **GNU General Public License v3.0 or later** (GPLv3+).
See `COPYING` / the per-file headers for the canonical text.

This document covers the **demo data** the application ships with so users
can immediately exercise the renderer without loading their own mesh.

---

## Procedurally generated demos (all of them, currently)

Every demo currently shipped is **procedurally generated in C#** at
runtime — no external data file is bundled in this commit.  The two
generator files are:

* `csharp/SimPlasPost.Core/Demos/DemoMeshGenerator.cs`
  — small canonical FE meshes (plate-with-hole, beams, mixed
  Tri/Quad/Tet/Hex/Wedge, all-element-types showcase, per-element
  field demo).
* `csharp/SimPlasPost.Core/Demos/HighEndMeshGenerator.cs`
  — ten high-vertex-count parametric surfaces from classical
  differential geometry (geodesic sphere, torus, trefoil knot,
  Möbius strip, Klein bottle, helicoid, catenoid, Boy's surface,
  spherical-harmonic Y₄₂ blob, double-helix tube + bars).

Because these meshes are **mathematical functions evaluated in source
code**, no third-party licence applies to the data itself; they inherit
the **GPLv3+** licence of the source.

The mathematical objects they represent (Möbius strip, Klein bottle,
trefoil knot, etc.) are part of the public-domain heritage of geometry
and carry no IP encumbrance.

---

## Loading real classical meshes (Bunny, Buddha, Armadillo, …)

This commit also adds an OBJ parser
(`csharp/SimPlasPost.Core/Parsers/ObjParser.cs`) that loads any
Wavefront `.obj` file the user provides via the existing **JSON mesh**
file picker (with a small wrapper) or by invoking
`ObjParser.Parse(text)` programmatically.

If you want to ship real datasets like the **Stanford 3D Scanning
Repository** meshes (Bunny, Happy Buddha, Dragon, Armadillo, Lucy,
XYZ RGB Statuette, Asian Dragon, Thai Statue, …), each carries its own
licence; the table below summarises the typical terms.  **You must read
and comply with the original licence before redistribution.**

| Dataset | Source | Licence (summary — NOT legal advice) |
|---|---|---|
| Stanford Bunny | Stanford 3D Scanning Repository | Free for any purpose, attribution requested |
| Happy Buddha | Stanford 3D Scanning Repository | Free, with attribution; cite Stanford |
| Dragon | Stanford 3D Scanning Repository | Free, with attribution; cite Stanford |
| Armadillo | Stanford 3D Scanning Repository | Free, with attribution; cite Stanford |
| Lucy | Stanford 3D Scanning Repository | Free, with attribution; cite Stanford and Carnegie Mellon |
| XYZ RGB Asian Dragon / Statuette / Thai Statue | XYZ RGB Inc. (via Stanford repo) | Free for **non-commercial** use only; written permission required for commercial use |
| Utah Teapot | Martin Newell, U. of Utah, 1975 | Effectively public domain |
| Spot the Cow | Keenan Crane | CC0 — public domain dedication |
| Stanford Lounge / Cornell Box geometry | Stanford / Cornell graphics labs | Educational use; cite source |

The recommended attribution string for Stanford-repository meshes is:

> Geometry from the Stanford 3D Scanning Repository,
> http://graphics.stanford.edu/data/3Dscanrep/

---

## How to add a real mesh later

1. Place the `.obj` file under `csharp/SimPlasPost.Core/Demos/Embedded/`.
2. In the project file, mark it as
   `<EmbeddedResource Include="Demos/Embedded/your-mesh.obj.gz" />`.
3. Add a generator method in `HighEndMeshGenerator` (or a sibling class)
   that loads the resource via
   `Assembly.GetManifestResourceStream(...)` + `GZipStream` + a
   `StreamReader`, calls `ObjParser.Parse(text, name)`, optionally adds
   a synthetic scalar field, and returns the `MeshData`.
4. Append it to `DemoMeshGenerator.AllDemos()`.
5. Update **this file** with the dataset's source URL and licence.
