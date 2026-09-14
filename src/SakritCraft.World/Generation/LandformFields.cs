using SakritCraft.World.Noise;
using SakritCraft.World.Seed;

namespace SakritCraft.World.Generation;

/// <summary>
/// The per-column sample every later generation stage reads. Computed once per
/// horizontal position and reused by height, density, biome, material and vegetation,
/// because recomputing the noise for each of them would be the single largest waste
/// in the generator.
/// </summary>
public readonly record struct ColumnSample(
    double Continentalness,
    double Erosion,
    double Ridge,
    double BaseHeight,
    double TemperatureC,
    double Humidity);

/// <summary>
/// Stages one through five of the generation pipeline: the three landform fields, the
/// height curve built from them, and the two climate fields.
/// <para>
/// These are all two-dimensional and cheap. Everything expensive downstream is shaped
/// by what happens here, so the tuning in this file matters more than its size suggests.
/// </para>
/// </summary>
public sealed class LandformFields
{
    /// <summary>Sea level in metres. The reference height for almost every other number.</summary>
    public const double SeaLevel = 64.0;

    private readonly ulong _continent;
    private readonly ulong _erosion;
    private readonly ulong _ridge;
    private readonly ulong _warp;
    private readonly ulong _temperature;
    private readonly ulong _humidity;

    // ── Height curves ────────────────────────────────────────────────────────────
    // Continentalness drives the broad land-versus-ocean shape. The flat step from
    // -0.15 to 0.05 is the continental shelf, and it is what gives coastlines a beach
    // instead of a cliff dropping straight into deep water.
    private static readonly Spline ContinentHeight = new(
        (-1.00, SeaLevel - 52.0),   // deep ocean
        (-0.55, SeaLevel - 30.0),   // ocean floor
        (-0.30, SeaLevel - 8.0),    // shelf
        (-0.18, SeaLevel - 1.0),    // shore
        (-0.10, SeaLevel + 4.0),    // beach, just clear of the water
        (0.10, SeaLevel + 26.0),    // lowland
        (0.35, SeaLevel + 72.0),    // inland
        (0.62, SeaLevel + 168.0),   // highland
        (1.00, SeaLevel + 330.0));  // far inland plateau

    // Erosion scales how much the ridge field is allowed to contribute. High erosion
    // means old, worn land: flat. Low erosion means young land: jagged.
    private static readonly Spline ErosionRelief = new(
        (-1.00, 340.0),   // young mountains, dramatic relief
        (-0.60, 215.0),
        (-0.20, 118.0),
        (0.15, 48.0),
        (0.50, 16.0),
        (0.80, 5.0),
        (1.00, 0.5));     // ancient peneplain, almost flat

    public LandformFields(WorldSeed seed, Dimension dimension = Dimension.Overworld)
    {
        _continent = seed.Stream(dimension, SeedDomains.Continent);
        _erosion = seed.Stream(dimension, SeedDomains.Erosion);
        _ridge = seed.Stream(dimension, SeedDomains.Ridge);
        _warp = seed.Stream(dimension, SeedDomains.Warp);
        _temperature = seed.Stream(dimension, SeedDomains.Temperature);
        _humidity = seed.Stream(dimension, SeedDomains.Humidity);
    }

    /// <summary>
    /// Stage 1. Ocean through far inland, in [-1,1]. Domain warped so coastlines
    /// wander and fold instead of reading as smooth contour lines.
    /// </summary>
    public double Continentalness(double x, double z)
    {
        (double wx, double wz) = Fractal.Warp2(_warp, x, z, 1.0 / 3000.0, 420.0);
        return Fractal.Fbm2(_continent, wx / 6000.0, wz / 6000.0, 5);
    }

    /// <summary>
    /// Stage 2. How worn the land is, in [-1,1]. Negative is young and jagged,
    /// positive is old and flat. Also gates how much three-dimensional character the
    /// density field gets, which is why overhangs appear in mountains and not plains.
    /// </summary>
    public double Erosion(double x, double z)
        => Fractal.Fbm2(_erosion, x / 2200.0, z / 2200.0, 4);

