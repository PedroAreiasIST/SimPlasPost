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
                // The full Ensight syntax is `model: [ts] [fs] file [opts...]`
                // where `ts` and `fs` are optional integer time-set and
                // file-set IDs.  Skip every leading numeric token so the
                // first non-numeric token is taken as the geometry filename
                // (matching the same `\d+\s+){0,2}` allowance the variable
                // parser below already uses).  Trailing options like
                // `change_coords_only` are ignored — we just take the first
                // non-numeric token.
                var parts = Regex.Replace(line, @"^model:\s*", "", RegexOptions.IgnoreCase)
                    .Trim()
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                int startIdx = 0;
                while (startIdx < parts.Length && Regex.IsMatch(parts[startIdx], @"^\d+$"))
                    startIdx++;
                if (startIdx < parts.Length) result.GeoFile = parts[startIdx];
            }

            if (section == "v")
            {
                // Accept the optional time-set and file-set IDs that real Ensight
                // case files often carry:
                //   scalar per node:           Temperature temp_****.scl
                //   scalar per node: 1         Temperature temp_****.scl
                //   scalar per node: 1 1       Temperature temp_****.scl
                var m = Regex.Match(line,
                    @"^(scalar|vector)\s+per\s+(node|element):\s*(?:\d+\s+){0,2}(\S+)\s+(\S+)",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    result.Variables.Add(new EnsightVariable
                    {
                        VType = m.Groups[1].Value.ToLower(),
                        Location = m.Groups[2].Value.ToLower(),
                        Name = m.Groups[3].Value,
                        File = m.Groups[4].Value,
                    });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Parse an Ensight ASCII geometry file.  Supports both formats found
    /// in the wild:
    ///
    ///   • Ensight Gold — `part / N / desc / coordinates / NPTS / x[] y[] z[]`
    ///     blocks (one coordinate column at a time, one number per line).
    ///   • Ensight 6 — a single global `coordinates / NPTS` block before
    ///     any `part` header, with `[id] x y z` per line.  Element blocks
    ///     follow inside `part N / desc / type / NELEM` sections, with the
    ///     element id (when present) inlined on the connectivity line.
    ///
    /// The format is detected from the file structure: if `coordinates`
    /// appears before any `part` header, we treat the file as Ensight 6.
    /// </summary>
    public static MeshData ParseGeo(string text)
    {
        var lines = text.Split('\n');
        int i = 0;
        string Next() => (i < lines.Length ? lines[i++] : "").Trim();
        string Peek() => (i < lines.Length ? lines[i].Trim() : "");

        Next(); Next(); // 2 description lines

        // Read 'node id ...' / 'element id ...' directives.  Both are
        // expected; some writers swap their order, so accept either.
        bool nidGiven = false, eidGiven = false;
        for (int g = 0; g < 4 && i < lines.Length; g++)
        {
            var p = Peek().ToLower();
            if (p.StartsWith("node id"))         { nidGiven |= Next().ToLower().Contains("given"); }
            else if (p.StartsWith("element id")) { eidGiven |= Next().ToLower().Contains("given"); }
            else break;
        }

        // Optional 'extents' block.  Skip the keyword and up to 6 numeric
        // lines, stopping at the first known keyword.
        if (Peek().StartsWith("extents", StringComparison.OrdinalIgnoreCase))
        {
            Next();
            for (int g = 0; g < 6 && i < lines.Length; g++)
            {
                var p = Peek().ToLower();
                if (p.StartsWith("part") || p.StartsWith("coordinates")) break;
                Next();
            }
        }

        var allNodes = new List<double[]>();
        var allElements = new List<Element>();

        // Detect Ensight 6: a global 'coordinates' block before any 'part'.
        bool isE6 = Peek().StartsWith("coordinates", StringComparison.OrdinalIgnoreCase);
        if (isE6)
        {
            Next(); // 'coordinates'
            if (int.TryParse(Next(), out int npts) && npts > 0 && npts <= MaxNodesPerPart)
            {
                // Custom Ensight writers sometimes mix the two ASCII
                // layouts: a global "coordinates" block (Ensight 6
                // placement) but numbers laid out per-axis, one value per
                // line (Gold-style — all X first, then all Y, then all Z).
                // The strict Ensight 6 reader expects "id x y z" or
                // "x y z" per line.  Detect which by peeking at the first
                // post-count line: a single token = per-axis; 3 or 4
                // tokens = per-line.
                //
                // For the per-axis variant we honour `node id given`
                // (IDs on their own lines BEFORE the coordinate columns,
                // exactly like Gold's per-part path).  For the per-line
                // variant the existing auto-detect on token count
                // (`parts.Length >= 4`) keeps handling inline IDs.
                var firstTokens = Peek().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                bool perLineXYZ = firstTokens.Length >= 3;

                if (perLineXYZ)
                {
                    for (int k = 0; k < npts; k++)
                    {
                        var parts = Next().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        int off = (parts.Length >= 4) ? 1 : 0; // tolerate inline node id even when 'off'
                        double rx = parts.Length > off     && double.TryParse(parts[off],     out var vx) ? vx : 0;
                        double ry = parts.Length > off + 1 && double.TryParse(parts[off + 1], out var vy) ? vy : 0;
                        double rz = parts.Length > off + 2 && double.TryParse(parts[off + 2], out var vz) ? vz : 0;
                        allNodes.Add(new[] { rx, ry, rz });
                    }
                }
                else
                {
                    if (nidGiven)
                        for (int k = 0; k < npts; k++) Next();
                    var x = new double[npts];
                    var y = new double[npts];
                    var z = new double[npts];
                    for (int k = 0; k < npts; k++) x[k] = double.TryParse(Next(), out var v) ? v : 0;
                    for (int k = 0; k < npts; k++) y[k] = double.TryParse(Next(), out var v) ? v : 0;
                    for (int k = 0; k < npts; k++) z[k] = double.TryParse(Next(), out var v) ? v : 0;
                    for (int k = 0; k < npts; k++) allNodes.Add(new[] { x[k], y[k], z[k] });
                }
            }
        }

        while (i < lines.Length)
        {
            // Find next 'part' header.  Accept both Gold ('part' alone with
            // the number on the next line) and Ensight 6 ('part N' on one
            // line).  Lines that aren't part headers are skipped.
            var raw = Peek();
            var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || !tokens[0].Equals("part", StringComparison.OrdinalIgnoreCase))
            {
                i++; continue;
            }
            i++; // consume the 'part' line
            if (tokens.Length == 1) Next(); // 'part' alone → next line is the number
            Next(); // part description

            int nodeBase = isE6 ? 0 : allNodes.Count;

            // Optional per-part 'coordinates' block (Gold).  In Ensight 6
            // the global block already populated allNodes.
            if (Peek().StartsWith("coordinates", StringComparison.OrdinalIgnoreCase))
            {
                Next();
                if (int.TryParse(Next(), out int npts) && npts > 0)
                {
                    if (npts > MaxNodesPerPart)
                        throw new InvalidDataException($"Part has {npts} nodes, exceeds safety limit of {MaxNodesPerPart}");

                    // Auto-detect the numeric layout from the first content
                    // line, the same way the global-coords path does.  This
                    // catches Gold-style per-part placement that
                    // nevertheless writes "id x y z" (or "x y z") per line
                    // — a hybrid some custom Ensight writers emit.  3+
                    // tokens on the first line ⇒ per-line XYZ; 1–2 ⇒
                    // per-axis columns (the strict Gold layout).
                    //
                    // For the per-axis branch we honour `node id given`
                    // (IDs precede the X column on their own lines).  For
                    // the per-line branch the inline-id auto-detect on
                    // each line covers both `nidGiven` and `nid off`.
                    var firstTokens = Peek().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    bool perLineXYZ = firstTokens.Length >= 3;

                    if (perLineXYZ)
                    {
                        for (int k = 0; k < npts; k++)
                        {
                            var parts = Next().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                            int off = (parts.Length >= 4) ? 1 : 0;
                            double rx = parts.Length > off     && double.TryParse(parts[off],     out var vx) ? vx : 0;
                            double ry = parts.Length > off + 1 && double.TryParse(parts[off + 1], out var vy) ? vy : 0;
                            double rz = parts.Length > off + 2 && double.TryParse(parts[off + 2], out var vz) ? vz : 0;
                            allNodes.Add(new[] { rx, ry, rz });
                        }
                    }
                    else
                    {
                        if (nidGiven)
                            for (int k = 0; k < npts; k++) Next();
                        var x = new double[npts];
                        var y = new double[npts];
                        var z = new double[npts];
                        for (int k = 0; k < npts; k++) x[k] = double.TryParse(Next(), out var v) ? v : 0;
                        for (int k = 0; k < npts; k++) y[k] = double.TryParse(Next(), out var v) ? v : 0;
                        for (int k = 0; k < npts; k++) z[k] = double.TryParse(Next(), out var v) ? v : 0;
                        for (int k = 0; k < npts; k++) allNodes.Add(new[] { x[k], y[k], z[k] });
                    }
                }
            }

            // Element blocks
            while (i < lines.Length)
            {
                var peek = Peek().ToLower();
                if (string.IsNullOrEmpty(peek)) { i++; continue; }
                if (peek.StartsWith("part")) break;

                // Element-type line may carry trailing whitespace or comments;
                // first whitespace-delimited token is the type.
                var etypeTok = Next().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var etype = etypeTok.Length > 0 ? etypeTok[0].ToLower() : "";
                if (!FaceTable.EnsightNpn.ContainsKey(etype)) break;

                if (!int.TryParse(Next(), out int ne) || ne <= 0) break;

                // Gold: element ids live on their own lines BEFORE connectivity.
                // Ensight 6: element ids are inlined on the connectivity line.
                if (eidGiven && !isE6)
                    for (int k = 0; k < ne; k++) Next();

                int npe = FaceTable.EnsightNpn[etype];
                int corners = FaceTable.CornerCount.GetValueOrDefault(etype, npe);

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
                    // Inline element id present whenever the connectivity line
                    // has one more token than the element's node count.  This
                    // covers Ensight 6 unconditionally and tolerates Gold
                    // writers that sneak an id in even when 'element id off'.
                    int off = (parts.Length == npe + 1) ? 1 : 0;
                    var conn = new int[corners];
                    for (int c = 0; c < corners && c + off < parts.Length; c++)
                        conn[c] = int.TryParse(parts[c + off], out var n) ? n - 1 + nodeBase : nodeBase;
                    allElements.Add(new Element { Type = mapped, Conn = conn });
                }
            }
        }

        return new MeshData { Nodes = allNodes.ToArray(), Elements = allElements };
    }

    /// <summary>
    /// Parse an Ensight ASCII scalar variable file.  Supports both:
    ///   • Gold: `description / part / N / coordinates / v1 v2 ... vN`
    ///     (one value per line).
    ///   • Ensight 6: `description / v1 v2 v3 ...` (any whitespace-
    ///     separated layout, typically 6 values per line).
    /// Detection is by presence of the `part` keyword.
    /// </summary>
    public static double[] ParseScalar(string text, int nNodes)
    {
        var lines = text.Split('\n');

        int partIdx = -1;
        for (int k = 1; k < lines.Length; k++)
        {
            if (lines[k].Trim().StartsWith("part", StringComparison.OrdinalIgnoreCase))
            {
                partIdx = k;
                break;
            }
        }

        if (partIdx >= 0)
        {
            // Gold layout: skip the 'part N' / 'coordinates' headers and
            // read one value per line until we have nNodes or hit a
            // non-numeric line.
            int i = partIdx + 1;
            // 'part' may or may not have the number on the same line; tolerate both.
            var partTok = lines[partIdx].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (partTok.Length == 1 && i < lines.Length) i++; // part number on its own line
            if (i < lines.Length && lines[i].Trim().StartsWith("coordinates", StringComparison.OrdinalIgnoreCase)) i++;

            var goldVals = new List<double>(nNodes);
            while (i < lines.Length && goldVals.Count < nNodes)
            {
                if (double.TryParse(lines[i].Trim(), out var v)) { goldVals.Add(v); i++; }
                else break;
            }
            return goldVals.ToArray();
        }

        // Ensight 6 / fallback: all whitespace-separated tokens are values,
        // skipping the description line.
        var vals = new List<double>(nNodes);
        for (int k = 1; k < lines.Length && vals.Count < nNodes; k++)
        {
            var tokens = lines[k].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var tok in tokens)
            {
                if (vals.Count >= nNodes) break;
                if (double.TryParse(tok, out var v)) vals.Add(v);
            }
        }
        return vals.ToArray();
    }

    /// <summary>
    /// Parse an Ensight ASCII vector variable file.  Supports both:
    ///   • Gold: separate vx[], vy[], vz[] columns inside a `part /
    ///     coordinates` block.
    ///   • Ensight 6: interleaved (vx, vy, vz) tuples per node, no part
    ///     header.  Whitespace layout is otherwise free.
    /// Detection is by presence of the `part` keyword.
    /// </summary>
    public static double[][] ParseVector(string text, int nNodes)
    {
        var lines = text.Split('\n');

        int partIdx = -1;
        for (int k = 1; k < lines.Length; k++)
        {
            if (lines[k].Trim().StartsWith("part", StringComparison.OrdinalIgnoreCase))
            {
                partIdx = k;
                break;
            }
        }

        var result = new double[nNodes][];

        if (partIdx >= 0)
        {
            int i = partIdx + 1;
            var partTok = lines[partIdx].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (partTok.Length == 1 && i < lines.Length) i++;
            if (i < lines.Length && lines[i].Trim().StartsWith("coordinates", StringComparison.OrdinalIgnoreCase)) i++;

            var vx = new double[nNodes];
            var vy = new double[nNodes];
            var vz = new double[nNodes];
            for (int k = 0; k < nNodes && i < lines.Length; k++, i++)
                vx[k] = double.TryParse(lines[i].Trim(), out var v) ? v : 0;
            for (int k = 0; k < nNodes && i < lines.Length; k++, i++)
                vy[k] = double.TryParse(lines[i].Trim(), out var v) ? v : 0;
            for (int k = 0; k < nNodes && i < lines.Length; k++, i++)
                vz[k] = double.TryParse(lines[i].Trim(), out var v) ? v : 0;

            for (int k = 0; k < nNodes; k++)
                result[k] = new[] { vx[k], vy[k], vz[k] };
            return result;
        }

        // Ensight 6: tokenise the whole file (after the description) and
        // pull (vx, vy, vz) triplets in order.
        var allVals = new List<double>(3 * nNodes);
        for (int k = 1; k < lines.Length && allVals.Count < 3 * nNodes; k++)
        {
            var tokens = lines[k].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var tok in tokens)
            {
                if (allVals.Count >= 3 * nNodes) break;
                if (double.TryParse(tok, out var v)) allVals.Add(v);
            }
        }
        for (int k = 0; k < nNodes; k++)
        {
            int b = k * 3;
            result[k] = new[]
            {
                b     < allVals.Count ? allVals[b]     : 0,
                b + 1 < allVals.Count ? allVals[b + 1] : 0,
                b + 2 < allVals.Count ? allVals[b + 2] : 0,
            };
        }
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

        // Find geo file.  When the case file uses a transient pattern
        // (`model: 1 foo.geo****`), expand the wildcard against the
        // already-loaded file keys; the viewer only needs a single snapshot.
        string? geoFileName = caseData.GeoFile;
        if (geoFileName != null && geoFileName.Contains('*'))
        {
            string escGeo = Regex.Escape(geoFileName);
            string patGeo = Regex.Replace(escGeo, @"(?:\\\*)+", @"\d+");
            var rxGeo = new Regex("^" + patGeo + "$", RegexOptions.IgnoreCase);
            geoFileName = fileContents.Keys
                .Where(n => rxGeo.IsMatch(n))
                .OrderBy(n => n, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        if (geoFileName == null || !fileContents.ContainsKey(geoFileName))
        {
            // Last resort: any file that *looks* like a geo, including the
            // transient `.geoNNNN` form that doesn't end in a clean `.geo`.
            geoFileName = fileContents.Keys.FirstOrDefault(n =>
                    n.EndsWith(".geo", StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith(".geom", StringComparison.OrdinalIgnoreCase))
                ?? fileContents.Keys.FirstOrDefault(n =>
                    Regex.IsMatch(n, @"\.geo\d+$", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(n, @"\.geom\d+$", RegexOptions.IgnoreCase));
            if (geoFileName == null)
                throw new InvalidDataException("Geo file not found in uploaded files");
        }

        var mesh = ParseGeo(fileContents[geoFileName]);
        mesh.Name = caseFile;
        mesh.Dim = 3;

        // Drop any element whose connectivity references a node index outside
        // [0, Nodes.Length).  Out-of-range indices crash the renderer when it
        // dereferences `nodes[conn[c]]`; this can happen if the geo file uses
        // a layout we mis-detect or has a malformed line.  Better to silently
        // discard the bad element than to abort the process at render time.
        if (mesh.Nodes.Length > 0)
        {
            int n = mesh.Nodes.Length;
            mesh.Elements = mesh.Elements
                .Where(el => el.Conn != null && el.Conn.All(c => c >= 0 && c < n))
                .ToList();
        }
        else
        {
            mesh.Elements.Clear();
        }

        // Load variables — when the file pattern contains '*', every matching
        // file is loaded as a separate time step instead of just the last one.
        int maxSteps = 1;
        foreach (var v in caseData.Variables)
        {
            string fn = v.File;
            List<string> stepFiles;

            if (fn.Contains('*'))
            {
                // Collapse any run of `*` into a single `\d+` so a four-star
                // pattern like `foo.res****` matches `foo.res0001` cleanly
                // instead of forcing the regex engine to backtrack across
                // four greedy `(\d+)` groups.
                string esc = Regex.Escape(fn);
                string pat = Regex.Replace(esc, @"(?:\\\*)+", @"\d+");
                var pattern = new Regex("^" + pat + "$");
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
