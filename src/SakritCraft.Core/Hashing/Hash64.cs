using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;

namespace SakritCraft.Core.Hashing;

/// <summary>
/// Deterministic 64-bit hashing primitives.
/// <para>
/// Everything in world generation derives from these, so they must produce identical
/// results on every machine, every run, and every .NET version. That rules out
/// <see cref="object.GetHashCode"/> (randomised per process) and anything using
/// floating point. See <c>docs/MASTER-PLAN.html</c> §03.
/// </para>
/// </summary>
public static class Hash64
{
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;

    // Distinct odd multipliers per axis. Odd so they are invertible mod 2^64,
    // distinct so that permuting coordinates cannot collide.
    private const ulong MulX = 0xD6E8FEB86659FD93UL;
    private const ulong MulY = 0xA0761D6478BD642FUL;
    private const ulong MulZ = 0xE7037ED1A0B428DBUL;
    private const ulong MulW = 0x8EBC6AF09C88C6E3UL;

    /// <summary>
    /// SplitMix64 finalizer. Strong avalanche in three rounds: flipping any input bit
    /// changes each output bit with probability near one half.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Advances a SplitMix64 generator state and returns the next value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Next(ref ulong state) => Mix(state += Gamma);

    /// <summary>Hashes a lattice point in one dimension.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Coord(ulong seed, long x) => Mix(seed ^ ((ulong)x * MulX));

    /// <summary>Hashes a lattice point in two dimensions.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Coord(ulong seed, long x, long y)
        => Mix(seed ^ ((ulong)x * MulX) ^ ((ulong)y * MulY));

    /// <summary>Hashes a lattice point in three dimensions.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Coord(ulong seed, long x, long y, long z)
        => Mix(seed ^ ((ulong)x * MulX) ^ ((ulong)y * MulY) ^ ((ulong)z * MulZ));

    /// <summary>Hashes a lattice point in four dimensions.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Coord(ulong seed, long x, long y, long z, long w)
        => Mix(seed ^ ((ulong)x * MulX) ^ ((ulong)y * MulY) ^ ((ulong)z * MulZ) ^ ((ulong)w * MulW));

    /// <summary>
    /// Stable hash of a UTF-8 string. Used for seed text and for naming generator
    /// streams, so it must never change once a world has been saved.
    /// </summary>
    public static ulong String(string text)
    {
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte[]? rented = null;
        Span<byte> buffer = max <= 256
            ? stackalloc byte[256]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(max));

        try
        {
            int written = Encoding.UTF8.GetBytes(text, buffer);
            return XxHash64.HashToUInt64(buffer[..written]);
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Maps a hash to a double in [0,1). Uses the top 53 bits, which are the
    /// best-mixed ones and exactly the mantissa width of a double.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToUnit(ulong hash) => (hash >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Maps a hash to a double in [-1,1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToSigned(ulong hash) => ToUnit(hash) * 2.0 - 1.0;
}
