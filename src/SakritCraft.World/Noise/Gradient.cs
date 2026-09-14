using System.Runtime.CompilerServices;
using SakritCraft.Core.Hashing;

namespace SakritCraft.World.Noise;

/// <summary>
/// Position-hashed gradient noise in two and three dimensions.
/// <para>
/// Gradients come from hashing the integer lattice coordinate with the stream seed
/// rather than from a permutation table. Three consequences matter: the field is
/// unbounded instead of repeating at a table period, any sample can be evaluated on
/// any thread with no shared state, and the same coordinate always returns the same
/// value regardless of evaluation order.
/// </para>
/// <para>
/// All arithmetic is <see cref="double"/> with no fused multiply-add, because the
/// save format stores only the difference between generated terrain and player edits.
/// A one-bit difference between machines would corrupt worlds. See §03 of the design.
/// </para>
/// </summary>
public static class Gradient
{
    // Perlin's range is bounded by sqrt(N)/2. Scaling by the reciprocal puts the
    // practical output close to [-1,1] without clipping the rare extreme sample.
    private const double Scale2 = 1.4142135623730951;  // 2/sqrt(2)
    private const double Scale3 = 1.1547005383792515;  // 2/sqrt(3)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Floor(double v)
    {
        long i = (long)v;
        return v < i ? i - 1 : i;
    }

    /// <summary>Quintic fade. Zero first and second derivative at both ends, which is
    /// what stops the visible creases plain cubic interpolation leaves on a lattice.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Fade(double t) => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Grad2(ulong seed, long ix, long iy, double dx, double dy)
    {
        // Eight evenly spaced directions on the unit circle, selected by three bits.
        ulong h = Hash64.Coord(seed, ix, iy) & 7;
        double u = (h & 4) == 0 ? dx : dy;
        double v = (h & 4) == 0 ? dy : dx;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? 2.0 * v : -2.0 * v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Grad3(ulong seed, long ix, long iy, long iz, double dx, double dy, double dz)
    {
        // Ken Perlin's twelve edge-midpoint gradients, folded into sixteen cases.
        // Using cube edges rather than random vectors avoids the directional clumping
        // that shows up as axis-aligned streaking in fractal sums.
        ulong h = Hash64.Coord(seed, ix, iy, iz) & 15;
        double u = h < 8 ? dx : dy;
        double v = h < 4 ? dy : (h == 12 || h == 14 ? dx : dz);
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    /// <summary>Two-dimensional gradient noise, output approximately [-1,1].</summary>
    public static double Noise2(ulong seed, double x, double y)
    {
        long ix = Floor(x), iy = Floor(y);
        double fx = x - ix, fy = y - iy;
        double u = Fade(fx), v = Fade(fy);

        double x0 = Lerp(Grad2(seed, ix,     iy,     fx,       fy),
                         Grad2(seed, ix + 1, iy,     fx - 1.0, fy), u);
        double x1 = Lerp(Grad2(seed, ix,     iy + 1, fx,       fy - 1.0),
                         Grad2(seed, ix + 1, iy + 1, fx - 1.0, fy - 1.0), u);

        return Lerp(x0, x1, v) * Scale2;
    }

    /// <summary>Three-dimensional gradient noise, output approximately [-1,1].</summary>
    public static double Noise3(ulong seed, double x, double y, double z)
    {
        long ix = Floor(x), iy = Floor(y), iz = Floor(z);
        double fx = x - ix, fy = y - iy, fz = z - iz;
        double u = Fade(fx), v = Fade(fy), w = Fade(fz);

        double x00 = Lerp(Grad3(seed, ix,     iy,     iz,     fx,       fy,       fz),
                          Grad3(seed, ix + 1, iy,     iz,     fx - 1.0, fy,       fz), u);
        double x10 = Lerp(Grad3(seed, ix,     iy + 1, iz,     fx,       fy - 1.0, fz),
                          Grad3(seed, ix + 1, iy + 1, iz,     fx - 1.0, fy - 1.0, fz), u);
        double x01 = Lerp(Grad3(seed, ix,     iy,     iz + 1, fx,       fy,       fz - 1.0),
                          Grad3(seed, ix + 1, iy,     iz + 1, fx - 1.0, fy,       fz - 1.0), u);
        double x11 = Lerp(Grad3(seed, ix,     iy + 1, iz + 1, fx,       fy - 1.0, fz - 1.0),
                          Grad3(seed, ix + 1, iy + 1, iz + 1, fx - 1.0, fy - 1.0, fz - 1.0), u);

        return Lerp(Lerp(x00, x10, v), Lerp(x01, x11, v), w) * Scale3;
    }
}
