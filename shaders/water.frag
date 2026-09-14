#version 460
// Sea shading: Fresnel between a deep body colour and a sky reflection, with a sun glint and
// animated wave normals. No refraction yet, which would need the terrain colour and depth read back
// as textures; at these viewing distances the reflection does nearly all the work.
#include "include/frame.glsl"
#include "include/sky.glsl"

layout(location = 0) in vec3 vRelPos;
layout(location = 0) out vec4 outColor;

layout(push_constant, scalar) uniform PushConstants
{
    uint  frameHandle;
    float seaLevelRelative;
    float time;
    float maxRadius;
    uint  gridCells;
} pc;

// One directional wave: returns its contribution to the surface slope. Summing a handful with
// incommensurable directions and wavelengths gives a surface that never visibly repeats, which a
// single scrolling normal map does within seconds.
vec2 waveSlope(vec2 p, vec2 direction, float wavelength, float amplitude, float speed, float time)
{
    float k = 6.28318530718 / wavelength;
    float phase = dot(p, direction) * k + time * speed * k;
    return direction * (amplitude * k * cos(phase));
}

void main()
{
    FrameConstants f = SAKRIT_FRAME(pc.frameHandle);

    vec3 viewDir = normalize(-vRelPos);
    float distance = length(vRelPos);

    // World-space wave coordinates, wrapped so precision holds anywhere in the world.
    vec2 p = vRelPos.xz + f.cameraPosWrapped.xz;

    // Fade detail out with distance: past a few hundred metres the waves are far below a pixel and
    // keeping them only produces a boiling shimmer that no filtering removes.
    float detail = clamp(1.0 - (distance - 120.0) / 900.0, 0.0, 1.0);

    vec2 slope = vec2(0.0);
    slope += waveSlope(p, normalize(vec2( 1.0,  0.25)), 31.0, 0.34, 0.55, pc.time);
    slope += waveSlope(p, normalize(vec2(-0.4,  1.0 )), 17.0, 0.18, 0.75, pc.time);
    slope += waveSlope(p, normalize(vec2( 0.7, -0.8 )),  8.5, 0.075, 1.05, pc.time) * detail;
    slope += waveSlope(p, normalize(vec2(-1.0, -0.3 )),  3.7, 0.028, 1.6,  pc.time) * detail;

    vec3 n = normalize(vec3(-slope.x, 1.0, -slope.y));
    // Flatten toward the horizon so distant water reads as a calm sheet rather than noise.
    n = normalize(mix(vec3(0.0, 1.0, 0.0), n, detail * 0.85 + 0.15));

    float ndotv = max(dot(n, viewDir), 1e-4);

    // Schlick Fresnel for water: 2% reflectance head-on, rising to a mirror at grazing angles. This
    // one term is most of why water looks like water.
    float fresnel = 0.02 + 0.98 * pow(1.0 - ndotv, 5.0);

    vec3 reflectDir = reflect(-viewDir, n);
    reflectDir.y = abs(reflectDir.y);            // never sample below the horizon
    vec3 reflection = sakritSkyRadiance(f, reflectDir);

    // Body colour: what comes back out after scattering in the water itself.
    const vec3 shallow = vec3(0.055, 0.135, 0.145);
    const vec3 deep    = vec3(0.006, 0.026, 0.050);
    vec3 body = mix(shallow, deep, clamp(distance / 600.0, 0.0, 1.0));
    body *= f.sunColor * f.sunIntensity * 0.045 + mix(f.skyHorizon, f.skyZenith, 0.5) * 0.5;

    vec3 color = mix(body, reflection, fresnel);

    // Sun glint. A tight GGX lobe on the wave normals gives the broken sparkle track that a smooth
    // plane cannot produce.
    vec3 halfVec = normalize(f.sunDirection + viewDir);
    float ndoth = max(dot(n, halfVec), 0.0);
    float roughness = mix(0.02, 0.10, 1.0 - detail);
    float a = roughness * roughness;
    float a2 = a * a;
    float d = ndoth * ndoth * (a2 - 1.0) + 1.0;
    float spec = a2 / max(3.14159265 * d * d, 1e-7);
    color += f.sunColor * f.sunIntensity * spec * fresnel * max(dot(n, f.sunDirection), 0.0);

    float fog = sakritFogAmount(f, vRelPos);
    color = mix(color, sakritFogColor(f, -viewDir), fog);

    outColor = vec4(sakritTonemap(color, f.exposure), 1.0);
}
