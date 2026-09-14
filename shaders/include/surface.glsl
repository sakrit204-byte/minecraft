// Terrain surface appearance: triplanar sampling of the procedural material arrays baked by
// SakritCraft.Content.Textures.TerrainTextureBaker, blended by slope and altitude.
//
// Terrain has no UV coordinates. It is a meshed density field, so a vertex has a position and a
// normal and nothing else to index a texture with. Triplanar projection solves that by sampling
// three times along the world axes and blending by the surface normal, which costs three fetches per
// map and works on overhangs, cave roofs and vertical cliffs alike.
#ifndef SAKRIT_SURFACE_GLSL
#define SAKRIT_SURFACE_GLSL

#include "frame.glsl"

// Material ids written into the density volume. Mirrors SakritCraft.Render.Terrain.TerrainMaterial.
const uint MAT_ROCK   = 1u;
const uint MAT_SOIL   = 2u;
const uint MAT_SAND   = 3u;
const uint MAT_SNOW   = 4u;
const uint MAT_GRAVEL = 5u;

// Array layers. Mirrors SakritCraft.Content.Textures.TerrainSurface.
const uint LAYER_ROCK   = 0u;
const uint LAYER_SOIL   = 1u;
const uint LAYER_SAND   = 2u;
const uint LAYER_SNOW   = 3u;
const uint LAYER_GRAVEL = 4u;
const uint LAYER_GRASS  = 5u;

// Metres per texture repeat. Small enough that a footstep spans real detail, large enough that the
// repeat is not obvious once macro variation is applied on top.
const float SAKRIT_TEXEL_SCALE   = 1.0 / 2.6;
// A second, much larger repeat multiplied over the first. Two incommensurable periods beat against
// each other, so the visible repeat becomes their least common multiple rather than the smaller one.
const float SAKRIT_MACRO_SCALE   = 1.0 / 37.0;

struct SakritSurface
{
    vec3  albedo;
    vec3  normal;      // world space
    float roughness;
    float occlusion;
};

// ---------------------------------------------------------------------------------------------
// Triplanar helpers
// ---------------------------------------------------------------------------------------------

// Blend weights from the surface normal. Raising to a power tightens the transition so a flat face
// is dominated by one projection instead of being a smear of all three.
vec3 sakritTriplanarWeights(vec3 n)
{
    vec3 w = pow(abs(n), vec3(6.0));
    return w / max(w.x + w.y + w.z, 1e-5);
}

vec4 sakritTriplanarSample(uint tex, uint smp, float layer, vec3 wp, vec3 w, float scale)
{
    vec4 x = texture(sampler2DArray(g_TextureArrays[nonuniformEXT(tex)], g_Samplers[nonuniformEXT(smp)]),
                     vec3(wp.zy * scale, layer));
    vec4 y = texture(sampler2DArray(g_TextureArrays[nonuniformEXT(tex)], g_Samplers[nonuniformEXT(smp)]),
                     vec3(wp.xz * scale, layer));
    vec4 z = texture(sampler2DArray(g_TextureArrays[nonuniformEXT(tex)], g_Samplers[nonuniformEXT(smp)]),
                     vec3(wp.xy * scale, layer));
    return x * w.x + y * w.y + z * w.z;
}

// Whiteout blend for triplanar normal mapping. Each projection's tangent normal is reoriented by the
// geometric normal before blending; naively averaging the three flattens detail toward the axes.
vec3 sakritTriplanarNormal(uint tex, uint smp, float layer, vec3 wp, vec3 w, float scale, vec3 n)
{
    vec3 nx = texture(sampler2DArray(g_TextureArrays[nonuniformEXT(tex)], g_Samplers[nonuniformEXT(smp)]),
                      vec3(wp.zy * scale, layer)).xyz * 2.0 - 1.0;
    vec3 ny = texture(sampler2DArray(g_TextureArrays[nonuniformEXT(tex)], g_Samplers[nonuniformEXT(smp)]),
                      vec3(wp.xz * scale, layer)).xyz * 2.0 - 1.0;
    vec3 nz = texture(sampler2DArray(g_TextureArrays[nonuniformEXT(tex)], g_Samplers[nonuniformEXT(smp)]),
                      vec3(wp.xy * scale, layer)).xyz * 2.0 - 1.0;

    nx = vec3(nx.xy + n.zy, abs(nx.z) * n.x);
    ny = vec3(ny.xy + n.xz, abs(ny.z) * n.y);
    nz = vec3(nz.xy + n.xy, abs(nz.z) * n.z);

    return normalize(nx.zyx * w.x + ny.xzy * w.y + nz.xyz * w.z);
}

// ---------------------------------------------------------------------------------------------
// Material selection
// ---------------------------------------------------------------------------------------------

