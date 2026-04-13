using SimPlasPost.Core.Geometry;
using SimPlasPost.Core.Models;

namespace SimPlasPost.Core.Rendering;

public class CameraState
{
    public Vec3 Eye { get; set; }
    public Vec3 Forward { get; set; }
    public Vec3 Right { get; set; }
    public Vec3 Up { get; set; }
}

public static class Camera
{
    public static CameraState Build(CameraParams cp)
    {
        var eye = new Vec3(
            cp.Dist * Math.Sin(cp.Phi) * Math.Sin(cp.Theta) + cp.Tx,
            cp.Dist * Math.Cos(cp.Phi) + cp.Ty,
            cp.Dist * Math.Sin(cp.Phi) * Math.Cos(cp.Theta));

        var target = new Vec3(cp.Tx, cp.Ty, 0);
        var fwd = (target - eye).Normalized();
        var up = new Vec3(0, 1, 0);

        if (Math.Abs(Vec3.Dot(fwd, up)) > 0.99)
            up = new Vec3(0, 0, -1);

        var right = Vec3.Cross(fwd, up).Normalized();
        var upC = Vec3.Cross(right, fwd);

        return new CameraState { Eye = eye, Forward = fwd, Right = right, Up = upC };
    }

    /// <summary>
    /// Project a 3D vertex to screen coordinates using orthographic projection.
    /// Returns [screenX, screenY, depth] or null if behind camera.
    /// </summary>
    public static double[]? Project(double[] v, CameraState cam, double orthoHalfHeight, int w, int h)
    {
        var vtx = new Vec3(v[0], v[1], v[2]);
        var rel = vtx - cam.Eye;
        double x = Vec3.Dot(rel, cam.Right);
        double y = Vec3.Dot(rel, cam.Up);
        double z = Vec3.Dot(rel, cam.Forward);

        if (z < 0.01) return null; // behind camera

        double scale = h / (2.0 * orthoHalfHeight);
        return new[] { w / 2.0 + x * scale, h / 2.0 - y * scale, z };
    }
}
