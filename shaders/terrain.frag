#version 460
// Terrain shading: physically-based direct sun plus a hemispherical sky term, over triplanar
// procedural materials. Section 08 of the design replaces the direct term with a ray query and the
// ambient with an irradiance probe grid; the surface evaluation below is unaffected by either, which
// is why material work and lighting work can proceed independently.
#include "include/frame.glsl"
#include "include/sky.glsl"
#include "include/surface.glsl"
#include "include/shadows.glsl"

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

const float PI = 3.14159265359;

// Trowbridge-Reitz. The long tail is what makes rough stone read as stone rather than as plastic.
float distributionGGX(float ndoth, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float d = ndoth * ndoth * (a2 - 1.0) + 1.0;
    return a2 / max(PI * d * d, 1e-7);
}

// Smith height-correlated visibility, already divided by the 4*NdotL*NdotV of the specular
// denominator, so the caller multiplies rather than divides.
float visibilitySmith(float ndotv, float ndotl, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float v = ndotl * sqrt(ndotv * ndotv * (1.0 - a2) + a2);
    float l = ndotv * sqrt(ndotl * ndotl * (1.0 - a2) + a2);
    return 0.5 / max(v + l, 1e-6);
}

vec3 fresnelSchlick(vec3 f0, float vdoth)
{
    return f0 + (1.0 - f0) * pow(clamp(1.0 - vdoth, 0.0, 1.0), 5.0);
}

void main()
{
    FrameConstants f = SAKRIT_FRAME(pc.frameHandle);

    // Interpolated normals shorten toward silhouettes; renormalise before anything uses them.
    vec3 geoNormal = normalize(vNormal);
    vec3 viewDir = normalize(-vRelPos);          // camera sits at the origin, so this points at it

    // Pattern coordinates: camera-relative position plus the wrapped camera position stays small
    // everywhere in the world, so texture coordinates never lose precision far from the origin.
    vec3 patternPos = vRelPos + f.cameraPosWrapped;
    float height = f.cameraHeight + vRelPos.y;

    SakritSurface surf = sakritEvaluateSurface(f, vMaterial, geoNormal, patternPos, height);

    // Flatten the normal map with distance. Beyond a few tens of metres the detail is below a pixel,
    // and keeping it only produces specular aliasing that no amount of anti-aliasing removes.
    float distance = length(vRelPos);
    float detailFade = clamp(1.0 - (distance - 90.0) / 340.0, 0.0, 1.0);
    vec3 n = normalize(mix(geoNormal, surf.normal, detailFade));

    // Roughening with distance is the matching half of that fade: a surface whose bumps have been
    // averaged away is statistically rougher, and pretending otherwise makes distant hills glitter.
    float roughness = mix(1.0, surf.roughness, 0.55 + 0.45 * detailFade);

    vec3 lightDir = f.sunDirection;
    vec3 halfVec = normalize(lightDir + viewDir);

    float ndotl = max(dot(n, lightDir), 0.0);
    float ndotv = max(dot(n, viewDir), 1e-4);
    float ndoth = max(dot(n, halfVec), 0.0);
    float vdoth = max(dot(viewDir, halfVec), 0.0);

    // Terrain is entirely dielectric; 4% reflectance is the standard value for non-metals.
    const vec3 f0 = vec3(0.04);
    vec3 fresnel = fresnelSchlick(f0, vdoth);

    vec3 specular = fresnel * distributionGGX(ndoth, roughness) * visibilitySmith(ndotv, ndotl, roughness);
    vec3 diffuse = (1.0 - fresnel) * surf.albedo / PI;

    // Cascaded shadow maps. The geometric normal, not the mapped one, drives the bias: the offset
    // corrects a sampling error in the shadow map's own grid, which knows nothing about texture detail.
    float viewDepth = dot(vRelPos, f.cameraForward);
    float sunVisibility = sakritSunVisibility(
        SAKRIT_SHADOWS(f.shadowConstants), vRelPos, geoNormal, dot(geoNormal, lightDir), viewDepth);

    vec3 sunRadiance = f.sunColor * f.sunIntensity;
    vec3 direct = (diffuse + specular) * sunRadiance * ndotl * sunVisibility;

    // Hemispherical ambient: sky from above, bounced ground light from below, so the underside of an
    // overhang goes warm-dark rather than black and slopes facing away from the sun still read.
    float hemi = n.y * 0.5 + 0.5;
    vec3 skyAmbient = mix(f.skyHorizon, f.skyZenith, 0.55);
    vec3 ambient = mix(f.groundAmbient, skyAmbient, hemi) * surf.albedo * surf.occlusion;

    // Wrapped diffuse standing in for a single bounce of global illumination until probes exist.
    float wrap = max(dot(n, lightDir) * 0.5 + 0.5, 0.0);
    // The bounce term keeps a little light in shadow, because a shadowed surface in daylight is lit
    // by everything around it. Killing it entirely gives the black, cut-out shadows of early games.
    vec3 bounce = sunRadiance * surf.albedo * surf.occlusion * wrap * wrap * 0.10
                * mix(0.45, 1.0, sunVisibility);

    vec3 color = direct + ambient + bounce;

    // Aerial perspective toward the sky behind the fragment.
    float fog = sakritFogAmount(f, vRelPos);
    color = mix(color, sakritFogColor(f, -viewDir), fog);

    outColor = vec4(sakritTonemap(color, f.exposure), 1.0);
}
