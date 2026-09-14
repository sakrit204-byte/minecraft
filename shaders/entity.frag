#version 460
// Entity shading. Deliberately the same sun, ambient, shadow and fog terms as the terrain, so a
// creature sits in the scene rather than on top of it: matching the lighting matters far more for
// that than matching the level of surface detail.
#include "include/frame.glsl"
#include "include/sky.glsl"
#include "include/shadows.glsl"

layout(location = 0) in vec3 vRelPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec3 vColour;

layout(location = 0) out vec4 outColor;

layout(push_constant, scalar) uniform PushConstants
{
    uint frameHandle;
    uint instanceBuffer;
    uint pad0;
    uint pad1;
} pc;

void main()
{
    FrameConstants f = SAKRIT_FRAME(pc.frameHandle);

    vec3 n = normalize(vNormal);
    vec3 viewDir = normalize(-vRelPos);

    float ndotl = max(dot(n, f.sunDirection), 0.0);
    float viewDepth = dot(vRelPos, f.cameraForward);
    float sunVisibility = sakritSunVisibility(
        SAKRIT_SHADOWS(f.shadowConstants), vRelPos, n, dot(n, f.sunDirection), viewDepth);

    vec3 sunRadiance = f.sunColor * f.sunIntensity;
    vec3 direct = vColour * (1.0 / 3.14159265) * sunRadiance * ndotl * sunVisibility;

    float hemi = n.y * 0.5 + 0.5;
    vec3 skyAmbient = mix(f.skyHorizon, f.skyZenith, 0.55);
    vec3 ambient = mix(f.groundAmbient, skyAmbient, hemi) * vColour;

    // A rim term so a dark creature still reads against dark ground, which matters more here
    // than physical accuracy: an unlit silhouette at night is the thing the player must see.
    float rim = pow(1.0 - max(dot(n, viewDir), 0.0), 3.0);
    vec3 edge = skyAmbient * rim * 0.6;

    vec3 color = direct + ambient + edge;

    float fog = sakritFogAmount(f, vRelPos);
    color = mix(color, sakritFogColor(f, -viewDir), fog);

    outColor = vec4(sakritTonemap(color, f.exposure), 1.0);
}
