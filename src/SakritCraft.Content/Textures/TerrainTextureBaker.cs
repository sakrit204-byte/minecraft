using System.Numerics;

namespace SakritCraft.Content.Textures;

/// <summary>Layers in the terrain material texture array. Order is the upload order.</summary>
public enum TerrainSurface
{
    Rock = 0,
    Soil = 1,
    Sand = 2,
    Snow = 3,
    Gravel = 4,
    Grass = 5,
}

/// <summary>
/// Generates the physically-based texture set for every terrain material.
/// <para>
/// Nothing here is painted or downloaded. Each material is a short recipe over the
/// tiling noise in <see cref="TileNoise"/>, which has three advantages that matter at
/// this scale: the output is seamless by construction, a material is a few lines of
/// source rather than megabytes of asset, and the same recipe can be re-evaluated at any
/// resolution or weathering state.
/// </para>
/// </summary>
public static class TerrainTextureBaker
{
    /// <summary>Every layer, in array order.</summary>
    public static readonly TerrainSurface[] Layers =
    [
        TerrainSurface.Rock, TerrainSurface.Soil, TerrainSurface.Sand,
        TerrainSurface.Snow, TerrainSurface.Gravel, TerrainSurface.Grass,
    ];

    public static MaterialTextureSet Bake(TerrainSurface surface, int resolution)
    {
        var set = new MaterialTextureSet(resolution);

        switch (surface)
        {
            case TerrainSurface.Rock: Granite(set); break;
            case TerrainSurface.Soil: Soil(set); break;
            case TerrainSurface.Sand: Sand(set); break;
            case TerrainSurface.Snow: Snow(set); break;
            case TerrainSurface.Gravel: Gravel(set); break;
            case TerrainSurface.Grass: Grass(set); break;
            default: throw new ArgumentOutOfRangeException(nameof(surface));
        }

        return set;
    }

