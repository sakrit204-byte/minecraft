namespace SakritCraft.Sim.Time;

/// <summary>
/// The day cycle. Twenty real minutes to a day, matching Minecraft, because that pacing
/// is what makes a night something you prepare for rather than wait out.
/// </summary>
public sealed class WorldClock
{
    /// <summary>Seconds in a full day.</summary>
    public const double DaySeconds = 20.0 * 60.0;

    /// <summary>In-game days in a season.</summary>
    public const int SeasonDays = 28;

    /// <summary>Seconds elapsed since the world began.</summary>
    public double TotalSeconds { get; private set; }

    /// <summary>Position within the current day, 0 to 1. Zero is dawn.</summary>
    public double DayFraction => TotalSeconds % DaySeconds / DaySeconds;

    public long DayNumber => (long)(TotalSeconds / DaySeconds);

    public Season Season => (Season)(DayNumber / SeasonDays % 4);

    public void Advance(double seconds) => TotalSeconds += seconds;

    /// <summary>Jumps to a point in the current day, for the <c>/time</c> command.</summary>
    public void SetDayFraction(double fraction)
    {
        double whole = Math.Floor(TotalSeconds / DaySeconds);
        TotalSeconds = (whole + Math.Clamp(fraction, 0.0, 0.9999)) * DaySeconds;
    }

    /// <summary>
    /// Sun elevation in degrees above the horizon. Negative is night.
    /// <para>
    /// A simple sinusoid with the peak at midday, tilted by season so that summer days are
    /// longer and the sun climbs higher. The renderer reads this for its sun direction and
    /// the light field reads it for sky brightness, so they can never disagree about
    /// whether it is dark.
    /// </para>
    /// </summary>
    public double SunElevationDegrees
    {
        get
        {
            // Dawn at 0, noon at 0.25, dusk at 0.5, midnight at 0.75.
            double angle = (DayFraction - 0.25) * 2.0 * Math.PI;
            double seasonalTilt = 12.0 * Math.Sin(DayNumber / (double)(SeasonDays * 4) * 2.0 * Math.PI);
            return Math.Cos(angle) * (52.0 + seasonalTilt);
        }
    }

    /// <summary>
    /// Fraction of full sky light reaching the ground, 0 to 1.
    /// <para>
    /// Crossing below the spawn threshold shortly after sunset is the moment the world turns
    /// dangerous, so the curve is deliberately steep through twilight rather than linear:
    /// dusk should feel like a door closing.
    /// </para>
    /// </summary>
    public double SkyBrightness
    {
        get
        {
            double elevation = SunElevationDegrees;
            if (elevation >= 6.0) return 1.0;
            if (elevation <= -8.0) return 0.0;
            double t = (elevation + 8.0) / 14.0;
            return t * t * (3.0 - 2.0 * t);   // smoothstep through twilight
        }
    }

    public bool IsNight => SkyBrightness < 0.25;
    public bool IsDaylight => SkyBrightness > 0.75;

    /// <summary>Clock reading as hours and minutes, for the debug overlay.</summary>
    public override string ToString()
    {
        double hours = DayFraction * 24.0 + 6.0;   // dawn at 06:00
        if (hours >= 24.0) hours -= 24.0;
        return $"day {DayNumber} {(int)hours:00}:{(int)(hours % 1.0 * 60.0):00} ({Season})";
    }
}

public enum Season : byte
{
    Spring = 0,
    Summer = 1,
    Autumn = 2,
    Winter = 3,
}
