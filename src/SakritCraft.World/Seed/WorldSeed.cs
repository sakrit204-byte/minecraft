using System.Security.Cryptography;
using SakritCraft.Core.Hashing;

namespace SakritCraft.World.Seed;

/// <summary>
/// The single 64-bit number an entire universe derives from, plus the named
/// sub-streams every generator draws on.
/// <para>
/// Generators must never share a stream. If ore veins and rivers drew from the same
/// noise, they would visibly correlate: veins would run along valleys. Splitting the
/// world seed into decorrelated streams is what prevents that.
/// </para>
/// </summary>
public readonly struct WorldSeed : IEquatable<WorldSeed>
{
    /// <summary>The raw world seed. This is the number shown to the player.</summary>
    public readonly ulong Value;

    public WorldSeed(ulong value) => Value = value;

    /// <summary>
    /// Interprets player-entered text. A string that parses as a signed 64-bit
    /// integer is used directly, so that sharing a numeric seed round-trips exactly.
    /// Anything else is hashed. Empty text draws from system entropy.
    /// </summary>
    public static WorldSeed FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new WorldSeed(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)));

        string trimmed = text.Trim();
        return long.TryParse(trimmed, out long numeric)
            ? new WorldSeed(unchecked((ulong)numeric))
            : new WorldSeed(Hash64.String(trimmed));
    }

    /// <summary>
    /// Derives an independent generator stream. The domain name is part of the save
    /// contract: renaming a domain changes every world that uses it, so names here
    /// are frozen once shipped.
    /// </summary>
    public ulong Stream(string domain) => Hash64.Mix(Value ^ Hash64.String(domain));

    /// <summary>Derives a stream for a named domain within a specific dimension.</summary>
    public ulong Stream(Dimension dimension, string domain)
        => Hash64.Mix(Value ^ Hash64.String(domain) ^ ((ulong)dimension * 0x9E3779B97F4A7C15UL));

    /// <summary>The seed as the player sees it, which is signed.</summary>
    public override string ToString() => unchecked((long)Value).ToString();

    public bool Equals(WorldSeed other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is WorldSeed s && Equals(s);
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>The worlds a player can stand in. Values are persisted, so never renumber.</summary>
public enum Dimension : ulong
{
    Overworld = 0,
    Nether = 1,
}

/// <summary>
/// Frozen stream names. Referenced by generators rather than typed as literals, so a
/// typo cannot silently produce a different world.
/// </summary>
public static class SeedDomains
{
    public const string Continent   = "continent";
    public const string Erosion     = "erosion";
    public const string Ridge       = "ridge";
    public const string Detail      = "detail";
    public const string Warp        = "warp";
    public const string Temperature = "temperature";
    public const string Humidity    = "humidity";
    public const string Overhang    = "overhang";
    public const string CaveCheese  = "cave.cheese";
    public const string CaveWormA   = "cave.worm.a";
    public const string CaveWormB   = "cave.worm.b";
    public const string CaveNoodle  = "cave.noodle";
    public const string Aquifer     = "aquifer";
    public const string Strata      = "strata";
    public const string Ore         = "ore";
    public const string Vegetation  = "vegetation";
    public const string Structure   = "structure";
}
