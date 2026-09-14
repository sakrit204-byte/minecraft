using SakritCraft.Core.Hashing;

namespace SakritCraft.World.Noise;

/// <summary>
/// Layered combinations of <see cref="Gradient"/>. These are the shapes the world is
/// actually built from: a single octave of noise looks like nothing in nature.
/// </summary>
public static class Fractal
{
    private const ulong OctaveStride = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// Fractional Brownian motion: octaves at doubling frequency and halving amplitude.
    /// The general-purpose terrain shape, used by continentalness and humidity.
    /// Normalised so the output stays near [-1,1] regardless of octave count.
    /// </summary>
    public static double Fbm2(ulong seed, double x, double y, int octaves,
                              double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0.0, amp = 1.0, norm = 0.0, fx = x, fy = y;
        for (int i = 0; i < octaves; i++)
        {
            sum += Gradient.Noise2(seed + (ulong)i * OctaveStride, fx, fy) * amp;
            norm += amp;
            fx *= lacunarity; fy *= lacunarity; amp *= gain;
        }
        return sum / norm;
    }

    /// <inheritdoc cref="Fbm2"/>
    public static double Fbm3(ulong seed, double x, double y, double z, int octaves,
                              double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0.0, amp = 1.0, norm = 0.0, fx = x, fy = y, fz = z;
        for (int i = 0; i < octaves; i++)
        {
            sum += Gradient.Noise3(seed + (ulong)i * OctaveStride, fx, fy, fz) * amp;
            norm += amp;
            fx *= lacunarity; fy *= lacunarity; fz *= lacunarity; amp *= gain;
        }
        return sum / norm;
    }

    /// <summary>
    /// Ridged multifractal. Folding the noise at zero and inverting turns smooth blobs
    /// into sharp crests, which is what mountain ranges and valley floors actually look
    /// like. Output is [0,1], where 1 sits on a ridge line.
    /// </summary>
    public static double Ridged2(ulong seed, double x, double y, int octaves,
                                 double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0.0, amp = 1.0, norm = 0.0, fx = x, fy = y;
        for (int i = 0; i < octaves; i++)
        {
            double n = 1.0 - Math.Abs(Gradient.Noise2(seed + (ulong)i * OctaveStride, fx, fy));
            sum += n * n * amp;
            norm += amp;
            fx *= lacunarity; fy *= lacunarity; amp *= gain;
        }
        return sum / norm;
    }

    /// <inheritdoc cref="Ridged2"/>
    public static double Ridged3(ulong seed, double x, double y, double z, int octaves,
                                 double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0.0, amp = 1.0, norm = 0.0, fx = x, fy = y, fz = z;
        for (int i = 0; i < octaves; i++)
        {
            double n = 1.0 - Math.Abs(Gradient.Noise3(seed + (ulong)i * OctaveStride, fx, fy, fz));
            sum += n * n * amp;
            norm += amp;
            fx *= lacunarity; fy *= lacunarity; fz *= lacunarity; amp *= gain;
        }
        return sum / norm;
    }

    /// <summary>
    /// Billow noise: the absolute value of fbm, giving rounded puffy forms. Used for
    /// cloud cover and for the cheese-cave mask where rounded chambers are wanted.
    /// </summary>
    public static double Billow3(ulong seed, double x, double y, double z, int octaves)
    {
        double sum = 0.0, amp = 1.0, norm = 0.0, fx = x, fy = y, fz = z;
        for (int i = 0; i < octaves; i++)
        {
            sum += Math.Abs(Gradient.Noise3(seed + (ulong)i * OctaveStride, fx, fy, fz)) * amp;
            norm += amp;
            fx *= 2.0; fy *= 2.0; fz *= 2.0; amp *= 0.5;
        }
        return sum / norm * 2.0 - 1.0;
    }

    /// <summary>
    /// Domain warp: offset the sample position by another noise field before sampling.
    /// This is the cheapest way to stop terrain looking procedurally generated, because
    /// it destroys the axis-aligned lattice signature a gradient field leaves behind.
    /// Coastlines in particular are unconvincing without it.
    /// </summary>
    public static (double X, double Y) Warp2(ulong seed, double x, double y,
                                             double frequency, double strength)
    {
        ulong sx = seed;
        ulong sy = Hash64.Mix(seed ^ 0x51ED270B7F4A7C15UL);
        double fx = x * frequency, fy = y * frequency;
        return (x + Fbm2(sx, fx, fy, 3) * strength,
                y + Fbm2(sy, fx, fy, 3) * strength);
    }

    /// <inheritdoc cref="Warp2"/>
    public static (double X, double Y, double Z) Warp3(ulong seed, double x, double y, double z,
                                                       double frequency, double strength)
    {
        ulong sx = seed;
        ulong sy = Hash64.Mix(seed ^ 0x51ED270B7F4A7C15UL);
        ulong sz = Hash64.Mix(seed ^ 0x2545F4914F6CDD1DUL);
        double fx = x * frequency, fy = y * frequency, fz = z * frequency;
        return (x + Fbm3(sx, fx, fy, fz, 3) * strength,
                y + Fbm3(sy, fx, fy, fz, 3) * strength,
                z + Fbm3(sz, fx, fy, fz, 3) * strength);
    }
}
