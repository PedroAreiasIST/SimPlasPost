namespace SimPlasPost.Core.Models;

public class CameraParams
{
    public double Theta { get; set; } = 0.6;
    public double Phi { get; set; } = 0.8;
    public double Dist { get; set; } = 3.5;
    public double Tx { get; set; }
    public double Ty { get; set; }

    public CameraParams Clone() => new()
    {
        Theta = Theta, Phi = Phi, Dist = Dist, Tx = Tx, Ty = Ty
    };

    public void CopyFrom(CameraParams other)
    {
        Theta = other.Theta;
        Phi = other.Phi;
        Dist = other.Dist;
        Tx = other.Tx;
        Ty = other.Ty;
    }

    public static CameraParams For2D() => new() { Theta = 0, Phi = Math.PI / 2, Dist = 1.5 };
    public static CameraParams For3D() => new() { Theta = 0.6, Phi = 0.8, Dist = 2.0 };
}
