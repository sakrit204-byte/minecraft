// Per-frame constants shared by every pass. Mirrors SakritCraft.Render.Frame.FrameConstants
// (scalar layout, 208 bytes); edit both together. Reached through the bindless storage-buffer array
// with the handle carried in each pass's push constants.
#ifndef SAKRIT_FRAME_GLSL
#define SAKRIT_FRAME_GLSL

#include "bindless.glsl"

struct FrameConstants
{
    mat4  viewProj;         // camera-relative: no translation, rotation + reverse-Z infinite projection
    vec3  cameraRight;      float tanHalfFovY;
    vec3  cameraUp;         float aspect;
    vec3  cameraForward;    float time;
    vec3  sunDirection;     float sunIntensity;
    vec3  sunColor;         float fogDensity;
    vec3  skyZenith;        float fogHeightFalloff;
    vec3  skyHorizon;       float exposure;
    vec3  groundAmbient;    float cameraHeight;   // absolute world Y of the camera, metres
    vec3  cameraPosWrapped; float pad1;           // camera position modulo 1024 m, for periodic surface patterns
};

SAKRIT_STORAGE_BUFFER(FrameBuffer, { FrameConstants frame; });

#define SAKRIT_FRAME(handle) g_FrameBuffer[(handle)].frame

#endif // SAKRIT_FRAME_GLSL
