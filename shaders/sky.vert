#version 460
// Sky background: one triangle covering the screen, placed at depth 0, which is the far plane under
// reverse-Z. Drawn after the terrain with GREATER_OR_EQUAL depth testing so it only shades the pixels
// nothing else touched; the clear value is 0 and every terrain fragment is strictly greater.
#include "include/frame.glsl"

layout(push_constant, scalar) uniform PushConstants
{
    uint frameHandle;
} pc;

layout(location = 0) out vec3 vViewDir;

void main()
{
    // Oversized triangle: (-1,-1), (3,-1), (-1,3) covers NDC exactly once after clipping.
    vec2 ndc = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2) * 2.0 - 1.0;
    gl_Position = vec4(ndc, 0.0, 1.0);

    // Reconstruct the world-space view ray from the camera basis instead of inverting the infinite
    // projection. Vulkan NDC has +Y down, hence the sign on the up term.
    FrameConstants f = SAKRIT_FRAME(pc.frameHandle);
    vViewDir = f.cameraForward
             + f.cameraRight * (ndc.x * f.tanHalfFovY * f.aspect)
             - f.cameraUp    * (ndc.y * f.tanHalfFovY);
}
