#version 460
// Sky background fragment: evaluate the shared analytic sky along the interpolated view ray.
#include "include/frame.glsl"
#include "include/sky.glsl"

layout(push_constant, scalar) uniform PushConstants
{
    uint frameHandle;
} pc;

layout(location = 0) in vec3 vViewDir;
layout(location = 0) out vec4 outColor;

void main()
{
    FrameConstants f = SAKRIT_FRAME(pc.frameHandle);
    vec3 dir = normalize(vViewDir);
    outColor = vec4(sakritTonemap(sakritSkyRadiance(f, dir), f.exposure), 1.0);
}
