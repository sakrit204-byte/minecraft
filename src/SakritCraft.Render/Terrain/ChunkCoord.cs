using SakritCraft.Mesh;
using Silk.NET.Maths;

namespace SakritCraft.Render.Terrain;

/// <summary>
/// Integer chunk address at one level of detail. A chunk is <see cref="DensityVolume.ChunkCells"/>
/// cells across; at LOD 0 the cells are 0.5 m so a chunk spans 16 m, and each LOD doubles the spacing
/// (docs/MASTER-PLAN.html section 06). World origin of a chunk is exact in double because chunk
/// sizes are powers of two in metres, which is what keeps neighbouring chunks' shared samples
/// bit-identical and their seams closed.
/// </summary>
public readonly record struct ChunkCoord(int X, int Y, int Z, int Lod = 0)
{
    /// <summary>Density sample spacing for LOD 0, in metres (section 03: the voxel is 0.5 m).</summary>
    public const double BaseSpacing = 0.5;

    public double Spacing => BaseSpacing * (1 << Lod);
    /// <summary>Edge length of the chunk in metres.</summary>
    public double Size => DensityVolume.ChunkCells * Spacing;

    public Vector3D<double> Origin => new(X * Size, Y * Size, Z * Size);

    public Vector3D<double> Centre
    {
        get
        {
            double half = Size * 0.5;
            var o = Origin;
            return new Vector3D<double>(o.X + half, o.Y + half, o.Z + half);
        }
    }

    public override string ToString() => Lod == 0 ? $"({X},{Y},{Z})" : $"({X},{Y},{Z})@L{Lod}";
}
