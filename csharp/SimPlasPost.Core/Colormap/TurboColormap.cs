namespace SimPlasPost.Core.Colormap;

/// <summary>
/// Google Turbo colormap (Mikhailov, 2019).
/// High-resolution LUT with linear interpolation.
/// </summary>
public static class TurboColormap
{
    // [t, r, g, b] — 31 entries from t=0 to t=1
    private static readonly double[][] Lut =
    {
        new[] { 0.000, 0.190, 0.072, 0.232 },
        new[] { 0.033, 0.234, 0.141, 0.414 },
        new[] { 0.067, 0.266, 0.220, 0.576 },
        new[] { 0.100, 0.282, 0.310, 0.714 },
        new[] { 0.133, 0.278, 0.408, 0.827 },
        new[] { 0.167, 0.254, 0.510, 0.906 },
        new[] { 0.200, 0.210, 0.613, 0.946 },
        new[] { 0.233, 0.156, 0.710, 0.948 },
        new[] { 0.267, 0.117, 0.795, 0.910 },
        new[] { 0.300, 0.117, 0.864, 0.836 },
        new[] { 0.333, 0.172, 0.914, 0.733 },
        new[] { 0.367, 0.270, 0.948, 0.608 },
        new[] { 0.400, 0.393, 0.966, 0.478 },
        new[] { 0.433, 0.520, 0.970, 0.353 },
        new[] { 0.467, 0.643, 0.960, 0.243 },
        new[] { 0.500, 0.755, 0.936, 0.157 },
        new[] { 0.533, 0.849, 0.898, 0.098 },
        new[] { 0.567, 0.919, 0.847, 0.063 },
        new[] { 0.600, 0.964, 0.784, 0.050 },
        new[] { 0.633, 0.987, 0.711, 0.051 },
        new[] { 0.667, 0.993, 0.631, 0.060 },
        new[] { 0.700, 0.985, 0.546, 0.068 },
        new[] { 0.733, 0.966, 0.459, 0.068 },
        new[] { 0.767, 0.938, 0.373, 0.060 },
        new[] { 0.800, 0.902, 0.291, 0.047 },
        new[] { 0.833, 0.858, 0.215, 0.036 },
        new[] { 0.867, 0.806, 0.149, 0.024 },
        new[] { 0.900, 0.743, 0.098, 0.015 },
        new[] { 0.933, 0.670, 0.062, 0.010 },
        new[] { 0.967, 0.586, 0.042, 0.008 },
        new[] { 1.000, 0.480, 0.015, 0.010 },
    };

    /// <summary>
    /// Sample the Turbo colormap at parameter t in [0, 1].
    /// Returns (r, g, b) each in [0, 1].
    /// </summary>
    public static (double R, double G, double B) Sample(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);

        for (int i = 0; i < Lut.Length - 1; i++)
        {
            if (t <= Lut[i + 1][0])
            {
                double f = (t - Lut[i][0]) / (Lut[i + 1][0] - Lut[i][0]);
                return (
                    Lut[i][1] + f * (Lut[i + 1][1] - Lut[i][1]),
                    Lut[i][2] + f * (Lut[i + 1][2] - Lut[i][2]),
                    Lut[i][3] + f * (Lut[i + 1][3] - Lut[i][3])
                );
            }
        }

        var last = Lut[^1];
        return (last[1], last[2], last[3]);
    }

    /// <summary>
    /// Sample and return as a byte-packed ARGB value (0xAARRGGBB).
    /// </summary>
    public static uint SampleArgb(double t)
    {
        var (r, g, b) = Sample(t);
        return 0xFF000000u
            | ((uint)(r * 255) << 16)
            | ((uint)(g * 255) << 8)
            | (uint)(b * 255);
    }
}
