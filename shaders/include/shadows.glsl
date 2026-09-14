// Cascaded shadow sampling. Mirrors SakritCraft.Render.Shadows.ShadowConstants (320 bytes, scalar).
#ifndef SAKRIT_SHADOWS_GLSL
#define SAKRIT_SHADOWS_GLSL

#include "bindless.glsl"

#define SAKRIT_CASCADES 4

struct ShadowConstants
{
    mat4 cascade[SAKRIT_CASCADES];   // camera-relative light view-projection
    vec4 splitDistances;             // view-space distance where each cascade ends
    vec4 texelWorldSize;             // metres per shadow texel, per cascade
    uint shadowMap;
    uint shadowSampler;
    float maxDistance;
    float strength;
    float invResolution;
};

SAKRIT_STORAGE_BUFFER(ShadowBuffer, { ShadowConstants shadows; });

#define SAKRIT_SHADOWS(handle) g_ShadowBuffer[(handle)].shadows

int sakritPickCascade(in ShadowConstants s, float viewDepth)
{
    for (int i = 0; i < SAKRIT_CASCADES; i++)
    {
        if (viewDepth < s.splitDistances[i]) return i;
    }
    return SAKRIT_CASCADES;   // beyond the last cascade: unshadowed
}

float sakritSampleCascade(in ShadowConstants s, int cascade, vec3 relPos, vec3 normal, float ndotl)
{
    // Normal-offset bias: move the sample point along the surface normal by roughly one shadow texel
    // before projecting. This is far more effective than depth bias alone on sloped ground, because
    // the error it corrects is a lateral sampling error, not a depth one. Scaling by the sine of the
    // light angle applies it where grazing light needs it and not where the sun is overhead.
    float texel = s.texelWorldSize[cascade];
    float slopeScale = clamp(1.0 - ndotl, 0.0, 1.0);
    vec3 offsetPos = relPos + normal * (texel * (0.9 + 2.2 * slopeScale));

    vec4 clip = s.cascade[cascade] * vec4(offsetPos, 1.0);
    vec3 ndc = clip.xyz / clip.w;

    // Outside this cascade's box entirely: treat as lit rather than guessing.
    if (any(greaterThan(abs(ndc.xy), vec2(1.0))) || ndc.z < 0.0 || ndc.z > 1.0)
    {
        return 1.0;
    }

    vec2 uv = ndc.xy * 0.5 + 0.5;
    float reference = ndc.z;

    // Rotated four-tap Poisson pattern on top of the hardware's own bilinear comparison, which gives
    // sixteen effective samples for four fetches. A regular grid at this tap count bands visibly.
    const vec2 taps[4] = vec2[4](
        vec2(-0.326, -0.406), vec2(0.519, -0.557),
        vec2(0.566,  0.677),  vec2(-0.660, 0.373));

    float radius = 1.35 * s.invResolution;
    float sum = 0.0;
    for (int i = 0; i < 4; i++)
    {
        sum += texture(
            sampler2DArrayShadow(g_TextureArrays[nonuniformEXT(s.shadowMap)],
                                 g_SamplersShadow[nonuniformEXT(s.shadowSampler)]),
            vec4(uv + taps[i] * radius, float(cascade), reference));
    }
    return sum * 0.25;
}

/// Returns how much sunlight reaches the point: 1 fully lit, 0 fully shadowed.
float sakritSunVisibility(in ShadowConstants s, vec3 relPos, vec3 normal, float ndotl, float viewDepth)
{
    if (ndotl <= 0.0) return 0.0;                 // facing away; no need to sample
    if (viewDepth >= s.maxDistance) return 1.0;   // past the cascades

    int cascade = sakritPickCascade(s, viewDepth);
    if (cascade >= SAKRIT_CASCADES) return 1.0;

    float visibility = sakritSampleCascade(s, cascade, relPos, normal, ndotl);

    // Cross-fade into the next cascade over the last tenth of this one, so the resolution change is
    // a gradient rather than a visible line drawn across the ground.
    float end = s.splitDistances[cascade];
    float begin = cascade == 0 ? 0.0 : s.splitDistances[cascade - 1];
    float band = (end - begin) * 0.12;
    if (cascade + 1 < SAKRIT_CASCADES && viewDepth > end - band)
    {
        float t = clamp((viewDepth - (end - band)) / band, 0.0, 1.0);
        float next = sakritSampleCascade(s, cascade + 1, relPos, normal, ndotl);
        visibility = mix(visibility, next, t);
    }

    // Fade the whole effect out at the far edge so terrain does not pop from shadowed to lit.
    float fade = clamp((s.maxDistance - viewDepth) / (s.maxDistance * 0.18), 0.0, 1.0);
    visibility = mix(1.0, visibility, fade);

    return mix(1.0, visibility, s.strength);
}

#endif // SAKRIT_SHADOWS_GLSL
