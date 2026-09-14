// Placeholder surface appearance for terrain until the material system of docs section 07 exists:
// flat albedos per material id, chosen by slope, with cheap procedural variation so a hillside reads
// as ground rather than as a single tone. No textures.
#ifndef SAKRIT_SURFACE_GLSL
#define SAKRIT_SURFACE_GLSL

#include "frame.glsl"

// Material ids mirror SakritCraft.Render.Terrain.TerrainMaterial.
const uint MAT_ROCK   = 1u;
const uint MAT_SOIL   = 2u;
const uint MAT_SAND   = 3u;
const uint MAT_SNOW   = 4u;
const uint MAT_GRAVEL = 5u;

// Mirrors FrameConstants.PatternPeriod. The hash works on lattice coordinates reduced modulo this, so
// the pattern is continuous across the wrap of cameraPosWrapped and its inputs never exceed 1024.
const float PATTERN_PERIOD = 1024.0;

// Integer hash (lowbias32) on the wrapped lattice cell: robust at any magnitude, unlike sin-based hashes.
float sakritHash2(vec2 cell)
{
    uvec2 q = uvec2(ivec2(mod(cell, PATTERN_PERIOD)));
    uint h = q.x * 1597334677u ^ q.y * 3812015801u;
    h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
    return float(h) * (1.0 / 4294967296.0);
}

// Smooth value noise in [0,1] over metres; period-safe through sakritHash2.
float sakritValueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = sakritHash2(i), b = sakritHash2(i + vec2(1.0, 0.0));
    float c = sakritHash2(i + vec2(0.0, 1.0)), d = sakritHash2(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// Linear-space base colours.
const vec3 ALBEDO_ROCK      = vec3(0.28, 0.27, 0.26);
const vec3 ALBEDO_ROCK_DARK = vec3(0.17, 0.16, 0.16);
const vec3 ALBEDO_SOIL      = vec3(0.25, 0.17, 0.10);
const vec3 ALBEDO_GRASS     = vec3(0.12, 0.22, 0.05);
const vec3 ALBEDO_GRASS_DRY = vec3(0.27, 0.24, 0.09);
const vec3 ALBEDO_SAND      = vec3(0.60, 0.53, 0.37);
const vec3 ALBEDO_SNOW      = vec3(0.86, 0.88, 0.93);
const vec3 ALBEDO_GRAVEL    = vec3(0.34, 0.32, 0.29);

// Albedo for a fragment. worldXZ: camera-relative xz plus the wrapped camera position (small numbers,
// periodic). height: absolute world Y. n: unit surface normal.
vec3 sakritTerrainAlbedo(uint material, vec3 n, vec2 worldXZ, float height)
{
    float flatness = clamp(n.y, 0.0, 1.0);

    // Two octaves: ~12 m patches and ~3 m mottling.
    float broad = sakritValueNoise(worldXZ * 0.085);
    float fine  = sakritValueNoise(worldXZ * 0.33);
    float variation = broad * 0.65 + fine * 0.35;

    // Rock always shows strata-like tonal variation with the fine noise.
    vec3 rock = mix(ALBEDO_ROCK_DARK, ALBEDO_ROCK, 0.35 + 0.65 * fine);

    if (material == MAT_SOIL)
    {
        // Grass dries out with altitude and in noise-chosen patches; steep ground shows soil, and
        // near-vertical faces expose the rock beneath. The noise also nudges the slope thresholds so
        // the transitions are ragged rather than contour lines.
        float dryness = clamp(smoothstep(95.0, 160.0, height) * 0.6 + (variation - 0.45) * 0.9, 0.0, 1.0);
        vec3 grass = mix(ALBEDO_GRASS, ALBEDO_GRASS_DRY, dryness) * (0.85 + 0.30 * fine);
        float jitter = (variation - 0.5) * 0.18;
        vec3 c = mix(ALBEDO_SOIL * (0.9 + 0.2 * fine), grass, smoothstep(0.58 + jitter, 0.80 + jitter, flatness));
        return mix(rock, c, smoothstep(0.28, 0.52, flatness));
    }
    if (material == MAT_SAND)   return mix(rock, ALBEDO_SAND * (0.9 + 0.2 * fine), smoothstep(0.30, 0.55, flatness));
    if (material == MAT_SNOW)   return mix(rock, ALBEDO_SNOW, smoothstep(0.35, 0.60, flatness));
    if (material == MAT_GRAVEL) return ALBEDO_GRAVEL * (0.85 + 0.3 * fine);
    return rock;
}

#endif // SAKRIT_SURFACE_GLSL
