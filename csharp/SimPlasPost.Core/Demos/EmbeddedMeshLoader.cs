using System.IO.Compression;
using System.Reflection;
using SimPlasPost.Core.Models;
using SimPlasPost.Core.Parsers;

namespace SimPlasPost.Core.Demos;

/// <summary>
/// Loads the gzipped Wavefront OBJ files embedded under
/// <c>csharp/SimPlasPost.Core/Demos/Embedded/</c> as <see cref="MeshData"/>
/// instances.  Each call decompresses the resource, parses it via
/// <see cref="ObjParser"/>, then attaches a synthetic per-node scalar
/// field (height along the mesh's longest axis) so the contour-plot /
/// contour-line / contour-label pipelines all light up out of the box.
///
/// Provenance: see Demos/LICENSES.md.  All meshes are mirrored from
/// <a href="https://github.com/alecjacobson/common-3d-test-models">
/// alecjacobson/common-3d-test-models</a> on GitHub (which itself
/// republishes datasets from Stanford, Keenan Crane, Newell, and others).
/// </summary>
public static class EmbeddedMeshLoader
{
    /// <param name="resourceName">
    /// Bare filename, e.g. "stanford-bunny.obj.gz".  The full manifest
    /// resource path is computed by appending it to the assembly's
    /// embedded-resource prefix.
    /// </param>
    /// <param name="displayName">Friendly name shown in the dropdown.</param>
    /// <param name="fieldName">Label for the synthetic scalar field.</param>
    public static MeshData Load(string resourceName, string displayName, string fieldName = "Height")
    {
        var asm = typeof(EmbeddedMeshLoader).Assembly;
        // EmbeddedResource entries with logical paths "Demos/Embedded/foo.obj.gz"
        // become manifest names "<RootNamespace>.Demos.Embedded.foo.obj.gz".
        string fullName = $"SimPlasPost.Core.Demos.Embedded.{resourceName}";
        using var raw = asm.GetManifestResourceStream(fullName)
            ?? throw new FileNotFoundException(
                $"Embedded resource not found: {fullName}", resourceName);
        using var gz = new GZipStream(raw, CompressionMode.Decompress);
        using var rdr = new StreamReader(gz);
        string text = rdr.ReadToEnd();

        var mesh = ObjParser.Parse(text, displayName);

        // Synthetic scalar field: height along the mesh's longest axis.
        // For most figural meshes (Bunny, Buddha, Armadillo, …) that's
        // the vertical axis, which gives a clean colour gradient that
        // reads as "elevation" and exercises the Turbo colormap fully.
        if (mesh.Nodes.Length > 0)
        {
            double mnX = double.MaxValue, mnY = double.MaxValue, mnZ = double.MaxValue;
            double mxX = double.MinValue, mxY = double.MinValue, mxZ = double.MinValue;
            foreach (var n in mesh.Nodes)
            {
                if (n[0] < mnX) mnX = n[0]; if (n[0] > mxX) mxX = n[0];
                if (n[1] < mnY) mnY = n[1]; if (n[1] > mxY) mxY = n[1];
                if (n[2] < mnZ) mnZ = n[2]; if (n[2] > mxZ) mxZ = n[2];
            }
            double sx = mxX - mnX, sy = mxY - mnY, sz = mxZ - mnZ;
            int axis = (sy >= sx && sy >= sz) ? 1 : (sz >= sx ? 2 : 0);
            var fv = new double[mesh.Nodes.Length];
            for (int i = 0; i < mesh.Nodes.Length; i++) fv[i] = mesh.Nodes[i][axis];
            mesh.Fields[fieldName] = new FieldData
            {
                Name = fieldName, IsVector = false, ScalarValues = fv,
            };
        }
        return mesh;
    }

    /// <summary>
    /// All ten embedded classical meshes, in display order.  The Examples
    /// dropdown appends these to the small set of canonical FE demos.
    /// </summary>
    public static IEnumerable<MeshData> All()
    {
        yield return Load("stanford-bunny.obj.gz", "Stanford Bunny",      "Height");
        yield return Load("happy.obj.gz",          "Happy Buddha",        "Height");
        yield return Load("armadillo.obj.gz",      "Stanford Armadillo",  "Height");
        yield return Load("nefertiti.obj.gz",      "Nefertiti",           "Height");
        yield return Load("rocker-arm.obj.gz",     "Rocker Arm",          "Height");
        yield return Load("fandisk.obj.gz",        "Fandisk",             "Height");
        yield return Load("cheburashka.obj.gz",    "Cheburashka",         "Height");
        yield return Load("spot.obj.gz",           "Spot the Cow",        "Height");
        yield return Load("teapot.obj.gz",         "Utah Teapot",         "Height");
        yield return Load("suzanne.obj.gz",        "Suzanne (Blender)",   "Height");
    }
}
