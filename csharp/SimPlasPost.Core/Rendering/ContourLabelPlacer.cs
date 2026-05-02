using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Rendering;

/// <summary>
/// Places contour-line value labels in screen space.  Used by both the
/// on-screen overlay and the PDF exporter so the two renderers agree on
/// which iso-lines get labelled and where (modulo small differences in the
/// underlying glyph metrics — we estimate text width from font size and
/// average character width to keep both renderers in sync).
///
/// Algorithm per call:
///   1. For each <see cref="ContourLabelWorld"/> candidate, project the
///      anchor and a tangent point through the supplied camera into screen
///      space.
///   2. Reject anchors that fall outside the safe interior of the viewport.
///   3. Reject candidates whose projected tangent is shorter than ~2 px
///      (iso-line edge-on, label would foreshorten to a point).
///   4. Derive screen rotation from the projected tangent and flip it if it
///      would render the text upside-down (|angle| &gt; π/2).
///   5. Sort surviving candidates by source polyline arc length descending
///      so prominent iso-lines win the placement race.
///   6. Greedy non-overlap: keep a label only if its conservative
///      rotation-invariant AABB (radius √(w²+h²)/2 around the anchor)
///      doesn't intersect any already-placed label, with a small margin.
/// </summary>
public static class ContourLabelPlacer
{
    /// <param name="margin">Extra pixels added to label width/height when
    /// computing the overlap rectangle (separates adjacent labels).</param>
    /// <param name="avgCharWidthFraction">Fraction of font size used as the
    /// per-character width estimate.  0.55 matches Times-Bold reasonably
    /// well and is what the PDF exporter uses elsewhere.</param>
    public static List<PlacedContourLabel> Place(
        List<ContourLabelWorld> candidates,
        CameraState cam, double orthoHalfHeight,
        int viewportW, int viewportH,
        double fontSize,
        double margin = 6,
        double avgCharWidthFraction = 0.55)
    {
        var placed = new List<PlacedContourLabel>();
        if (candidates.Count == 0 || viewportW < 4 || viewportH < 4) return placed;

        double avgCharW = fontSize * avgCharWidthFraction;
        var placedBoxes = new List<(double x, double y, double w, double h)>();

        // Sort by polyline length descending (longer iso-lines preempt shorter
        // ones in case of overlap).  Materialised so we don't churn the input.
        var ordered = candidates.OrderByDescending(c => c.Length).ToList();
        foreach (var cand in ordered)
        {
            var center = Camera.Project(cand.Pos, cam, orthoHalfHeight, viewportW, viewportH);
            double cx = center[0], cy = center[1];
            // Cull anchors near the viewport edges so the rotated label
            // doesn't run off the page.
            if (cx < 16 || cx > viewportW - 16 || cy < 16 || cy > viewportH - 16) continue;

            const double tEps = 0.05;
            var tipWorld = new[]
            {
                cand.Pos[0] + cand.TangentDir[0] * tEps,
                cand.Pos[1] + cand.TangentDir[1] * tEps,
                cand.Pos[2] + cand.TangentDir[2] * tEps,
            };
            var tip = Camera.Project(tipWorld, cam, orthoHalfHeight, viewportW, viewportH);
            double dx = tip[0] - cx, dy = tip[1] - cy;
            if (dx * dx + dy * dy < 4.0) continue;

            double angle = Math.Atan2(dy, dx);
            if (angle > Math.PI / 2) angle -= Math.PI;
            else if (angle < -Math.PI / 2) angle += Math.PI;

            double textW = (cand.Text?.Length ?? 0) * avgCharW + margin;
            double textH = fontSize * 1.2 + 2;
            // Rotation-invariant AABB: any orientation of the label fits
            // inside a square of side = diagonal of the unrotated box.
            double diag = Math.Sqrt(textW * textW + textH * textH);
            double bx = cx - diag / 2, by = cy - diag / 2;
            bool overlap = false;
            foreach (var p in placedBoxes)
            {
                if (bx < p.x + p.w && bx + diag > p.x &&
                    by < p.y + p.h && by + diag > p.y)
                { overlap = true; break; }
            }
            if (overlap) continue;
            placedBoxes.Add((bx, by, diag, diag));

            placed.Add(new PlacedContourLabel
            {
                X = cx, Y = cy, Angle = angle, Text = cand.Text,
            });
        }
        return placed;
    }
}
