// Analytic sky shared by the sky pass (background) and the terrain pass (fog colour), so terrain in
// the distance fades into exactly the sky behind it. Not physically based yet: a zenith-to-horizon
// gradient, a sun disc with a soft glow, and a ground tone below the horizon. The Preetham/Hosek
// model or a precomputed LUT replaces this when section 08 lighting lands.
#ifndef SAKRIT_SKY_GLSL
#define SAKRIT_SKY_GLSL

#include "frame.glsl"

// Radiance of the sky in direction `dir` (unit, world axes, +Y up), in linear scene units.
vec3 sakritSkyRadiance(in FrameConstants f, vec3 dir)
{
    float up = dir.y;

    // Horizon band is brighter and warmer than the zenith. Below the horizon the sky is what distant,
    // fully fogged terrain would be: haze in the horizon colour, darkening only slowly with depression
    // angle, so a scene whose ground ends before the horizon dissolves into haze rather than into a
    // dark false ground plane.
    float horizonBlend = pow(1.0 - clamp(up, 0.0, 1.0), 4.0);
    vec3 sky = mix(f.skyZenith, f.skyHorizon, horizonBlend);
    vec3 haze = f.skyHorizon * 0.82;
    vec3 deep = mix(f.skyHorizon, f.groundAmbient, 0.5) * 0.7;
    vec3 below = mix(haze, deep, smoothstep(-0.10, -0.60, up));
    vec3 base = mix(sky, below, smoothstep(0.0, -0.05, up));

    // Sun: sharp disc plus a wide forward-scattering glow.
    float cosSun = dot(dir, f.sunDirection);
    float disc = smoothstep(0.9993, 0.9997, cosSun);
    float glow = pow(clamp(cosSun, 0.0, 1.0), 48.0) * 0.35 + pow(clamp(cosSun, 0.0, 1.0), 8.0) * 0.06;
    vec3 sun = f.sunColor * (disc * f.sunIntensity * 4.0 + glow * f.sunIntensity);

    return base + sun;
}

// Fog colour for a fragment seen along `viewDir`: the sky in that direction, slightly desaturated
// so distant terrain grays out the way real aerial perspective does.
vec3 sakritFogColor(in FrameConstants f, vec3 viewDir)
{
    vec3 sky = sakritSkyRadiance(f, normalize(vec3(viewDir.x, max(viewDir.y, 0.02), viewDir.z)));
    float luma = dot(sky, vec3(0.2126, 0.7152, 0.0722));
    return mix(sky, vec3(luma), 0.15);
}

// Transmittance-based distance fog with a height term: fog is thickest at and below the camera and
// thins exponentially above it, which keeps peaks crisp while valleys soften.
float sakritFogAmount(in FrameConstants f, vec3 relPos)
{
    float dist = length(relPos);
    float heightFactor = exp(-max(relPos.y, 0.0) * f.fogHeightFalloff);
    float optical = dist * f.fogDensity * mix(1.0, heightFactor, 0.85);
    return 1.0 - exp(-optical);
}

// Filmic tonemap (Hable / Uncharted 2 curve) with exposure. The swapchain is sRGB so the encode is
// done by the hardware on write; output here is linear.
vec3 sakritTonemap(vec3 color, float exposure)
{
    color *= exposure;
    const float A = 0.22, B = 0.30, C = 0.10, D = 0.20, E = 0.01, F = 0.30;
    const float W = 11.2;
    vec3 curr = ((color * (A * color + C * B) + D * E) / (color * (A * color + B) + D * F)) - E / F;
    float whiteScale = 1.0 / (((W * (A * W + C * B) + D * E) / (W * (A * W + B) + D * F)) - E / F);
    return clamp(curr * whiteScale, 0.0, 1.0);
}

#endif // SAKRIT_SKY_GLSL
