using System.Numerics;

namespace SakritCraft.Mesh;

/// <summary>One meshed vertex. Position is relative to the chunk origin, in metres.</summary>
public struct TerrainVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public byte Material;
}

/// <summary>Triangles and vertices produced for one chunk.</summary>
public sealed class ChunkMesh
{
    public readonly List<TerrainVertex> Vertices = new();
    public readonly List<int> Indices = new();

    public int TriangleCount => Indices.Count / 3;
    public bool IsEmpty => Indices.Count == 0;

    public void Clear()
    {
        Vertices.Clear();
        Indices.Clear();
    }
}

/// <summary>
/// Dual contouring by surface nets: one vertex per cell that straddles the surface,
/// positioned by minimising a quadratic error function over the plane constraints from
/// each edge crossing.
/// <para>
/// Marching cubes was rejected for this project. It produces sliver triangles, and it
/// cannot represent a sharp edge at all, so the moment a player flattens a floor the
/// junction with the wall comes out rounded. Solving a QEF instead recovers the corner,
/// which is what lets one representation serve both a natural cave and a cut mineshaft.
/// </para>
/// </summary>
public static class SurfaceNets
{
    // The twelve edges of a cell, as pairs of corner indices in the standard ordering
    // where corner bit 0 is +x, bit 1 is +y, bit 2 is +z.
    private static readonly int[,] EdgeCorners =
    {
        {0,1},{2,3},{4,5},{6,7},   // along x
        {0,2},{1,3},{4,6},{5,7},   // along y
        {0,4},{1,5},{2,6},{3,7},   // along z
    };

    private static readonly int[,] CornerOffset =
    {
        {0,0,0},{1,0,0},{0,1,0},{1,1,0},
        {0,0,1},{1,0,1},{0,1,1},{1,1,1},
    };

    /// <summary>
    /// Regularisation weight pulling the solved vertex toward the average of its edge
    /// crossings. Without it the normal equations are singular on a flat surface, where
    /// every constraint plane is parallel and the vertex is free to slide.
    /// </summary>
    private const float Regularisation = 0.06f;

