using SakritCraft.World.Noise;
using SakritCraft.World.Seed;

namespace SakritCraft.World.Generation;

/// <summary>
/// The world, as a continuous scalar function of position. This is the single most
/// important type in the project: everything downstream reads it.
/// <para>
/// Sign convention, fixed once and relied on everywhere: <b>negative is solid rock,
/// positive is open air, and the surface is the zero crossing.</b> The magnitude is an
/// approximate distance to that surface in metres, which is what lets the physics in
/// §09 sphere-trace against the field directly instead of against a collision mesh.
/// </para>
/// </summary>
public sealed class DensityField
{
    /// <summary>Lowest point in the world. Below this everything is solid bedrock.</summary>
    public const double WorldBottom = -512.0;

    /// <summary>Highest point in the world.</summary>
    public const double WorldTop = 1024.0;

    /// <summary>
    /// The largest distance the three-dimensional terms can pull the surface above the
    /// height field. A sample further above the height field than this is certainly
    /// air, which lets the generator skip the expensive noise entirely.
    /// </summary>
    private const double MaxDeformation = 80.0;

    private readonly LandformFields _landform;
    private readonly CaveCarver _caves;
    private readonly ulong _overhang;
    private readonly ulong _detail;

    public WorldSeed Seed { get; }
    public Dimension Dimension { get; }

    public DensityField(WorldSeed seed, Dimension dimension = Dimension.Overworld)
    {
        Seed = seed;
        Dimension = dimension;
        _landform = new LandformFields(seed, dimension);
        _caves = new CaveCarver(seed, dimension);
        _overhang = seed.Stream(dimension, SeedDomains.Overhang);
        _detail = seed.Stream(dimension, SeedDomains.Detail);
    }

    /// <summary>The per-column landform and climate fields, exposed for callers that
    /// need them alongside the density, and for the <c>/worldinfo</c> command.</summary>
    public LandformFields Landform => _landform;

    /// <summary>
    /// Evaluates the field at a point. Prefer <see cref="SampleColumn"/> when taking
    /// several samples in the same vertical column, since that reuses the expensive
    /// two-dimensional work instead of repeating it per sample.
    /// </summary>
    public double Sample(double x, double y, double z)
    {
        ColumnSample column = _landform.Sample(x, z);
        return SampleColumn(in column, x, y, z);
    }

    /// <summary>
    /// Evaluates the field using a pre-computed column sample. A 32-cubed chunk takes
    /// 1,024 column samples instead of 32,768, which is the difference between meshing
    /// being viable and not.
    /// </summary>
    public double SampleColumn(in ColumnSample column, double x, double y, double z)
    {
        // Hard caps. Bedrock at the bottom keeps the player in the world; the ceiling
        // keeps the octree bounded.
        if (y <= WorldBottom) return -64.0;
        if (y >= WorldTop) return 64.0;

        // Base term: distance above the height field, which alone would be a heightmap.
        double density = y - column.BaseHeight;

        // Early out high in the sky. Nothing below can pull the field back to solid up
        // here, and skipping the 3D noise saves most of the cost of an airborne sample.
        if (density > MaxDeformation) return density;

        // Stage 6. Three-dimensional deformation, scaled by how young the terrain is.
        // This is what turns a heightmap into something with overhangs and arches, and
        // it is the reason a cliff can undercut.
        double overhangStrength = LandformFields.OverhangStrength(column.Erosion);
        if (overhangStrength > 0.0)
        {
            // Fade the deformation out well below the surface so it warps cliff faces
            // rather than churning deep rock, where it would only cost time.
            double nearSurface = Math.Clamp(1.0 - (column.BaseHeight - y) / 120.0, 0.0, 1.0);
            if (nearSurface > 0.0)
            {
                // The vertical frequency matters more than the strength. An overhang
                // exists only where the deformation makes density non-monotonic in y,
                // which needs strength * d(noise)/dy to exceed 1. At this scale that
                // threshold is around 30, which is why the earlier value of 26 as a
                // maximum produced a bumpy heightmap and never a true overhang.
                density -= Fractal.Fbm3(_overhang, x / 74.0, y / 30.0, z / 74.0, 3)
                           * overhangStrength * nearSurface;
            }
        }

        // Fine detail, so surfaces are not glassy at arm's length.
        density -= Fractal.Fbm3(_detail, x / 17.0, y / 17.0, z / 17.0, 2) * 1.8;

        // Stage 8. Caves are subtracted as a boolean difference of signed distances,
        // which is max(solid, -cavity). Adding an air amount instead cannot work: this
        // field grows without bound with depth, so any fixed contribution stops
        // breaking through a few metres down.
        double cavity = _caves.Distance(x, y, z, column.BaseHeight);
        return Math.Max(density, -cavity);
    }

    /// <summary>
    /// Approximate surface normal, from the gradient of the field. Points away from
    /// solid rock, so it is the outward normal of the terrain.
    /// </summary>
    public (double X, double Y, double Z) Normal(double x, double y, double z, double epsilon = 0.25)
    {
        ColumnSample cxPlus = _landform.Sample(x + epsilon, z);
        ColumnSample cxMinus = _landform.Sample(x - epsilon, z);
        ColumnSample czPlus = _landform.Sample(x, z + epsilon);
        ColumnSample czMinus = _landform.Sample(x, z - epsilon);
        ColumnSample here = _landform.Sample(x, z);

        double dx = SampleColumn(in cxPlus, x + epsilon, y, z) - SampleColumn(in cxMinus, x - epsilon, y, z);
        double dy = SampleColumn(in here, x, y + epsilon, z) - SampleColumn(in here, x, y - epsilon, z);
        double dz = SampleColumn(in czPlus, x, y, z + epsilon) - SampleColumn(in czMinus, x, y, z - epsilon);

        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length < 1e-12) return (0.0, 1.0, 0.0);
        return (dx / length, dy / length, dz / length);
    }

    /// <summary>
    /// Finds the terrain surface height in a column by marching down from the top of
    /// the possible range and refining the first sign change by bisection.
    /// <para>
    /// Because the field has overhangs, a column can cross the surface several times.
    /// This returns the highest crossing, which is the one a tree or a spawning
    /// creature should sit on.
    /// </para>
    /// </summary>
    public double SurfaceHeight(double x, double z, double step = 2.0)
    {
        ColumnSample column = _landform.Sample(x, z);

        // Start above any deformation the overhang term can produce.
        double top = Math.Min(WorldTop, column.BaseHeight + 64.0);
        double previous = SampleColumn(in column, x, top, z);

        for (double y = top - step; y > WorldBottom; y -= step)
        {
            double current = SampleColumn(in column, x, y, z);
            if (previous > 0.0 && current <= 0.0)
            {
                // Bisect the bracketing interval. Eight iterations resolve a 2 metre
                // step to under a centimetre, far below the 0.5 metre voxel size.
                double lo = y, hi = y + step;
                for (int i = 0; i < 8; i++)
                {
                    double mid = (lo + hi) * 0.5;
                    if (SampleColumn(in column, x, mid, z) <= 0.0) lo = mid; else hi = mid;
                }
                return (lo + hi) * 0.5;
            }
            previous = current;
        }

        return WorldBottom;
    }
}
