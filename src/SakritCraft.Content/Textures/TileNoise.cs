using System.Runtime.CompilerServices;

namespace SakritCraft.Content.Textures;

/// <summary>
/// Noise that tiles seamlessly over the unit square.
/// <para>
/// Every generator here takes a frequency in cells per tile and wraps its lattice
/// coordinates modulo that frequency. Sampling at u and at u plus one therefore hits the
/// same lattice point and returns the same value, so the texture repeats without a seam.
/// A texture baked from ordinary unbounded noise shows a hard line at every tile edge,
/// which on terrain reads as a grid stamped across the landscape.
/// </para>
/// </summary>
public static class TileNoise
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(uint x, uint y, uint seed)
    {
        uint h = x * 1597334677u ^ y * 3812015801u ^ seed * 2654435761u;
        h ^= h >> 16; h *= 0x7feb352du;
        h ^= h >> 15; h *= 0x846ca68bu;
        h ^= h >> 16;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Unit(uint h) => h * (1.0f / 4294967296.0f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Wrap(int v, int period)
    {
        int m = v % period;
        return m < 0 ? m + period : m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Smooth(float t) => t * t * (3.0f - 2.0f * t);

    /// <summary>Value noise in [0,1], tiling at the given frequency.</summary>
    public static float Value(float u, float v, int frequency, uint seed)
    {
        float x = u * frequency, y = v * frequency;
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float fx = Smooth(x - ix), fy = Smooth(y - iy);

        int x0 = Wrap(ix, frequency), x1 = Wrap(ix + 1, frequency);
        int y0 = Wrap(iy, frequency), y1 = Wrap(iy + 1, frequency);

        float a = Unit(Hash((uint)x0, (uint)y0, seed));
        float b = Unit(Hash((uint)x1, (uint)y0, seed));
        float c = Unit(Hash((uint)x0, (uint)y1, seed));
        float d = Unit(Hash((uint)x1, (uint)y1, seed));

        return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
    }

    /// <summary>Layered value noise in [0,1]. Each octave doubles the frequency, so
    /// every octave still tiles at the base period.</summary>
    public static float Fbm(float u, float v, int frequency, int octaves, uint seed, float gain = 0.5f)
    {
        float sum = 0.0f, amp = 1.0f, norm = 0.0f;
        int f = frequency;
        for (int i = 0; i < octaves; i++)
        {
            sum += Value(u, v, f, seed + (uint)i * 7919u) * amp;
            norm += amp;
            amp *= gain;
            f *= 2;
        }
        return sum / norm;
    }

    /// <summary>Ridged noise in [0,1], peaking along creases. Used for bark and strata.</summary>
    public static float Ridged(float u, float v, int frequency, int octaves, uint seed)
    {
        float sum = 0.0f, amp = 1.0f, norm = 0.0f;
        int f = frequency;
        for (int i = 0; i < octaves; i++)
        {
            float n = 1.0f - MathF.Abs(Value(u, v, f, seed + (uint)i * 6271u) * 2.0f - 1.0f);
            sum += n * n * amp;
            norm += amp;
            amp *= 0.5f;
            f *= 2;
        }
        return sum / norm;
    }

    /// <summary>
    /// Cellular noise. Returns distance to the nearest feature point and to the second
    /// nearest, both normalised by cell size. The gap between them is what draws crystal
    /// edges, mud cracks and basalt columns.
    /// </summary>
    public static (float F1, float F2) Cellular(float u, float v, int frequency, uint seed)
    {
        float x = u * frequency, y = v * frequency;
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float f1 = float.MaxValue, f2 = float.MaxValue;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int cx = ix + dx, cy = iy + dy;
                uint h = Hash((uint)Wrap(cx, frequency), (uint)Wrap(cy, frequency), seed);
                float px = cx + Unit(h);
                float py = cy + Unit(h * 2654435761u + 12345u);

                float ex = px - x, ey = py - y;
                float d = ex * ex + ey * ey;
                if (d < f1) { f2 = f1; f1 = d; }
                else if (d < f2) { f2 = d; }
            }
        }

        return (MathF.Sqrt(f1), MathF.Sqrt(f2));
    }

    /// <summary>Identifier of the cell containing a point, so per-cell properties such as
    /// a crystal's tint stay constant across it.</summary>
    public static uint CellId(float u, float v, int frequency, uint seed)
    {
        float x = u * frequency, y = v * frequency;
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float best = float.MaxValue;
        uint bestId = 0;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int cx = ix + dx, cy = iy + dy;
                uint h = Hash((uint)Wrap(cx, frequency), (uint)Wrap(cy, frequency), seed);
                float px = cx + Unit(h);
                float py = cy + Unit(h * 2654435761u + 12345u);
                float ex = px - x, ey = py - y;
                float d = ex * ex + ey * ey;
                if (d < best) { best = d; bestId = h; }
            }
        }
        return bestId;
    }

    /// <summary>
    /// Offsets a sample position by another noise field. Domain warping is the cheapest
    /// way to stop a procedural texture looking procedural, because it destroys the
    /// axis-aligned lattice signature the underlying grid leaves behind.
    /// </summary>
    public static (float U, float V) Warp(float u, float v, int frequency, float strength, uint seed)
    {
        float wu = Fbm(u, v, frequency, 3, seed) - 0.5f;
        float wv = Fbm(u, v, frequency, 3, seed ^ 0x9E3779B9u) - 0.5f;
        return (u + wu * strength, v + wv * strength);
    }
}
