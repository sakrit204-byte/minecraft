#version 460
// M0 triangle fragment shader. The swapchain is an sRGB format, so colours here are linear and the
// hardware performs the sRGB encode on write. Edit and save to see hot reload in action.
#include "include/bindless.glsl"

layout(location = 0) in vec3 vColor;
layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vec4(vColor, 1.0);
}
