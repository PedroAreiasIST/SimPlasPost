# Demo mesh provenance & licensing

The post-processor itself (and all source code in this repository) is
licensed under the **GNU General Public License v3.0 or later (GPLv3+)**.
See `COPYING` / the per-file headers for the canonical text.

This document covers the **demo data** that ships with the application
so users can immediately exercise the renderer without loading their own
mesh.

---

## Procedurally generated FE demos

Eight FE demos in `Demos/DemoMeshGenerator.cs` are generated
analytically in C#:

| Name | Element types | Source |
|---|---|---|
| Plate with Hole | Quad4 | DemoMeshGenerator.GenPlateHole |
| Mixed Patch | Tri3 + Quad4 | DemoMeshGenerator.GenMixedTriQuad |
| Cantilever | Hex8 | DemoMeshGenerator.Gen3DBeam |
| Tet Beam | Tet4 | DemoMeshGenerator.GenTetBox |
| House | Hex8 + Penta6 | DemoMeshGenerator.GenHouseHexWedge |
| Toblerone | Tet4 + Penta6 | DemoMeshGenerator.GenToblerone |
| All Element Types | Point1 + Bar2 + Tri3 + Quad4 + Tet4 + Hex8 + Penta6 | DemoMeshGenerator.GenAllElementsShowcase |
| Per-Element Beam | Hex8, per-element field | DemoMeshGenerator.GenPerElementBeam |

These inherit the source code's **GPLv3+** licence.

---

## Embedded classical reference meshes

Ten classical 3D meshes are bundled as gzipped Wavefront `.obj` files
under `Demos/Embedded/` and decompressed at runtime by
`EmbeddedMeshLoader`.  All ten were mirrored from
**Alec Jacobson's `common-3d-test-models` repository on GitHub**
(<https://github.com/alecjacobson/common-3d-test-models>), which
republishes them with their original licences intact for research /
educational use.

| File | Display name | Origin / author | Original licence |
|---|---|---|---|
| `stanford-bunny.obj.gz` | Stanford Bunny | Stanford 3D Scanning Repository (G. Turk, M. Levoy, 1994) | Free for research/non-commercial use; please cite Stanford 3D Scanning Repository, http://graphics.stanford.edu/data/3Dscanrep/ |
| `happy.obj.gz` | Happy Buddha | Stanford 3D Scanning Repository (Stanford, 1996) | Same as Bunny — please cite Stanford |
| `armadillo.obj.gz` | Stanford Armadillo | Stanford 3D Scanning Repository (B. Curless, M. Levoy, 1996) | Same as Bunny — please cite Stanford |
| `nefertiti.obj.gz` | Nefertiti | scan by Berlin's Egyptian Museum, distributed via Cosmo Wenman / Nora Al-Badri & Jan Nikolai Nelles | Released under Creative Commons by the original scanners; 3D model widely redistributed |
| `rocker-arm.obj.gz` | Rocker Arm | INRIA (commonly cited as "rocker arm" engine part) | Research-use mesh; AIM@SHAPE-style attribution requested |
| `fandisk.obj.gz` | Fandisk | Hugues Hoppe et al., "Piecewise smooth surface reconstruction" (SIGGRAPH 1994) | Research-use; cite Hoppe et al. |
| `cheburashka.obj.gz` | Cheburashka | community mesh, popularised by libigl tutorial | Public-domain / unrestricted use as far as can be determined |
| `spot.obj.gz` | Spot the Cow | **Keenan Crane** (https://www.cs.cmu.edu/~kmcrane/Projects/ModelRepository/) | **CC0 — public domain dedication** |
| `teapot.obj.gz` | Utah Teapot | **Martin Newell**, University of Utah, 1975 | Effectively **public domain** |
| `suzanne.obj.gz` | Suzanne | the Blender Foundation's mascot mesh | **CC-0** (Blender Foundation public-domain mascot) |

> ⚠ The Stanford 3D Scanning Repository licence asks that any
> redistribution credit Stanford and (where applicable) the named
> contributors.  This `LICENSES.md` constitutes that attribution; please
> retain it when redistributing the binary.

If you need to ship binaries to commercial customers and want to be
extra careful, consider:

* dropping `nefertiti.obj.gz` if you can't trace the licence chain,
* or pruning down to just the CC0-licensed Spot + Suzanne and the
  effectively-public-domain Utah Teapot.

---

## Loading additional real meshes

`csharp/SimPlasPost.Core/Parsers/ObjParser.cs` accepts any reasonable
Wavefront `.obj` file (positions + n-gon faces with optional `/vt/vn`).
To add another classical dataset:

1. Place the `.obj` file under `csharp/SimPlasPost.Core/Demos/Embedded/`,
   then `gzip -9 -k yourmesh.obj` to produce `yourmesh.obj.gz`.
2. The `.csproj`'s glob `<EmbeddedResource Include="Demos/Embedded/*.obj.gz" />`
   already picks it up — no project edit needed.
3. Add a `yield return Load("yourmesh.obj.gz", "Display Name");` line
   to `EmbeddedMeshLoader.All()`.
4. Update **this file** with the dataset's source URL and licence.
