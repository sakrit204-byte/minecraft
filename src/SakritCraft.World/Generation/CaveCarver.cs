using SakritCraft.World.Noise;
using SakritCraft.World.Seed;

namespace SakritCraft.World.Generation;

/// <summary>
/// Stage 8. Three cave families, each doing a different job, expressed as a signed
/// distance so they can be subtracted from the rock correctly.
/// <para>
/// The families exist because one noise function produces one texture of cave, and a
/// system that reads as natural needs variety of scale: somewhere to build, somewhere
/// to walk, and dead ends that make the network feel unplanned.
/// </para>
/// <para>
/// <b>Sign convention:</b> negative inside cave air, positive in rock, magnitude in
/// approximate metres. This matches <see cref="DensityField"/> so the two combine with
/// a boolean subtraction rather than by adding an arbitrary amount of "airness". The
/// earlier additive form could not work: terrain density grows without bound with
/// depth, so a fixed air contribution never broke through at any depth below a few
/// metres.
/// </para>
/// </summary>
public sealed class CaveCarver
{
    private readonly ulong _cheese;
    private readonly ulong _wormA;
    private readonly ulong _wormB;
    private readonly ulong _noodle;

    // Converts a noise-space difference into approximate metres. Each is the reciprocal
    // of that field's gradient magnitude per metre, so the resulting distance is close
    // enough to metric for sphere tracing to converge. Exactness is not required: the
    // zero crossing, which is what the mesher reads, is unaffected by these.
    private const double CheeseMetres = 40.0;
    private const double TubeMetres = 64.0;
    private const double NoodleMetres = 23.0;

    // ── Thresholds, chosen by measurement rather than by eye ─────────────────────
    // Each was picked from the measured percentile of its own field over 300,000
    // samples of realistic underground volume. Together they carve 7.6% of rock into
    // cave, with a median cavity 2.5 m tall and two thirds of cavities tall enough to
    // stand in. Guessed values were three times too generous and produced a world of
    // floating rock islands, so these numbers are not to be nudged without re-running
    // the sweep.
    private const double CheeseThreshold = -0.35;   // cave where the field falls below
    private const double TubeThreshold = 0.82;      // cave where both ridges rise above
    private const double NoodleThreshold = 0.93;

    /// <summary>Distance returned where no cave may exist. Large enough never to win a
    /// minimum against a real cave, small enough not to disturb the outer subtraction.</summary>
    private const double NoCave = 1000.0;

    public CaveCarver(WorldSeed seed, Dimension dimension = Dimension.Overworld)
    {
        _cheese = seed.Stream(dimension, SeedDomains.CaveCheese);
        _wormA = seed.Stream(dimension, SeedDomains.CaveWormA);
        _wormB = seed.Stream(dimension, SeedDomains.CaveWormB);
        _noodle = seed.Stream(dimension, SeedDomains.CaveNoodle);
    }

    /// <summary>
    /// Signed distance to the nearest cave surface. Negative inside cave air.
    /// </summary>
    /// <param name="surfaceHeight">Terrain height at this column. Caves fade out near
    /// the surface so they do not open as holes in flat ground, and fade out near
    /// bedrock so the deepest layer stays walkable.</param>
    public double Distance(double x, double y, double z, double surfaceHeight)
    {
        double depth = surfaceHeight - y;

        // Fade caves in below the soil, and out approaching bedrock. Expressed as a
        // distance so it clips the cave volume smoothly instead of switching it off.
        double roofClearance = (depth - 6.0) * 1.5;          // negative in the top 6 m
        double floorClearance = (y - DensityField.WorldBottom - 24.0) * 1.5;
        double envelope = Math.Min(roofClearance, floorClearance);
        if (envelope <= 0.0) return NoCave;

        // ── Cheese caves ──────────────────────────────────────────────────────────
        // Large open chambers, the ones worth building a base inside. Carved where the
        // field dips below a threshold, which produces compact rounded volumes.
        //
        // One-sided rather than the banded form that carves where the absolute value is
        // small: a band around the zero level set is a thin connected sheet, so chamber
        // height and cave volume cannot be tuned independently. Wanting a chamber tall
        // enough to stand in then forces a volume fraction that dissolves the rock.
        // A one-sided threshold decouples the two, with size set by the noise scale and
        // volume set by the threshold.
        double cheeseField = Fractal.Fbm3(_cheese, x / 70.0, y / 48.0, z / 70.0, 3);
        double cheeseThreshold = CheeseThreshold + 0.035 * Math.Clamp((40.0 - y) / 140.0, 0.0, 1.0);
        double cheese = (cheeseField - cheeseThreshold) * CheeseMetres;

        // ── Spaghetti caves ───────────────────────────────────────────────────────
        // Long winding tubes two to four metres across: the ones you follow. Formed by
        // intersecting two independent ridged fields, so a passage exists only where
        // both are near their crest. That intersection is what makes them connected
        // rather than a scatter of unrelated pockets.
        double ra = Fractal.Ridged3(_wormA, x / 150.0, y / 96.0, z / 150.0, 2);
        double rb = Fractal.Ridged3(_wormB, x / 150.0, y / 96.0, z / 150.0, 2);
        double tube = (TubeThreshold - Math.Min(ra, rb)) * TubeMetres;

        // ── Noodle caves ──────────────────────────────────────────────────────────
        // Thin, high frequency, mostly dead ends. Their only job is to make the network
        // irregular so it does not read as designed.
        double noodle = (NoodleThreshold - Fractal.Ridged3(_noodle, x / 42.0, y / 34.0, z / 42.0, 2))
                        * NoodleMetres;

        // Union of the three families is the minimum of their distances, then clipped
        // by the envelope so nothing opens through the surface or the bedrock floor.
        double caves = Math.Min(cheese, Math.Min(tube, noodle));
        return Math.Max(caves, -envelope);
    }
}
