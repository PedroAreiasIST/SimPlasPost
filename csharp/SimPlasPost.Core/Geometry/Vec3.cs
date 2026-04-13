namespace SimPlasPost.Core.Geometry;

/// <summary>
/// Lightweight 3D vector with common operations for mesh processing and rendering.
/// </summary>
public readonly struct Vec3
{
    public readonly double X, Y, Z;

    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vec3 operator *(double s, Vec3 v) => new(v.X * s, v.Y * s, v.Z * s);

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public Vec3 Normalized()
    {
        double l = Length;
        return l > 1e-12 ? new Vec3(X / l, Y / l, Z / l) : new Vec3(0, 0, 1);
    }

    public override string ToString() => $"({X:F4}, {Y:F4}, {Z:F4})";
}
