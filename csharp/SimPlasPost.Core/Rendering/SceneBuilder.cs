using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Geometry;
using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Rendering;

/// <summary>
/// Builds a projected 2D scene from 3D mesh data, suitable for both
/// on-screen rendering (painter's algorithm) and vector export.
/// </summary>
public static class SceneBuilder
{
    public static ExportScene? Build(
        MeshData meshData, string? activeField, bool showDef, double defScale,
        CameraParams camParams, int w, int h, DisplayMode dMode, int contourN,
        double? eMinOverride, double? eMaxOverride)
    {
        if (meshData == null) return null;

        var ns = meshData.Nodes;

        // Compute bounding box and center
        double mnX = double.MaxValue, mnY = double.MaxValue, mnZ = double.MaxValue;
        double mxX = double.MinValue, mxY = double.MinValue, mxZ = double.MinValue;
        foreach (var n in ns)
        {
            mnX = Math.Min(mnX, n[0]); mxX = Math.Max(mxX, n[0]);
            mnY = Math.Min(mnY, n[1]); mxY = Math.Max(mxY, n[1]);
            mnZ = Math.Min(mnZ, n[2]); mxZ = Math.Max(mxZ, n[2]);
        }

        double cenX = (mnX + mxX) / 2, cenY = (mnY + mxY) / 2, cenZ = (mnZ + mxZ) / 2;
        double span = Math.Max(Math.Max(mxX - mnX, mxY - mnY), Math.Max(mxZ - mnZ, 1e-12));
        double sc = 2.0 / span;

        // Determine if mesh is 3D
        bool is3D = meshData.Dim == 3 || meshData.Elements.Any(e =>
            FaceTable.Faces.TryGetValue(e.Type, out var ft) && ft.Dim == 3);

        // Compute displaced positions
        var dispField = meshData.GetDisplacementField();
        var dp = new double[ns.Length][];
        for (int i = 0; i < ns.Length; i++)
        {
            var n = ns[i];
            double dx = 0, dy = 0, dz = 0;
            if (showDef && dispField is { IsVector: true, VectorValues: not null } && i < dispField.VectorValues.Length)
            {
                var d = dispField.VectorValues[i];
                dx = d[0] * defScale; dy = d[1] * defScale; dz = d[2] * defScale;
            }
            dp[i] = new[]
            {
                (n[0] + dx - cenX) * sc,
                (n[1] + dy - cenY) * sc,
                (n[2] + dz - cenZ) * sc,
            };
        }

        // Field values and range
        double[]? fv = null;
        double fmin = 0, fmax = 1;
        if (!string.IsNullOrEmpty(activeField) && meshData.Fields.TryGetValue(activeField, out var field) && !field.IsVector)
        {
            fv = field.ScalarValues;
            if (fv != null && fv.Length > 0)
            {
                fmin = double.MaxValue; fmax = double.MinValue;
                foreach (double v in fv) { fmin = Math.Min(fmin, v); fmax = Math.Max(fmax, v); }
                if (Math.Abs(fmax - fmin) < 1e-15) fmax = fmin + 1;
            }
        }

        double efMin = eMinOverride ?? fmin;
        double efMax = eMaxOverride ?? fmax;
        double efSpan = Math.Abs(efMax - efMin) < 1e-15 ? 1 : efMax - efMin;

        var bfaces = BoundaryExtractor.Extract(meshData.Elements, is3D);
        var cam = Camera.Build(camParams);
        double orthoHH = camParams.Dist;

        var exportFaces = new List<ProjectedFace>();
        var wireEdges3D = new List<(double[] a, double[] b)>();

        foreach (var face in bfaces)
        {
            var pts = new double[face.Length][];
            for (int i = 0; i < face.Length; i++)
                pts[i] = Camera.Project(dp[face[i]], cam, orthoHH, w, h);

            var screenPts = pts.Select(p => new[] { p[0], p[1] }).ToArray();
            var pts3D = pts.Select(p => new[] { p[0], p[1], p[2] }).ToArray();
            double avgZ = pts.Average(p => p[2]);

            double r, g, b;
            if (dMode == DisplayMode.Wireframe)
            {
                r = 1; g = 1; b = 1; // plain white faces
            }
            else if (fv != null)
            {
                double avgF = face.Sum(ni => fv[ni]) / (double)face.Length;
                double t = (avgF - efMin) / efSpan;
                (r, g, b) = TurboColormap.Sample(t);
            }
            else
            {
                r = 0.75; g = 0.78; b = 0.82; // neutral gray when no field
            }

            exportFaces.Add(new ProjectedFace
            {
                ScreenPts = screenPts, Pts3D = pts3D,
                R = r, G = g, B = b, Depth = avgZ,
            });

            if (dMode == DisplayMode.Wireframe || dMode == DisplayMode.Plot)
            {
                for (int j = 0; j < pts.Length; j++)
                    wireEdges3D.Add((pts[j], pts[(j + 1) % pts.Length]));
            }
        }

        // Painter's algorithm: sort back-to-front
        exportFaces.Sort((a, b) => b.Depth.CompareTo(a.Depth));

        var zbuf = ZBufferRenderer.Build(exportFaces, w, h);
        var visibleEdges = new List<ProjectedEdge>();

        // Wireframe/plot edges
        if (dMode == DisplayMode.Wireframe || dMode == DisplayMode.Plot)
        {
            foreach (var (a, b) in wireEdges3D)
            {
                if (ZBufferRenderer.IsSegmentVisible(a, b, zbuf, w, h))
                    visibleEdges.Add(new ProjectedEdge { P1 = new[] { a[0], a[1] }, P2 = new[] { b[0], b[1] } });
            }
        }

        // Feature edges for contour-lines mode
        if (dMode == DisplayMode.Lines)
        {
            var featPos = FeatureEdgeDetector.Extract(bfaces, dp);
            for (int k = 0; k < featPos.Length; k += 6)
            {
                var a3 = new[] { featPos[k], featPos[k + 1], featPos[k + 2] };
                var b3 = new[] { featPos[k + 3], featPos[k + 4], featPos[k + 5] };
                var pa = Camera.Project(a3, cam, orthoHH, w, h);
                var pb = Camera.Project(b3, cam, orthoHH, w, h);
                if (ZBufferRenderer.IsSegmentVisible(pa, pb, zbuf, w, h))
                    visibleEdges.Add(new ProjectedEdge { P1 = new[] { pa[0], pa[1] }, P2 = new[] { pb[0], pb[1] } });
            }
        }

        // Contour iso-lines
        var contours = new List<ProjectedContour>();
        if (dMode == DisplayMode.Lines && fv != null)
        {
            var rawSegs = ContourGenerator.ComputeSegments(bfaces, dp, fv, efMin, efMax, contourN);
            var smoothed = ContourGenerator.Smooth(rawSegs, 2);
            foreach (var seg in smoothed)
            {
                var pa = Camera.Project(seg.A, cam, orthoHH, w, h);
                var pb = Camera.Project(seg.B, cam, orthoHH, w, h);
                if (ZBufferRenderer.IsSegmentVisible(pa, pb, zbuf, w, h))
                {
                    double t = (seg.Level - efMin) / efSpan;
                    var (cr, cg, cb) = TurboColormap.Sample(t);
                    contours.Add(new ProjectedContour
                    {
                        P1 = new[] { pa[0], pa[1] }, P2 = new[] { pb[0], pb[1] },
                        R = cr, G = cg, B = cb,
                    });
                }
            }
        }

        // Lines mode: faces should be white
        if (dMode == DisplayMode.Lines)
        {
            foreach (var f in exportFaces) { f.R = 1; f.G = 1; f.B = 1; }
        }

        int nEdges = bfaces.Sum(f => f.Length);
        var lp = LinePreset.Auto(nEdges);

        // Wireframe mode: no color bar, no field coloring
        string? displayFieldName = dMode == DisplayMode.Wireframe ? null : activeField;

        return new ExportScene
        {
            Faces = exportFaces, VisibleEdges = visibleEdges, Contours = contours,
            Lp = lp, FieldName = displayFieldName, FMin = efMin, FMax = efMax,
            W = w, H = h, Mode = dMode, Rotation = camParams.Rot,
        };
    }
}
