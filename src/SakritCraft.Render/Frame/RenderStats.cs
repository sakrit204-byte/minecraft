namespace SakritCraft.Render.Frame;

/// <summary>
/// Per-frame scene counters for the statistics readout. Reset by the renderer at the top of each
/// frame and filled in by the passes; plain fields so updating them costs nothing.
/// </summary>
public struct RenderStats
{
    public long TrianglesDrawn;
    public int DrawCalls;
    public int ChunksDrawn;
    public int ChunksCulled;
    public int ChunksResident;
    public int ChunksUploading;
    public int ChunksPending;
    public int ChunksEmpty;
    public ulong VramUsedBytes;
    public ulong VramReservedBytes;
    public ulong GeometryBytes;

    public void ResetFrame()
    {
        TrianglesDrawn = 0;
        DrawCalls = 0;
        ChunksDrawn = 0;
        ChunksCulled = 0;
    }

    public static string FormatBytes(ulong bytes)
    {
        const double MiB = 1024.0 * 1024.0;
        return bytes >= 1024 * MiB ? $"{bytes / (1024 * MiB):F2} GiB" : $"{bytes / MiB:F0} MiB";
    }

    public static string FormatTriangles(long triangles)
        => triangles >= 1_000_000 ? $"{triangles / 1_000_000.0:F2}M" : triangles >= 1_000 ? $"{triangles / 1_000.0:F1}k" : triangles.ToString();
}
