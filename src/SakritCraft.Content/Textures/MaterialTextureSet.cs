using System.Numerics;

namespace SakritCraft.Content.Textures;

/// <summary>
/// The physically-based texture maps for one material, as raw eight-bit-per-channel data
/// ready for upload.
/// <para>
/// Three maps, matching the split in §07 of the design: base colour with no baked
/// lighting, a tangent-space normal, and ambient occlusion, roughness and metalness
/// packed one per channel. Height is kept alongside because it drives both the normal
/// and the height-based blending between materials.
/// </para>
/// </summary>
public sealed class MaterialTextureSet
{
    public int Resolution { get; }

    /// <summary>Base colour, sRGB encoded, four bytes per texel.</summary>
    public byte[] Albedo { get; }

    /// <summary>Tangent-space normal, linear, four bytes per texel.</summary>
    public byte[] Normal { get; }

    /// <summary>Occlusion in red, roughness in green, metalness in blue, height in alpha.</summary>
    public byte[] Orm { get; }

    /// <summary>Working height field, in [0,1]. Not uploaded directly.</summary>
    public float[] Height { get; }

    public MaterialTextureSet(int resolution)
    {
        Resolution = resolution;
        int texels = resolution * resolution;
        Albedo = new byte[texels * 4];
        Normal = new byte[texels * 4];
        Orm = new byte[texels * 4];
        Height = new float[texels];
    }

    public int Index(int x, int y) => y * Resolution + x;

    private int WrapCoord(int v) => (v % Resolution + Resolution) % Resolution;

    public void SetAlbedo(int x, int y, Vector3 linearColor)
    {
        int i = Index(x, y) * 4;
        Albedo[i + 0] = ToSrgbByte(linearColor.X);
        Albedo[i + 1] = ToSrgbByte(linearColor.Y);
        Albedo[i + 2] = ToSrgbByte(linearColor.Z);
        Albedo[i + 3] = 255;
    }

    public void SetSurface(int x, int y, float occlusion, float roughness, float metalness)
    {
        int i = Index(x, y) * 4;
        Orm[i + 0] = ToByte(occlusion);
        Orm[i + 1] = ToByte(roughness);
        Orm[i + 2] = ToByte(metalness);
        Orm[i + 3] = ToByte(Height[Index(x, y)]);
    }

    /// <summary>
    /// Derives the tangent-space normal from the height field by central difference,
    /// wrapping at the edges so the normal map tiles as seamlessly as the height does.
    /// </summary>
    /// <param name="strength">Vertical scale. Higher makes the surface read as deeper.</param>
    public void BuildNormalsFromHeight(float strength)
    {
        for (int y = 0; y < Resolution; y++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                float left = Height[Index(WrapCoord(x - 1), y)];
                float right = Height[Index(WrapCoord(x + 1), y)];
                float down = Height[Index(x, WrapCoord(y - 1))];
                float up = Height[Index(x, WrapCoord(y + 1))];

                var n = Vector3.Normalize(new Vector3(
                    (left - right) * strength,
                    (down - up) * strength,
                    1.0f));

                int i = Index(x, y) * 4;
                Normal[i + 0] = ToByte(n.X * 0.5f + 0.5f);
                Normal[i + 1] = ToByte(n.Y * 0.5f + 0.5f);
                Normal[i + 2] = ToByte(n.Z * 0.5f + 0.5f);
                Normal[i + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Approximates ambient occlusion from the height field: a texel sitting below its
    /// surroundings is shadowed by them. Sampling several radii keeps both fine crevices
    /// and broad hollows, which a single radius misses.
    /// </summary>
    public float[] BuildOcclusionFromHeight(float strength = 1.0f)
    {
        var occlusion = new float[Resolution * Resolution];
        ReadOnlySpan<int> radii = stackalloc int[] { 1, 2, 4, 8 };

        for (int y = 0; y < Resolution; y++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                float here = Height[Index(x, y)];
                float raised = 0.0f;

                foreach (int r in radii)
                {
                    float neighbourhood =
                        Height[Index(WrapCoord(x - r), y)] +
                        Height[Index(WrapCoord(x + r), y)] +
                        Height[Index(x, WrapCoord(y - r))] +
                        Height[Index(x, WrapCoord(y + r))];
                    raised += neighbourhood * 0.25f - here;
                }

                raised /= radii.Length;
                occlusion[Index(x, y)] = Math.Clamp(1.0f - raised * 2.4f * strength, 0.35f, 1.0f);
            }
        }

        return occlusion;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(v * 255.0f + 0.5f, 0.0f, 255.0f);

    /// <summary>
    /// Encodes a linear value as sRGB. The albedo texture is sampled through an sRGB
    /// format so the hardware decodes it back to linear; writing linear values into it
    /// would wash every material out.
    /// </summary>
    private static byte ToSrgbByte(float linear)
    {
        linear = Math.Clamp(linear, 0.0f, 1.0f);
        float encoded = linear <= 0.0031308f
            ? linear * 12.92f
            : 1.055f * MathF.Pow(linear, 1.0f / 2.4f) - 0.055f;
        return (byte)Math.Clamp(encoded * 255.0f + 0.5f, 0.0f, 255.0f);
    }
}