    // ── Granite ──────────────────────────────────────────────────────────────────
    // Interlocking crystals from cellular noise at three scales, with a sparse mask of
    // mica flecks that catch the light. Roughness varies per crystal, so the surface
    // glitters unevenly the way cut stone does rather than uniformly.
    private static void Granite(MaterialTextureSet set)
    {
        int n = set.Resolution;
        float inv = 1.0f / n;

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;
                // Heavy warp so crystal boundaries are ragged rather than the smooth
                // polygons raw cellular noise produces. Unwarped cells read as tiling.
                (float wu, float wv) = TileNoise.Warp(u, v, 22, 0.055f, 11u);

                (float c1, float c2) = TileNoise.Cellular(wu, wv, 9, 101u);
                (float d1, float d2) = TileNoise.Cellular(wu, wv, 21, 102u);
                float fine = TileNoise.Fbm(u, v, 70, 4, 103u);

                // Crystals sit proud with recessed boundaries, rather than the surface
                // being flat with cracks scratched into it.
                float grain = Math.Clamp((c2 - c1) * 3.0f, 0.0f, 1.0f);
                float sub = Math.Clamp((d2 - d1) * 4.5f, 0.0f, 1.0f);

                // Fracture veins: thin, deep, and following their own direction.
                float vein = TileNoise.Ridged(wu, wv, 6, 3, 109u);
                float veinMask = Math.Clamp((vein - 0.80f) * 7.0f, 0.0f, 1.0f);

                float height = 0.30f + grain * 0.40f + sub * 0.16f + fine * 0.14f - veinMask * 0.42f;
                set.Height[set.Index(x, y)] = Math.Clamp(height, 0.0f, 1.0f);
            }
        });

        float[] occlusion = set.BuildOcclusionFromHeight(1.35f);
        set.BuildNormalsFromHeight(3.4f);

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;
                (float wu, float wv) = TileNoise.Warp(u, v, 22, 0.055f, 11u);

                // Per-crystal identity: a stable id keeps each grain one colour, and two
                // independent draws from it give tint and lightness separately.
                uint id = TileNoise.CellId(wu, wv, 9, 101u);
                float tint = (id & 0xFFFF) / 65535.0f;
                float value = ((id >> 16) & 0xFFFF) / 65535.0f;
                uint subId = TileNoise.CellId(wu, wv, 21, 102u);
                float subTint = (subId & 0xFF) / 255.0f;
                float fine = TileNoise.Fbm(u, v, 70, 4, 103u);

                // A real granite palette: dark hornblende, grey quartz, pink feldspar.
                var hornblende = new Vector3(0.052f, 0.050f, 0.054f);
                var quartz = new Vector3(0.226f, 0.222f, 0.214f);
                var feldspar = new Vector3(0.318f, 0.216f, 0.182f);

                Vector3 baseColor = Vector3.Lerp(quartz, feldspar, tint * tint);
                baseColor = Vector3.Lerp(baseColor, hornblende, Math.Clamp(0.86f - value * 1.5f, 0.0f, 0.88f));
                baseColor = Vector3.Lerp(baseColor, baseColor * 1.35f, subTint * 0.45f);
                baseColor *= 0.84f + fine * 0.32f;

                float vein = TileNoise.Ridged(wu, wv, 6, 3, 109u);
                float veinMask = Math.Clamp((vein - 0.80f) * 7.0f, 0.0f, 1.0f);
                baseColor = Vector3.Lerp(baseColor, hornblende * 0.7f, veinMask * 0.8f);

                float occ = occlusion[set.Index(x, y)];
                baseColor *= 0.52f + 0.48f * occ;

                // Mica: a few percent of area, brighter and much smoother.
                float mica = TileNoise.Value(u, v, 170, 107u);
                bool fleck = mica > 0.945f;
                if (fleck) baseColor += new Vector3(0.10f, 0.098f, 0.092f);

                float roughness = 0.74f - value * 0.16f + (fine - 0.5f) * 0.12f;
                if (fleck) roughness = 0.26f;

                set.SetAlbedo(x, y, baseColor);
                set.SetSurface(x, y, occ, Math.Clamp(roughness, 0.08f, 1.0f), 0.0f);
            }
        });
    }

    // ── Soil ─────────────────────────────────────────────────────────────────────
    // Clumped aggregates from cellular noise, a dark humus ramp, and scattered small
    // stones from a second sparser cellular layer.
    private static void Soil(MaterialTextureSet set)
    {
        int n = set.Resolution;
        float inv = 1.0f / n;

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;
                (float wu, float wv) = TileNoise.Warp(u, v, 6, 0.09f, 21u);

                (float c1, _) = TileNoise.Cellular(wu, wv, 22, 201u);
                float clumps = 1.0f - Math.Clamp(c1 * 1.7f, 0.0f, 1.0f);
                float grain = TileNoise.Fbm(u, v, 48, 4, 202u);

                float stone = StoneMask(u, v, out _);
                float height = 0.40f + clumps * 0.24f + grain * 0.18f + stone * 0.34f;
                set.Height[set.Index(x, y)] = Math.Clamp(height, 0.0f, 1.0f);
            }
        });

        float[] occlusion = set.BuildOcclusionFromHeight(1.15f);
        set.BuildNormalsFromHeight(3.1f);

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;
                float grain = TileNoise.Fbm(u, v, 48, 4, 202u);
                float broad = TileNoise.Fbm(u, v, 5, 3, 203u);

                float stone = StoneMask(u, v, out uint stoneId);

                var dark = new Vector3(0.043f, 0.031f, 0.022f);
                var mid = new Vector3(0.104f, 0.074f, 0.049f);
                var dry = new Vector3(0.176f, 0.134f, 0.090f);

                Vector3 color = Vector3.Lerp(dark, mid, grain);
                color = Vector3.Lerp(color, dry, Math.Clamp(broad * 1.3f - 0.25f, 0.0f, 1.0f));

                if (stone > 0.0f)
                {
                    // Tint each stone from its own identifier, so a scatter of pebbles
                    // reads as many stones rather than one repeated dot.
                    float tone = 0.14f + (stoneId >> 20 & 0xFF) / 255.0f * 0.16f;
                    var pebble = new Vector3(tone, tone * 0.96f, tone * 0.90f);
                    color = Vector3.Lerp(color, pebble, Math.Clamp(stone * 1.6f, 0.0f, 0.92f));
                }

                float occ = occlusion[set.Index(x, y)];
                color *= 0.60f + 0.40f * occ;

                float roughness = stone > 0.3f ? 0.70f : 0.94f - grain * 0.07f;
                set.SetAlbedo(x, y, color);
                set.SetSurface(x, y, occ, roughness, 0.0f);
            }
        });
    }

    /// <summary>
    /// A scatter of small stones embedded in soil.
    /// <para>
    /// Thresholding a cellular distance directly gives a perfect circle in every cell,
    /// which reads as polka dots rather than as gravel in earth. Three things fix that:
    /// the sample position is warped hard so the outlines go lumpy, most cells hold no
    /// stone at all, and the ones that do draw their size from their own identifier.
    /// </para>
    /// </summary>
    private static float StoneMask(float u, float v, out uint id)
    {
        (float su, float sv) = TileNoise.Warp(u, v, 44, 0.020f, 209u);
        (float d, _) = TileNoise.Cellular(su, sv, 13, 205u);
        id = TileNoise.CellId(su, sv, 13, 205u);

        float present = ((id >> 4) & 0xFF) / 255.0f;
        if (present < 0.56f) return 0.0f;

        float size = 0.12f + ((id >> 12) & 0xFF) / 255.0f * 0.18f;
        if (d >= size) return 0.0f;

        return MathF.Sqrt(1.0f - d / size);   // domed, so the stone sits proud
    }

    // ── Sand ─────────────────────────────────────────────────────────────────────
    // Fine grain plus a large-scale ripple carried almost entirely in the normal, since
    // ripples are geometry rather than colour. Albedo is near uniform on purpose.
    private static void Sand(MaterialTextureSet set)
    {
        int n = set.Resolution;
        float inv = 1.0f / n;

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;
                (float wu, float wv) = TileNoise.Warp(u, v, 4, 0.16f, 31u);

                // Ripples: a directional wave, warped so the crests wander.
                float ripple = MathF.Sin((wu * 9.0f + wv * 2.0f) * MathF.PI * 2.0f) * 0.5f + 0.5f;
                float grain = TileNoise.Fbm(u, v, 128, 3, 302u);
                float drift = TileNoise.Fbm(u, v, 7, 3, 303u);

                float height = 0.42f + ripple * 0.30f + grain * 0.12f + drift * 0.14f;
                set.Height[set.Index(x, y)] = Math.Clamp(height, 0.0f, 1.0f);
            }
        });

        float[] occlusion = set.BuildOcclusionFromHeight(0.7f);
        set.BuildNormalsFromHeight(1.9f);

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;
                float grain = TileNoise.Fbm(u, v, 128, 3, 302u);
                float drift = TileNoise.Fbm(u, v, 7, 3, 303u);

                var pale = new Vector3(0.482f, 0.406f, 0.288f);
                var warm = new Vector3(0.552f, 0.462f, 0.318f);

                Vector3 color = Vector3.Lerp(pale, warm, drift);
                color *= 0.94f + grain * 0.12f;

                float occ = occlusion[set.Index(x, y)];
                color *= 0.82f + 0.18f * occ;

                // Sparse quartz glints, the one specular feature dry sand has.
                float sparkle = TileNoise.Value(u, v, 220, 307u);
                float roughness = sparkle > 0.965f ? 0.34f : 0.90f;

                set.SetAlbedo(x, y, color);
                set.SetSurface(x, y, occ, roughness, 0.0f);
            }
        });
    }

    // ── Snow ─────────────────────────────────────────────────────────────────────
    // Soft wind-formed dunes, a very bright and slightly blue albedo, and a scatter of
    // ice crystals that catch the sun.
    private static void Snow(MaterialTextureSet set)
    {
        int n = set.Resolution;
        float inv = 1.0f / n;

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;
                (float wu, float wv) = TileNoise.Warp(u, v, 5, 0.12f, 41u);

                // Wind-carved sastrugi: ridged rather than smooth, which is what makes
                // snow read as a surface with form instead of a flat white sheet.
                float dunes = TileNoise.Ridged(wu, wv, 5, 3, 401u);
                float drift = TileNoise.Fbm(wu, wv, 14, 3, 402u);
                float crystal = TileNoise.Fbm(u, v, 110, 2, 403u);

                float height = 0.12f + dunes * 0.58f + drift * 0.22f + crystal * 0.08f;
                set.Height[set.Index(x, y)] = Math.Clamp(height, 0.0f, 1.0f);
            }
        });

        float[] occlusion = set.BuildOcclusionFromHeight(0.85f);
        set.BuildNormalsFromHeight(1.5f);

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;
                float occ = occlusion[set.Index(x, y)];
                float h = set.Height[set.Index(x, y)];

                // Snow is deeply translucent, so light entering a hollow scatters and
                // comes back blue. Driving colour from depth as well as occlusion is
                // what stops it clipping to a featureless white.
                var lit = new Vector3(0.880f, 0.906f, 0.950f);
                var shadowed = new Vector3(0.330f, 0.428f, 0.622f);
                float exposure = Math.Clamp(h * 0.55f + MathF.Pow(occ, 1.5f) * 0.45f, 0.0f, 1.0f);
                Vector3 color = Vector3.Lerp(shadowed, lit, exposure);

                float sparkle = TileNoise.Value(u, v, 300, 407u);
                float roughness = sparkle > 0.976f ? 0.12f : 0.55f - h * 0.10f;
                if (sparkle > 0.976f) color += new Vector3(0.06f);

                set.SetAlbedo(x, y, color);
                set.SetSurface(x, y, occ, roughness, 0.0f);
            }
        });
    }

    // ── Gravel ───────────────────────────────────────────────────────────────────
    // Packed pebbles: cellular noise domed into rounded stones, each tinted by its cell
    // identifier so the bed reads as many stones rather than one textured sheet.
    private static void Gravel(MaterialTextureSet set)
    {
        int n = set.Resolution;
        float inv = 1.0f / n;

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;
                (float wu, float wv) = TileNoise.Warp(u, v, 10, 0.045f, 51u);

                (float c1, float c2) = TileNoise.Cellular(wu, wv, 26, 501u);
                // Dome each cell: high at the centre, falling to the shared border.
                float dome = Math.Clamp((c2 - c1) * 2.6f, 0.0f, 1.0f);
                float grain = TileNoise.Fbm(u, v, 90, 3, 502u);

                set.Height[set.Index(x, y)] = Math.Clamp(0.18f + dome * 0.70f + grain * 0.12f, 0.0f, 1.0f);
            }
        });

        float[] occlusion = set.BuildOcclusionFromHeight(1.4f);
        set.BuildNormalsFromHeight(3.4f);

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;
                (float wu, float wv) = TileNoise.Warp(u, v, 10, 0.045f, 51u);

                uint id = TileNoise.CellId(wu, wv, 26, 501u);
                float tint = (id & 0xFFFF) / 65535.0f;
                float shade = ((id >> 16) & 0xFF) / 255.0f;
                float grain = TileNoise.Fbm(u, v, 90, 3, 502u);

                var cold = new Vector3(0.232f, 0.236f, 0.246f);
                var warm = new Vector3(0.316f, 0.276f, 0.232f);
                var pale = new Vector3(0.402f, 0.394f, 0.372f);

                Vector3 color = Vector3.Lerp(cold, warm, tint);
                color = Vector3.Lerp(color, pale, shade * 0.45f);
                color *= 0.85f + grain * 0.22f;

                float occ = occlusion[set.Index(x, y)];
                color *= 0.58f + 0.42f * occ;

                set.SetAlbedo(x, y, color);
                set.SetSurface(x, y, occ, 0.80f - tint * 0.10f, 0.0f);
            }
        });
    }

    // ── Grass ────────────────────────────────────────────────────────────────────
    // Blade clumps from directional flow noise, with colour varying per clump and the
    // height raised at clump centres so parallax gives real depth at grazing angles.
    private static void Grass(MaterialTextureSet set)
    {
        int n = set.Resolution;
        float inv = 1.0f / n;

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;

                // Blades: a high-frequency field stretched eight to one along a slowly
                // wandering direction, so individual blades resolve instead of blurring
                // into moss. The stretch factor must stay a whole number or the texture
                // stops tiling.
                (float bu, float bv) = TileNoise.Warp(u, v, 16, 0.045f, 61u);
                float blades = TileNoise.Ridged(bu, bv * 8.0f, 40, 2, 601u);
                float fine = TileNoise.Ridged(bu * 8.0f, bv, 56, 2, 604u);
                float clumps = TileNoise.Fbm(u, v, 9, 3, 602u);
                float patch = TileNoise.Fbm(u, v, 4, 2, 603u);

                float height = 0.12f + blades * 0.40f + fine * 0.22f + clumps * 0.24f + patch * 0.10f;
                set.Height[set.Index(x, y)] = Math.Clamp(height, 0.0f, 1.0f);
            }
        });

        float[] occlusion = set.BuildOcclusionFromHeight(1.5f);
        set.BuildNormalsFromHeight(2.8f);

        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                float u = x * inv, v = y * inv;
                (float bu, float bv) = TileNoise.Warp(u, v, 16, 0.045f, 61u);
                float blades = TileNoise.Ridged(bu, bv * 8.0f, 40, 2, 601u);
                float fine = TileNoise.Ridged(bu * 8.0f, bv, 56, 2, 604u);
                float clumps = TileNoise.Fbm(u, v, 9, 3, 602u);
                float patch = TileNoise.Fbm(u, v, 4, 2, 603u);

                var deep = new Vector3(0.019f, 0.038f, 0.013f);
                var mid = new Vector3(0.062f, 0.112f, 0.032f);
                var bright = new Vector3(0.138f, 0.196f, 0.058f);
                var dry = new Vector3(0.208f, 0.190f, 0.084f);

                Vector3 color = Vector3.Lerp(deep, mid, blades * 0.7f + fine * 0.3f);
                color = Vector3.Lerp(color, bright, Math.Clamp(clumps * 1.4f - 0.3f, 0.0f, 1.0f));
                color = Vector3.Lerp(color, dry, Math.Clamp(patch * 1.1f - 0.55f, 0.0f, 0.55f));

                // Soil showing through where the cover thins.
                float thin = Math.Clamp(0.30f - clumps, 0.0f, 1.0f) * 2.4f;
                color = Vector3.Lerp(color, new Vector3(0.098f, 0.072f, 0.048f), Math.Clamp(thin, 0.0f, 0.62f));

                float occ = occlusion[set.Index(x, y)];
                color *= 0.42f + 0.58f * occ;

                set.SetAlbedo(x, y, color);
                set.SetSurface(x, y, occ, 0.88f - blades * 0.14f, 0.0f);
            }
        });
    }
}
