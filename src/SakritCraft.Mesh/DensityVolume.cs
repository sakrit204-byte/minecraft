using System.Runtime.CompilerServices;

namespace SakritCraft.Mesh;

/// <summary>
/// A cubic block of density samples with material identifiers, sized to cover one
/// chunk plus the padding the mesher needs.
/// <para>
/// A 32-cell chunk needs 33 corner samples, and estimating a surface normal at a
/// corner by central difference needs one sample beyond that in each direction, so the
/// buffer is 35 samples across with the chunk origin at index 1. Getting this padding
/// wrong is the classic cause of visible seams between chunks, because a mesher that
/// clamps at the edge produces a slightly different normal there than its neighbour
/// does for the same point in space.
/// </para>
/// </summary>
public sealed class DensityVolume
{
    /// <summary>Cells along one edge of a chunk.</summary>
    public const int ChunkCells = 32;

    /// <summary>
    /// Samples along one edge of the padded buffer.
    /// <para>
    /// A 32-cell chunk meshes cells 0 through 32 inclusive, because the far boundary
    /// needs one extra cell for its quads to reference. Those cells span corner samples
    /// 1 through 34, and a central-difference gradient at corner 34 reads sample 35.
    /// Hence 36, not 35: an off-by-one here reads past the buffer on the far face.
    /// </para>
    /// </summary>
    public const int Dim = ChunkCells + 4;   // 36

    /// <summary>Index of the chunk origin within the padded buffer.</summary>
    public const int Origin = 1;

    private readonly float[] _density;
    private readonly byte[] _material;

    /// <summary>World position of sample index zero.</summary>
    public (double X, double Y, double Z) Base { get; }

    /// <summary>Metres between adjacent samples. Doubles with each level of detail.</summary>
    public double Spacing { get; }

    public DensityVolume((double X, double Y, double Z) origin, double spacing)
    {
        // The padded buffer starts one sample before the chunk origin.
        Base = (origin.X - spacing * Origin, origin.Y - spacing * Origin, origin.Z - spacing * Origin);
        Spacing = spacing;
        _density = new float[Dim * Dim * Dim];
        _material = new byte[Dim * Dim * Dim];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Index(int x, int y, int z) => (z * Dim + y) * Dim + x;

    public float this[int x, int y, int z]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _density[Index(x, y, z)];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _density[Index(x, y, z)] = value;
    }

    public byte GetMaterial(int x, int y, int z) => _material[Index(x, y, z)];
    public void SetMaterial(int x, int y, int z, byte material) => _material[Index(x, y, z)] = material;

    /// <summary>World position of a sample index.</summary>
    public (double X, double Y, double Z) PositionOf(int x, int y, int z)
        => (Base.X + x * Spacing, Base.Y + y * Spacing, Base.Z + z * Spacing);

    /// <summary>
    /// Fills the buffer from a sampling function. The function receives world
    /// coordinates and returns density, with negative meaning solid.
    /// </summary>
    public void Fill(Func<double, double, double, float> sample)
    {
        for (int z = 0; z < Dim; z++)
        {
            double wz = Base.Z + z * Spacing;
            for (int y = 0; y < Dim; y++)
            {
                double wy = Base.Y + y * Spacing;
                for (int x = 0; x < Dim; x++)
                {
                    _density[Index(x, y, z)] = sample(Base.X + x * Spacing, wy, wz);
                }
            }
        }
    }

    /// <summary>
    /// Gradient of the density field at a sample, by central difference. Points away
    /// from solid rock, so it is the outward surface normal once normalised.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (float X, float Y, float Z) Gradient(int x, int y, int z)
    {
        float dx = this[x + 1, y, z] - this[x - 1, y, z];
        float dy = this[x, y + 1, z] - this[x, y - 1, z];
        float dz = this[x, y, z + 1] - this[x, y, z - 1];
        return (dx, dy, dz);
    }
}
