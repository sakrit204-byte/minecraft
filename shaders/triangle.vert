#version 460
// M0 triangle: vertex pulling through the bindless storage-buffer array. There is no vertex input
// state in the pipeline; the push constant carries the handle of the buffer holding the vertices.
#include "include/bindless.glsl"

struct Vertex
{
    vec4 position;
    vec4 color;
};

SAKRIT_STORAGE_BUFFER(TriangleVertices, { Vertex vertices[]; });

// Mirrors Renderer.TrianglePushConstants (scalar layout, 16 bytes).
layout(push_constant, scalar) uniform PushConstants
{
    uint  vertexBuffer;
    float time;
    float aspect;
    uint  pad;
} pc;

layout(location = 0) out vec3 vColor;

void main()
{
    Vertex v = g_TriangleVertices[pc.vertexBuffer].vertices[gl_VertexIndex];

    float c = cos(pc.time);
    float s = sin(pc.time);
    vec2 p = vec2(v.position.x * c - v.position.y * s,
                  v.position.x * s + v.position.y * c);
    p.x /= pc.aspect;

    // Vulkan clip space has +Y down; flip so "up" in the vertex data is up on screen.
    gl_Position = vec4(p.x, -p.y, 0.0, 1.0);
    vColor = v.color.rgb;
}
