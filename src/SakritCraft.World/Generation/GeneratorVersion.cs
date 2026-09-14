namespace SakritCraft.World.Generation;

/// <summary>
/// Identifies the exact generation rules a world was created with.
/// <para>
/// The save format stores only the difference between generated terrain and player
/// edits, so changing any generator silently moves the terrain under every existing
/// world: a player's mine shaft would end up inside solid rock, or their house would
/// be left floating. The only honest defence is to pin the version per world and keep
/// old generators alive.
/// </para>
/// <para>
/// <b>Rule:</b> when a change alters the output of <see cref="DensityField"/>,
/// <see cref="LandformFields"/>, <see cref="CaveCarver"/> or the noise primitives,
/// add a new entry here and leave the old code path reachable. A golden-hash test
/// failure is the signal that this is required.
/// </para>
/// </summary>
public static class GeneratorVersion
{
    /// <summary>The version new worlds are created with.</summary>
    public const int Current = 1;

    /// <summary>
    /// The oldest version this build can still generate. Opening a world older than
    /// this must refuse rather than corrupt it.
    /// </summary>
    public const int MinimumSupported = 1;

    /// <summary>
    /// History, so that a future maintainer can tell what moved and when.
    /// <list type="bullet">
    /// <item><b>1</b> — First playable generator. Three landform fields with domain
    /// warping, spline-driven height, lapse-rate climate, signed-distance caves in
    /// three families, erosion-gated overhangs. Cave thresholds set by measured
    /// percentile to 7.6% of underground volume.</item>
    /// </list>
    /// </summary>
    public static string Describe(int version) => version switch
    {
        1 => "First playable generator (landform splines, SDF caves, erosion-gated overhangs)",
        _ => $"Unknown generator version {version}",
    };
}