    /// <summary>
    /// Stage 3. Ridge lines in [0,1], peaking at 1 along a crest. Warped so ranges
    /// curve the way real orogenic belts do rather than running straight.
    /// </summary>
    public double Ridge(double x, double z)
    {
        (double wx, double wz) = Fractal.Warp2(_ridge, x, z, 1.0 / 900.0, 140.0);
        return Fractal.Ridged2(_ridge, wx / 1100.0, wz / 1100.0, 5);
    }

    /// <summary>
    /// Stage 6, first half. Combines the three landform fields into a height in metres.
    /// The ridge contribution is centred so that a mid-range ridge value neither raises
    /// nor lowers the base, letting valleys cut below it as well as peaks rise above.
    /// </summary>
    public double BaseHeight(double continentalness, double erosion, double ridge)
    {
        double baseline = ContinentHeight.Evaluate(continentalness);
        double relief = ErosionRelief.Evaluate(erosion);

        // Bias the ridge toward its low end so most terrain sits below the crests.
        double shaped = ridge * ridge * (3.0 - 2.0 * ridge);   // smoothstep, sharpens peaks
        return baseline + (shaped - 0.30) * relief;
    }

    /// <summary>
    /// Stage 4. Temperature in degrees Celsius, combining a broad latitude gradient, a
    /// regional noise field, and a lapse rate of 6.5 degrees per kilometre of altitude.
    /// The lapse rate is what makes a mountain genuinely colder than the plain beside
    /// it, which in turn is what puts snow lines where they belong.
    /// </summary>
    public double Temperature(double x, double z, double height)
    {
        const double LatitudeScale = 13000.0;   // metres from equator to pole band
        const double LapseRatePerMetre = 0.0065;

        double latitude = Math.Clamp(z / LatitudeScale, -1.0, 1.0);
        double seasonal = 27.0 - 40.0 * (latitude * latitude);          // +27 C at equator, -13 C at pole
        double regional = Fractal.Fbm2(_temperature, x / 3200.0, z / 3200.0, 3) * 11.0;
        double altitude = Math.Max(0.0, height - SeaLevel) * LapseRatePerMetre;

        return seasonal + regional - altitude;
    }

    /// <summary>
    /// Stage 4, second half. Relative humidity in [0,1]. Raised near and below sea
    /// level, and suppressed at altitude, which produces dry high plateaus without
    /// needing an explicit rain-shadow pass.
    /// </summary>
    public double Humidity(double x, double z, double height)
    {
        double field = Fractal.Fbm2(_humidity, x / 2600.0, z / 2600.0, 4) * 0.5 + 0.5;
        double maritime = Math.Clamp((SeaLevel + 30.0 - height) / 120.0, 0.0, 1.0) * 0.28;
        double dryness = Math.Clamp((height - SeaLevel - 120.0) / 400.0, 0.0, 1.0) * 0.35;
        return Math.Clamp(field + maritime - dryness, 0.0, 1.0);
    }

    /// <summary>
    /// Evaluates every per-column field in one pass. This is the entry point the rest
    /// of the generator uses; the individual methods are exposed for debugging and for
    /// the <c>/worldinfo</c> command.
    /// </summary>
    public ColumnSample Sample(double x, double z)
    {
        double continentalness = Continentalness(x, z);
        double erosion = Erosion(x, z);
        double ridge = Ridge(x, z);
        double height = BaseHeight(continentalness, erosion, ridge);
        double temperature = Temperature(x, z, height);
        double humidity = Humidity(x, z, height);

        return new ColumnSample(continentalness, erosion, ridge, height, temperature, humidity);
    }

    /// <summary>
    /// How strongly the three-dimensional noise is allowed to deform the height field
    /// into overhangs and arches. Near zero on eroded plains and large in young
    /// mountains, so cliffs undercut where they geologically should.
    /// </summary>
    public static double OverhangStrength(double erosion)
        => Math.Clamp((0.15 - erosion) / 1.05, 0.0, 1.0) * 40.0;
}
