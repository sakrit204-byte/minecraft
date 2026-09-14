using SakritCraft.World.Seed;

namespace SakritCraft.Render.Terrain;

/// <summary>
/// Start-up configuration for the terrain system. The region settings describe the fixed block of
/// chunks generated once at start-up; when dynamic streaming lands they become the initial request
/// and the streamer takes over from there.
/// </summary>
public sealed class TerrainOptions
{
    public required WorldSeed Seed { get; init; }

    /// <summary>World X/Z of the region centre (the camera's start column), in metres.</summary>
    public double CentreX { get; init; }
    public double CentreZ { get; init; }

    /// <summary>Chunks along X and Z in the start-up region. 20 chunks is 320 m, about 7 s of generation on ten workers.</summary>
    public int RegionChunksX { get; init; } = 20;
    public int RegionChunksZ { get; init; } = 20;

    /// <summary>
    /// Upper bound on chunks stacked vertically. The actual span is derived from the terrain's height
    /// range over the region so mountains are not clipped and deep flat ground does not waste chunks.
    /// </summary>
    public int MaxRegionChunksY { get; init; } = 12;

    /// <summary>Worker threads for generation and meshing. Default leaves two cores for the render and message-pump threads.</summary>
    public int WorkerThreads { get; init; } = Math.Max(1, Environment.ProcessorCount - 2);

    /// <summary>Bytes of chunk geometry the render thread will push into staging per frame.</summary>
    public ulong UploadBytesPerFrame { get; init; } = 24UL * 1024 * 1024;

    /// <summary>Size of each vertex/index pool buffer. Pools are added as needed.</summary>
    public ulong PoolBufferBytes { get; init; } = 64UL * 1024 * 1024;
}
