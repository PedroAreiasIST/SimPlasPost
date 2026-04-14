using System.Text.RegularExpressions;
using SimPlasPost.Core.Geometry;
using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Parsers;

public class EnsightCaseData
{
    public string? GeoFile { get; set; }
    public List<EnsightVariable> Variables { get; set; } = new();
}

public class EnsightVariable
{
    public string VType { get; set; } = ""; // "scalar" or "vector"
    public string Location { get; set; } = ""; // "node" or "element"
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
}

/// <summary>
/// Parser for Ensight Gold ASCII format (.case, .geo, .scl, .vec files).
/// </summary>
public static class EnsightParser
{
    private const int MaxNodesPerPart = 50_000_000; // safety limit

    public static EnsightCaseData ParseCase(string text)
    {
        var result = new EnsightCaseData();
        var lines = text.Split('\n').Select(l => l.Trim()).ToArray();
        string section = "";

        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^FORMAT", RegexOptions.IgnoreCase)) { section = "f"; continue; }
            if (Regex.IsMatch(line, @"^GEOMETRY", RegexOptions.IgnoreCase)) { section = "g"; continue; }
            if (Regex.IsMatch(line, @"^VARIABLE", RegexOptions.IgnoreCase)) { section = "v"; continue; }
            if (Regex.IsMatch(line, @"^TIME", RegexOptions.IgnoreCase)) { section = "t"; continue; }

            if (section == "g" && Regex.IsMatch(line, @"^model:", RegexOptions.IgnoreCase))
            {
                var parts = Regex.Replace(line, @"^model:\s*", "", RegexOptions.IgnoreCase).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                result.GeoFile = parts.Length > 1 && Regex.IsMatch(parts[0], @"^\d+$") ? parts[1] : parts[0];
            }

            if (section == "v")
            {
                var m = Regex.Match(line, @"^(scalar|vector)\s+per\s+(node|element):\s*(?:(\d+)\s+)?(\S+)\s+(\S+)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    result.Variables.Add(new EnsightVariable
                    {
                        VType = m.Groups[1].Value.ToLower(),
                        Location = m.Groups[2].Value.ToLower(),
                        Name = m.Groups[4].Value,
                        File = m.Groups[5].Value,
                    });
                }
            }
        }

        return result;
    }

    public static MeshData ParseGeo(string text)
    {
        var lines = text.Split('\n');
        int i = 0;
        string Next() => (i < lines.Length ? lines[i++] : "").Trim();

        Next(); Next(); // description lines
        var nidLine = Next().ToLower();
        bool nidGiven = nidLine.Contains("given");
        var eidLine = Next().ToLower();
        bool eidGiven = eidLine.Contains("given");

        var allNodes = new List<double[]>();
        var allElements = new List<Element>();

        while (i < lines.Length)
        {
            var line = Next();
            if (!line.Equals("part", StringComparison.OrdinalIgnoreCase)) continue;
            Next(); // part number
            Next(); // part description

            var coordLine = Next();
            if (!coordLine.StartsWith("coordinates", StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(Next(), out int npts) || npts <= 0) continue;
            if (npts > MaxNodesPerPart) throw new InvalidDataException($"Part has {npts} nodes, exceeds safety limit of {MaxNodesPerPart}");

            if (nidGiven)
                for (int k = 0; k < npts; k++) Next();

            var x = new double[npts];
            var y = new double[npts];
            var z = new double[npts];
            for (int k = 0; k < npts; k++) x[k] = double.TryParse(Next(), out var v) ? v : 0;
            for (int k = 0; k < npts; k++) y[k] = double.TryParse(Next(), out var v) ? v : 0;
            for (int k = 0; k < npts; k++) z[k] = double.TryParse(Next(), out var v) ? v : 0;

            int nodeBase = allNodes.Count;
            for (int k = 0; k < npts; k++)
                allNodes.Add(new[] { x[k], y[k], z[k] });

            // Element blocks
            while (i < lines.Length)
            {
                var peek = (i < lines.Length ? lines[i] : "").Trim().ToLower();
                if (string.IsNullOrEmpty(peek) || peek == "part") break;

                var etype = Next().Trim().ToLower();
                if (!FaceTable.EnsightNpn.ContainsKey(etype)) break;

                if (!int.TryParse(Next(), out int ne) || ne <= 0) break;
                if (eidGiven)
                    for (int k = 0; k < ne; k++) Next();

                int npe = FaceTable.EnsightNpn[etype];
                int corners = FaceTable.CornerCount.GetValueOrDefault(etype, npe);

                // Skip bar and point elements
                if (etype is "bar2" or "bar3" or "point")
                {
                    for (int k = 0; k < ne; k++) Next();
                    continue;
                }

                if (!FaceTable.EnsightTypeMap.TryGetValue(etype, out var mapped))
                {
                    for (int k = 0; k < ne; k++) Next();
                    continue;
                }

                for (int k = 0; k < ne; k++)
                {
                    var parts = Next().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var conn = new int[corners];
                    for (int c = 0; c < corners && c < parts.Length; c++)
                        conn[c] = int.TryParse(parts[c], out var n) ? n - 1 + nodeBase : nodeBase;

                    allElements.Add(new Element { Type = mapped, Conn = conn });
                }
            }
        }

        return new MeshData { Nodes = allNodes.ToArray(), Elements = allElements };
    }

    public static double[] ParseScalar(string text, int nNodes)
    {
        var lines = text.Split('\n');
        int i = 0;
        string Next() => (i < lines.Length ? lines[i++] : "").Trim();

        Next(); // description
        var vals = new List<double>();

        while (i < lines.Length)
        {
            var l = Next().ToLower();
            if (l == "part")
            {
                Next(); Next(); // part num + "coordinates"
                while (i < lines.Length && vals.Count < nNodes)
                {
                    if (double.TryParse(lines[i].Trim(), out double v))
                    {
                        vals.Add(v);
                        i++;
                    }
                    else break;
                }
                break;
            }
        }

        return vals.ToArray();
    }

    public static double[][] ParseVector(string text, int nNodes)
    {
        var lines = text.Split('\n');
        int i = 0;
        string Next() => (i < lines.Length ? lines[i++] : "").Trim();

        Next(); // description
        var vx = new double[nNodes];
        var vy = new double[nNodes];
        var vz = new double[nNodes];

        while (i < lines.Length)
        {
            var l = Next().ToLower();
            if (l == "part")
            {
                Next(); Next();
                for (int k = 0; k < nNodes && i < lines.Length; k++)
                    vx[k] = double.TryParse(Next(), out var v) ? v : 0;
                for (int k = 0; k < nNodes && i < lines.Length; k++)
                    vy[k] = double.TryParse(Next(), out var v) ? v : 0;
                for (int k = 0; k < nNodes && i < lines.Length; k++)
                    vz[k] = double.TryParse(Next(), out var v) ? v : 0;
                break;
            }
        }

        var result = new double[nNodes][];
        for (int k = 0; k < nNodes; k++)
            result[k] = new[] { vx[k], vy[k], vz[k] };
        return result;
    }

    /// <summary>
    /// Load a complete mesh from a set of files (case + geo + variable files).
    /// </summary>
    public static MeshData LoadFromFiles(Dictionary<string, string> fileContents)
    {
        // Try to find .case file
        var caseFile = fileContents.Keys.FirstOrDefault(f => f.EndsWith(".case", StringComparison.OrdinalIgnoreCase));

        if (caseFile == null)
        {
            // No .case file — try to load .geo directly
            var geoFile = fileContents.Keys.FirstOrDefault(f =>
                f.EndsWith(".geo", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".geom", StringComparison.OrdinalIgnoreCase));

            if (geoFile == null)
                throw new InvalidDataException("No .case or .geo file found");

            var geo = ParseGeo(fileContents[geoFile]);
            geo.Name = geoFile;
            geo.Dim = 3;

            // Try to load scalar files
            foreach (var (name, content) in fileContents)
            {
                if (name == geoFile) continue;
                var vals = ParseScalar(content, geo.Nodes.Length);
                if (vals.Length == geo.Nodes.Length)
                {
                    string fieldName = Path.GetFileNameWithoutExtension(name);
                    geo.Fields[fieldName] = new FieldData
                    {
                        Name = fieldName,
                        IsVector = false,
                        ScalarValues = vals,
                    };
                }
            }

            DetectDimension(geo);
            return geo;
        }

        // Parse .case file
        var caseData = ParseCase(fileContents[caseFile]);

        // Find geo file
        string? geoFileName = caseData.GeoFile;
        if (geoFileName == null || !fileContents.ContainsKey(geoFileName))
        {
            geoFileName = fileContents.Keys.FirstOrDefault(n =>
                n.EndsWith(".geo", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith(".geom", StringComparison.OrdinalIgnoreCase));
            if (geoFileName == null)
                throw new InvalidDataException("Geo file not found in uploaded files");
        }

        var mesh = ParseGeo(fileContents[geoFileName]);
        mesh.Name = caseFile;
        mesh.Dim = 3;

        // Load variables — when the file pattern contains '*', every matching
        // file is loaded as a separate time step instead of just the last one.
        int maxSteps = 1;
        foreach (var v in caseData.Variables)
        {
            string fn = v.File;
            List<string> stepFiles;

            if (fn.Contains('*'))
            {
                var pattern = new Regex("^" + Regex.Escape(fn).Replace("\\*", @"(\d+)") + "$");
                stepFiles = fileContents.Keys
                    .Where(n => pattern.IsMatch(n))
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                stepFiles = fileContents.ContainsKey(fn)
                    ? new List<string> { fn }
                    : new List<string>();
            }

            if (stepFiles.Count == 0) continue;

            if (v.VType == "scalar")
            {
                var steps = new List<double[]>();
                foreach (var sf in stepFiles)
                {
                    var vals = ParseScalar(fileContents[sf], mesh.Nodes.Length);
                    if (vals.Length > 0) steps.Add(vals);
                }
                if (steps.Count > 0)
                {
                    mesh.Fields[v.Name] = new FieldData
                    {
                        Name = v.Name,
                        IsVector = false,
                        ScalarValues = steps[0],
                        StepScalars = steps,
                    };
                    if (steps.Count > maxSteps) maxSteps = steps.Count;
                }
            }
            else if (v.VType == "vector")
            {
                var steps = new List<double[][]>();
                foreach (var sf in stepFiles)
                {
                    var vals = ParseVector(fileContents[sf], mesh.Nodes.Length);
                    if (vals.Length > 0) steps.Add(vals);
                }
                if (steps.Count > 0)
                {
                    mesh.Fields[v.Name] = new FieldData
                    {
                        Name = v.Name,
                        IsVector = true,
                        VectorValues = steps[0],
                        StepVectors = steps,
                    };
                    if (steps.Count > maxSteps) maxSteps = steps.Count;
                }
            }
        }

        mesh.StepCount = maxSteps;
        mesh.CurrentStep = 0;
        mesh.SetCurrentStep(0);

        DetectDimension(mesh);
        return mesh;
    }

    private static void DetectDimension(MeshData mesh)
    {
        double zMin = double.MaxValue, zMax = double.MinValue;
        double xySpan = 0;
        foreach (var n in mesh.Nodes)
        {
            zMin = Math.Min(zMin, n[2]);
            zMax = Math.Max(zMax, n[2]);
            xySpan = Math.Max(xySpan, Math.Max(Math.Abs(n[0]), Math.Abs(n[1])));
        }
        if (xySpan < 1e-30) xySpan = 1;
        if (zMax - zMin < 1e-10 * xySpan)
            mesh.Dim = 2;
    }
}
