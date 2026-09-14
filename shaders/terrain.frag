#version 460
// Terrain shading, M2: directional sun (Lambert) plus hemispherical sky ambient, flat material colours
// chosen from the vertex material id with a slope test, and aerial-perspective fog matched to the sky.
// Everything here is a stand-in for the G-buffer + ray-traced path in docs section 08; what must
// survive is the camera-relative input and the material id on the vertex.
#include "include/frame.glsl"
#include "include/sky.glsl"
#include "include/surface.glsl"

layout(location = 0) in vec3 vRelPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) flat in uint vMaterial;

layout(location = 0) out vec4 outColor;

layout(push_constant, scalar) uniform PushConstants
{
    uint  frameHandle;
    uint  vertexBuffer;
    uint  pad0;
    uint  pad1;
    vec3  chunkOffset;
    float pad2;
} pc;

void main()
{
    FrameConstants f = SAKRIT_FRAME(pc.frameHandle);

    // Interpolated snorm normals shorten toward silhouettes; renormalise before lighting.
    vec3 n = normalize(vNormal);
    vec3 viewDir = normalize(vRelPos);   // camera sits at the origin
    // Pattern coordinates: camera-relative position plus the wrapped camera position stays small
    // everywhere in the world, so procedural detail never swims far from the origin (section 03).
    vec2 patternXZ = vRelPos.xz + f.cameraPosWrapped.xz;
    float height = f.cameraHeight + vRelPos.y;
    vec3 albedo = sakritTerrainAlbedo(vMaterial, n, patternXZ, height);

    // Direct sun. No shadows yet: section 08 replaces this with a ray query per pixel.
    float ndotl = max(dot(n, f.sunDirection), 0.0);
    vec3 direct = f.sunColor * f.sunIntensity * ndotl;

    // Hemisphere ambient: sky from above, bounce from below, so undersides of overhangs go warm-dark
    // instead of black and slopes facing away from the sun still read.
    float hemi = n.y * 0.5 + 0.5;
    vec3 skyAmbient = mix(f.skyHorizon, f.skyZenith, 0.5) * 0.7;
    vec3 ambient = mix(f.groundAmbient, skyAmbient, hemi);

    // A cheap wrap term for the light side of large forms, standing in for GI until probes exist.
    float wrap = max(dot(n, f.sunDirection) * 0.5 + 0.5, 0.0);
    vec3 fill = f.sunColor * 0.08 * wrap;

    vec3 color = albedo * (direct + ambient + fill);

    // Aerial perspective toward the sky behind the fragment.
    float fog = sakritFogAmount(f, vRelPos);
    color = mix(color, sakritFogColor(f, viewDir), fog);

    outColor = vec4(sakritTonemap(color, f.exposure), 1.0);
}
