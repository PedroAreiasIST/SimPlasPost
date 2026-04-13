namespace SimPlasPost.Core.Models;

/// <summary>
/// Camera state using a 3x3 rotation matrix for arcball control.
/// No gimbal lock, uniform rotation speed, precise positioning.
/// </summary>
public class CameraParams
{
    /// <summary>Row-major 3x3 rotation matrix (view orientation).</summary>
    public double[] Rot { get; set; } = Identity();

    public double Dist { get; set; } = 3.5;
    public double Tx { get; set; }
    public double Ty { get; set; }

    public CameraParams Clone() => new()
    {
        Rot = (double[])Rot.Clone(),
        Dist = Dist, Tx = Tx, Ty = Ty,
    };

    public void CopyFrom(CameraParams other)
    {
        Array.Copy(other.Rot, Rot, 9);
        Dist = other.Dist;
        Tx = other.Tx;
        Ty = other.Ty;
    }

    /// <summary>Build rotation matrix from spherical angles (for preset views).</summary>
    public static double[] RotFromAngles(double theta, double phi)
    {
        double st = Math.Sin(theta), ct = Math.Cos(theta);
        double sp = Math.Sin(phi), cp = Math.Cos(phi);

        // Forward = -eye direction (from origin toward camera)
        // Eye direction in spherical: (sp*st, cp, sp*ct)
        // Forward = opposite: (-sp*st, -cp, -sp*ct)
        double fx = -sp * st, fy = -cp, fz = -sp * ct;

        // Up = (0,1,0), handle poles
        double ux = 0, uy = 1, uz = 0;
        double dot = fx * ux + fy * uy + fz * uz;
        if (Math.Abs(dot) > 0.99) { ux = 0; uy = 0; uz = -1; }

        // Right = normalize(forward x up)
        double rx = fy * uz - fz * uy, ry = fz * ux - fx * uz, rz = fx * uy - fy * ux;
        double rl = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        rx /= rl; ry /= rl; rz /= rl;

        // True up = right x forward
        ux = ry * fz - rz * fy; uy = rz * fx - rx * fz; uz = rx * fy - ry * fx;

        // Rotation matrix: rows are right, up, forward
        return new[] { rx, ry, rz, ux, uy, uz, fx, fy, fz };
    }

    public static CameraParams For2D() => new()
    {
        Rot = RotFromAngles(0, Math.PI / 2), Dist = 1.5,
    };

    public static CameraParams For3D() => new()
    {
        Rot = RotFromAngles(0.6, 0.8), Dist = 2.0,
    };

    public static CameraParams FromAngles(double theta, double phi, double dist) => new()
    {
        Rot = RotFromAngles(theta, phi), Dist = dist,
    };

    // ─── Matrix operations ───

    public static double[] Identity() => new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

    /// <summary>Multiply two 3x3 matrices (row-major): C = A * B.</summary>
    public static double[] Mul(double[] a, double[] b)
    {
        var c = new double[9];
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            c[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return c;
    }

    /// <summary>Rotation matrix around axis (ax,ay,az) by angle radians.</summary>
    public static double[] RotAxis(double ax, double ay, double az, double angle)
    {
        double l = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (l < 1e-12) return Identity();
        ax /= l; ay /= l; az /= l;
        double c = Math.Cos(angle), s = Math.Sin(angle), t = 1 - c;
        return new[]
        {
            t * ax * ax + c,      t * ax * ay - s * az, t * ax * az + s * ay,
            t * ax * ay + s * az, t * ay * ay + c,      t * ay * az - s * ax,
            t * ax * az - s * ay, t * ay * az + s * ax, t * az * az + c,
        };
    }

    /// <summary>
    /// Arcball rotation: map two screen positions to sphere points, return rotation.
    /// px/py are in [-1,1] normalized viewport coordinates.
    /// </summary>
    public static double[] ArcballDelta(double px0, double py0, double px1, double py1)
    {
        var a = MapToSphere(px0, py0);
        var b = MapToSphere(px1, py1);

        // Rotation axis = cross(a, b), angle = acos(dot(a, b))
        double cx = a[1] * b[2] - a[2] * b[1];
        double cy = a[2] * b[0] - a[0] * b[2];
        double cz = a[0] * b[1] - a[1] * b[0];
        double dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        dot = Math.Clamp(dot, -1, 1);
        double angle = Math.Acos(dot);

        if (angle < 1e-10) return Identity();
        return RotAxis(cx, cy, cz, angle);
    }

    private static double[] MapToSphere(double x, double y)
    {
        double r2 = x * x + y * y;
        if (r2 > 1.0)
        {
            double r = Math.Sqrt(r2);
            return new[] { x / r, y / r, 0.0 };
        }
        return new[] { x, y, Math.Sqrt(1.0 - r2) };
    }
}
