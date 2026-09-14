using SakritCraft.Core.Hashing;

namespace SakritCraft.World.Noise;

/// <summary>
/// Cellular (Worley) noise. Distance to scattered feature points, which produces the
/// interlocking-cell look shared by rock crystals, cracked mud, basalt columns and
/// dissolution pits. Used heavily by the texture baker and by ore vein placement.
/// </summary>
public static class Worley
{
    private const ulong OffsetY = 0xA0761D6478BD642FUL;
    private const ulong OffsetZ = 0xE7037ED1A0B428DBUL;

    /// <summary>
    /// Distance to the nearest feature point, and to the second nearest.
    /// <para>
    /// The gap between the two is the useful quantity for cell borders: it approaches
    /// zero exactly on the boundary between two cells, which is what draws crack lines
    /// and crystal edges.
    /// </para>
    /// </summary>
    public static (double F1, double F2) Distances3(ulong seed, double x, double y, double z)
    {
        long cx = (long)Math.Floor(x), cy = (long)Math.Floor(y), cz = (long)Math.Floor(z);
        double f1 = double.MaxValue, f2 = double.MaxValue;

        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    long gx = cx + dx, gy = cy + dy, gz = cz + dz;
                    ulong h = Hash64.Coord(seed, gx, gy, gz);

                    // Three decorrelated offsets in [0,1) drawn from one hash by re-mixing.
                    double px = gx + Hash64.ToUnit(h);
                    double py = gy + Hash64.ToUnit(Hash64.Mix(h ^ OffsetY));
                    double pz = gz + Hash64.ToUnit(Hash64.Mix(h ^ OffsetZ));

                    double ex = px - x, ey = py - y, ez = pz - z;
                    double d = ex * ex + ey * ey + ez * ez;   // squared, rooted once at the end

                    if (d < f1) { f2 = f1; f1 = d; }
                    else if (d < f2) { f2 = d; }
                }
            }
        }

        return (Math.Sqrt(f1), Math.Sqrt(f2));
    }

    /// <summary>Distance to the nearest feature point only. Cheaper when F2 is unused.</summary>
    public static double Nearest3(ulong seed, double x, double y, double z)
        => Distances3(seed, x, y, z).F1;

    /// <summary>
    /// Cell border strength in [0,1], peaking at 1 on a boundary. This is the crack
    /// and crystal-edge mask that granite, basalt and ice all build on.
    /// </summary>
    public static double Edges3(ulong seed, double x, double y, double z, double width = 0.08)
    {
        (double f1, double f2) = Distances3(seed, x, y, z);
        double border = f2 - f1;
        return border >= width ? 0.0 : 1.0 - border / width;
    }

    /// <summary>
    /// A stable identifier for the cell containing a point, so per-cell properties such
    /// as a crystal tint or an ore blob material stay consistent across every sample
    /// that falls inside the same cell.
    /// </summary>
    public static ulong CellId3(ulong seed, double x, double y, double z)
    {
        long cx = (long)Math.Floor(x), cy = (long)Math.Floor(y), cz = (long)Math.Floor(z);
        double best = double.MaxValue;
        ulong bestId = 0;

        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    long gx = cx + dx, gy = cy + dy, gz = cz + dz;
                    ulong h = Hash64.Coord(seed, gx, gy, gz);
                    double px = gx + Hash64.ToUnit(h);
                    double py = gy + Hash64.ToUnit(Hash64.Mix(h ^ OffsetY));
                    double pz = gz + Hash64.ToUnit(Hash64.Mix(h ^ OffsetZ));
                    double ex = px - x, ey = py - y, ez = pz - z;
                    double d = ex * ex + ey * ey + ez * ez;
                    if (d < best) { best = d; bestId = h; }
                }
            }
        }
        return bestId;
    }
}