    /// <summary>Meshes one chunk from a padded density volume.</summary>
    public static void Mesh(DensityVolume volume, ChunkMesh output)
    {
        output.Clear();

        const int cells = DensityVolume.ChunkCells;
        const int stride = cells + 1;    // one extra so the far face can index neighbours

        // Vertex index per cell, or -1. Sized one larger than the chunk in each
        // direction so that quads on the far boundary can reference the adjacent cell.
        var cellVertex = new int[stride * stride * stride];
        Array.Fill(cellVertex, -1);

        float spacing = (float)volume.Spacing;

        // Allocated once, reused per cell. A stackalloc inside the loop would grow the
        // frame on every iteration and eventually overflow the stack.
        Span<float> corner = stackalloc float[8];

        // ── Pass one: place a vertex in every cell the surface passes through ─────
        for (int cz = 0; cz < stride; cz++)
        {
            for (int cy = 0; cy < stride; cy++)
            {
                for (int cx = 0; cx < stride; cx++)
                {
                    int bx = cx + DensityVolume.Origin;
                    int by = cy + DensityVolume.Origin;
                    int bz = cz + DensityVolume.Origin;

                    // Gather the eight corner densities.
                    int signMask = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        float d = volume[bx + CornerOffset[i, 0], by + CornerOffset[i, 1], bz + CornerOffset[i, 2]];
                        corner[i] = d;
                        if (d < 0.0f) signMask |= 1 << i;
                    }

                    // All solid or all air means the surface does not cross this cell.
                    if (signMask == 0 || signMask == 0xFF) continue;

                    // Accumulate the normal equations for the QEF, plus the mass point.
                    float a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0;
                    float b0 = 0, b1 = 0, b2 = 0;
                    Vector3 massPoint = Vector3.Zero;
                    Vector3 normalSum = Vector3.Zero;
                    int crossings = 0;
                    int materialVotes = 0;
                    int material = 0;

                    for (int e = 0; e < 12; e++)
                    {
                        int c0 = EdgeCorners[e, 0], c1 = EdgeCorners[e, 1];
                        float d0 = corner[c0], d1 = corner[c1];
                        if ((d0 < 0.0f) == (d1 < 0.0f)) continue;

                        // Linear interpolation to the zero crossing along the edge.
                        float t = d0 / (d0 - d1);
                        var p = new Vector3(
                            CornerOffset[c0, 0] + t * (CornerOffset[c1, 0] - CornerOffset[c0, 0]),
                            CornerOffset[c0, 1] + t * (CornerOffset[c1, 1] - CornerOffset[c0, 1]),
                            CornerOffset[c0, 2] + t * (CornerOffset[c1, 2] - CornerOffset[c0, 2]));

                        // Normal at the crossing, interpolated from the two corner gradients.
                        var g0 = volume.Gradient(bx + CornerOffset[c0, 0], by + CornerOffset[c0, 1], bz + CornerOffset[c0, 2]);
                        var g1 = volume.Gradient(bx + CornerOffset[c1, 0], by + CornerOffset[c1, 1], bz + CornerOffset[c1, 2]);
                        var n = new Vector3(
                            g0.X + t * (g1.X - g0.X),
                            g0.Y + t * (g1.Y - g0.Y),
                            g0.Z + t * (g1.Z - g0.Z));

                        float length = n.Length();
                        if (length < 1e-9f) continue;
                        n /= length;

                        float d = Vector3.Dot(n, p);
                        a00 += n.X * n.X; a01 += n.X * n.Y; a02 += n.X * n.Z;
                        a11 += n.Y * n.Y; a12 += n.Y * n.Z; a22 += n.Z * n.Z;
                        b0 += n.X * d; b1 += n.Y * d; b2 += n.Z * d;

                        massPoint += p;
                        normalSum += n;
                        crossings++;

                        // The material of a face comes from whichever side is solid.
                        int solidCorner = d0 < 0.0f ? c0 : c1;
                        byte m = volume.GetMaterial(
                            bx + CornerOffset[solidCorner, 0],
                            by + CornerOffset[solidCorner, 1],
                            bz + CornerOffset[solidCorner, 2]);
                        if (m != 0) { material = m; materialVotes++; }
                    }

                    if (crossings == 0) continue;

                    massPoint /= crossings;

                    // Regularise toward the mass point so the system is never singular.
                    a00 += Regularisation; a11 += Regularisation; a22 += Regularisation;
                    b0 += Regularisation * massPoint.X;
                    b1 += Regularisation * massPoint.Y;
                    b2 += Regularisation * massPoint.Z;

                    Vector3 local = Solve3x3(a00, a01, a02, a11, a12, a22, b0, b1, b2, massPoint);

                    // Clamp inside the cell. An unclamped QEF solution can land far
                    // outside on a near-degenerate system, which is the self-intersection
                    // this technique is notorious for.
                    local = Vector3.Clamp(local, Vector3.Zero, Vector3.One);

                    Vector3 normal = normalSum.LengthSquared() > 1e-12f
                        ? Vector3.Normalize(normalSum)
                        : Vector3.UnitY;

                    cellVertex[(cz * stride + cy) * stride + cx] = output.Vertices.Count;
                    output.Vertices.Add(new TerrainVertex
                    {
                        Position = new Vector3((cx + local.X) * spacing,
                                               (cy + local.Y) * spacing,
                                               (cz + local.Z) * spacing),
                        Normal = normal,
                        Material = (byte)(materialVotes > 0 ? material : 1),
                    });
                }
            }
        }