// Chooses the array layer and a second layer to blend toward, plus the blend factor.
// Slope is the deciding input almost everywhere: soil and snow cannot cling to a steep face, so a
// cliff exposes bare rock with no authoring, exactly as the generator's own material rule intends.
void sakritChooseLayers(uint material, vec3 n, float height, float variation,
                        out float layerA, out float layerB, out float mixAB)
{
    float slope = 1.0 - clamp(n.y, 0.0, 1.0);   // 0 flat, 1 vertical

    if (material == MAT_SOIL)
    {
        // Grass on gentle ground, bare soil where it steepens. The variation term makes the
        // transition wander instead of following a clean contour line.
        float bare = smoothstep(0.16, 0.46, slope + (variation - 0.5) * 0.16);
        layerA = float(LAYER_GRASS);
        layerB = float(LAYER_SOIL);
        mixAB  = bare;
    }
    else if (material == MAT_SNOW)
    {
        // Snow slides off anything steep, revealing the rock beneath.
        float blown = smoothstep(0.34, 0.62, slope + (variation - 0.5) * 0.18);
        layerA = float(LAYER_SNOW);
        layerB = float(LAYER_ROCK);
        mixAB  = blown;
    }
    else if (material == MAT_SAND)
    {
        float packed = smoothstep(0.30, 0.58, slope);
        layerA = float(LAYER_SAND);
        layerB = float(LAYER_GRAVEL);
        mixAB  = packed;
    }
    else if (material == MAT_GRAVEL)
    {
        float exposed = smoothstep(0.40, 0.70, slope);
        layerA = float(LAYER_GRAVEL);
        layerB = float(LAYER_ROCK);
        mixAB  = exposed;
    }
    else
    {
        // Rock, and anything unrecognised. Break it up with gravel in the hollows so a cliff is not
        // one uniform sheet of granite.
        float scree = smoothstep(0.34, 0.06, slope) * variation;
        layerA = float(LAYER_ROCK);
        layerB = float(LAYER_GRAVEL);
        mixAB  = clamp(scree, 0.0, 0.65);
    }
}

// Large-scale variation in [0,1], from the macro tap of the rock layer. Reusing an existing texture
// rather than adding a noise function keeps this to one fetch and guarantees it tiles.
float sakritMacroVariation(FrameConstants f, vec3 wp, vec3 w)
{
    vec4 m = sakritTriplanarSample(f.materialAlbedo, f.materialSampler, float(LAYER_ROCK),
                                   wp, w, SAKRIT_MACRO_SCALE);
    return clamp(dot(m.rgb, vec3(0.30, 0.59, 0.11)) * 2.4, 0.0, 1.0);
}

// ---------------------------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------------------------

SakritSurface sakritEvaluateSurface(FrameConstants f, uint material, vec3 geoNormal,
                                    vec3 patternPos, float height)
{
    vec3 w = sakritTriplanarWeights(geoNormal);
    float variation = sakritMacroVariation(f, patternPos, w);

    float layerA, layerB, mixAB;
    sakritChooseLayers(material, geoNormal, height, variation, layerA, layerB, mixAB);

    vec4 albedoA = sakritTriplanarSample(f.materialAlbedo, f.materialSampler, layerA, patternPos, w, SAKRIT_TEXEL_SCALE);
    vec4 ormA    = sakritTriplanarSample(f.materialOrm,    f.materialSampler, layerA, patternPos, w, SAKRIT_TEXEL_SCALE);
    vec3 normalA = sakritTriplanarNormal(f.materialNormal, f.materialSampler, layerA, patternPos, w, SAKRIT_TEXEL_SCALE, geoNormal);

    vec3 albedo    = albedoA.rgb;
    vec3 normal    = normalA;
    float rough    = ormA.g;
    float occ      = ormA.r;

    // Only pay for the second material where it actually contributes. The branch is coherent across
    // most of the screen, since large areas are pure grass or pure rock.
    if (mixAB > 0.01)
    {
        vec4 albedoB = sakritTriplanarSample(f.materialAlbedo, f.materialSampler, layerB, patternPos, w, SAKRIT_TEXEL_SCALE);
        vec4 ormB    = sakritTriplanarSample(f.materialOrm,    f.materialSampler, layerB, patternPos, w, SAKRIT_TEXEL_SCALE);
        vec3 normalB = sakritTriplanarNormal(f.materialNormal, f.materialSampler, layerB, patternPos, w, SAKRIT_TEXEL_SCALE, geoNormal);

        // Height-aware blend: the material whose surface stands proud wins the contested band, so the
        // transition follows crevices instead of being a linear crossfade through mud.
        float ha = albedoA.a + ormA.a;
        float hb = albedoB.a + ormB.a;
        float t = clamp(mixAB + (hb - ha) * 0.25, 0.0, 1.0);
        t = smoothstep(0.0, 1.0, t);

        albedo = mix(albedo, albedoB.rgb, t);
        normal = normalize(mix(normal, normalB, t));
        rough  = mix(rough, ormB.g, t);
        occ    = mix(occ, ormB.r, t);
    }

    // Macro variation: a slow lightness drift over tens of metres, which real ground has and a tiled
    // texture never does. Without it the repeat reads immediately at distance.
    albedo *= mix(0.74, 1.20, variation);

    SakritSurface s;
    s.albedo = albedo;
    s.normal = normal;
    s.roughness = clamp(rough, 0.04, 1.0);
    s.occlusion = occ;
    return s;
}

#endif // SAKRIT_SURFACE_GLSL
