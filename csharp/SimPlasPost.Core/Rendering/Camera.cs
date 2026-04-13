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
    /// <summary>
    /// Build camera state from rotation matrix + distance + pan.
    /// </summary>
    public static CameraState Build(CameraParams cp)
    {
        var rot = cp.Rot;

        // Extract basis vectors from rotation matrix (rows)
        var right = new Vec3(rot[0], rot[1], rot[2]);
        var up = new Vec3(rot[3], rot[4], rot[5]);
        var fwd = new Vec3(rot[6], rot[7], rot[8]);

        // Eye = target - forward * dist
        var target = new Vec3(cp.Tx, cp.Ty, 0);
        var eye = target - fwd * cp.Dist;

        return new CameraState { Eye = eye, Forward = fwd, Right = right, Up = up };
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

        if (z < 0.01) return null;

        double scale = h / (2.0 * orthoHalfHeight);
        return new[] { w / 2.0 + x * scale, h / 2.0 - y * scale, z };
    }
}