        // ── Pass two: one quad per sign-changing edge of the sample lattice ───────
        // Each such edge is shared by exactly four cells, and their vertices form the
        // quad. This is the dual of marching cubes and is why the output is quads.
        for (int cz = 0; cz < stride; cz++)
        {
            for (int cy = 0; cy < stride; cy++)
            {
                for (int cx = 0; cx < stride; cx++)
                {
                    int bx = cx + DensityVolume.Origin;
                    int by = cy + DensityVolume.Origin;
                    int bz = cz + DensityVolume.Origin;

                    float here = volume[bx, by, bz];
                    bool solidHere = here < 0.0f;

                    // Edge along +x. The four cells sharing it are those at
                    // (cx, cy-1..cy, cz-1..cz).
                    if (cx < stride - 1 && cy > 0 && cz > 0)
                    {
                        bool solidNext = volume[bx + 1, by, bz] < 0.0f;
                        if (solidHere != solidNext)
                        {
                            EmitQuad(output, cellVertex, stride,
                                (cx, cy - 1, cz - 1), (cx, cy - 1, cz), (cx, cy, cz), (cx, cy, cz - 1),
                                flip: solidHere);
                        }
                    }

                    // Edge along +y. Shared by cells at (cx-1..cx, cy, cz-1..cz).
                    if (cy < stride - 1 && cx > 0 && cz > 0)
                    {
                        bool solidNext = volume[bx, by + 1, bz] < 0.0f;
                        if (solidHere != solidNext)
                        {
                            EmitQuad(output, cellVertex, stride,
                                (cx - 1, cy, cz - 1), (cx, cy, cz - 1), (cx, cy, cz), (cx - 1, cy, cz),
                                flip: solidHere);
                        }
                    }

                    // Edge along +z. Shared by cells at (cx-1..cx, cy-1..cy, cz).
                    if (cz < stride - 1 && cx > 0 && cy > 0)
                    {
                        bool solidNext = volume[bx, by, bz + 1] < 0.0f;
                        if (solidHere != solidNext)
                        {
                            EmitQuad(output, cellVertex, stride,
                                (cx - 1, cy - 1, cz), (cx - 1, cy, cz), (cx, cy, cz), (cx, cy - 1, cz),
                                flip: solidHere);
                        }
                    }
                }
            }
        }
    }

    private static void EmitQuad(ChunkMesh mesh, int[] cellVertex, int stride,
                                 (int X, int Y, int Z) a, (int X, int Y, int Z) b,
                                 (int X, int Y, int Z) c, (int X, int Y, int Z) d,
                                 bool flip)
    {
        int i0 = cellVertex[(a.Z * stride + a.Y) * stride + a.X];
        int i1 = cellVertex[(b.Z * stride + b.Y) * stride + b.X];
        int i2 = cellVertex[(c.Z * stride + c.Y) * stride + c.X];
        int i3 = cellVertex[(d.Z * stride + d.Y) * stride + d.X];

        // Every cell touching a sign-changing edge must hold a vertex. If one is
        // missing the volume was inconsistent, so drop the quad rather than emit a
        // degenerate triangle that would break the normal and the physics.
        if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0) return;

        if (flip)
        {
            (i0, i1, i2, i3) = (i3, i2, i1, i0);
        }

        mesh.Indices.Add(i0); mesh.Indices.Add(i1); mesh.Indices.Add(i2);
        mesh.Indices.Add(i0); mesh.Indices.Add(i2); mesh.Indices.Add(i3);
    }

    /// <summary>
    /// Solves a symmetric positive-definite 3x3 system by Cramer's rule. Falls back to
    /// the mass point when the determinant is too small to trust, which happens on a
    /// perfectly flat surface where the vertex genuinely has a free direction.
    /// </summary>
    private static Vector3 Solve3x3(float a00, float a01, float a02,
                                    float a11, float a12, float a22,
                                    float b0, float b1, float b2,
                                    Vector3 fallback)
    {
        float det = a00 * (a11 * a22 - a12 * a12)
                  - a01 * (a01 * a22 - a12 * a02)
                  + a02 * (a01 * a12 - a11 * a02);

        if (MathF.Abs(det) < 1e-10f) return fallback;

        float inv = 1.0f / det;
        float x = inv * (b0 * (a11 * a22 - a12 * a12)
                       - a01 * (b1 * a22 - a12 * b2)
                       + a02 * (b1 * a12 - a11 * b2));
        float y = inv * (a00 * (b1 * a22 - a12 * b2)
                       - b0 * (a01 * a22 - a12 * a02)
                       + a02 * (a01 * b2 - b1 * a02));
        float z = inv * (a00 * (a11 * b2 - b1 * a12)
                       - a01 * (a01 * b2 - b1 * a02)
                       + b0 * (a01 * a12 - a11 * a02));

        return new Vector3(x, y, z);
    }
}
