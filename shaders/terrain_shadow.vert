#version 460
// Depth-only terrain pass for one shadow cascade. Same vertex pulling as terrain.vert, but the
// transform comes from the push constants rather than the frame constants, because each cascade has
// its own light projection and they are all recorded into one command buffer.
//
// There is no fragment stage: the pass writes depth and nothing else.
#include "include/bindless.glsl"

struct Vertex
{
    vec3 position;   // metres from the chunk origin
    uint nm;         // xyz: snorm8 normal, w: material id
};

SAKRIT_STORAGE_BUFFER(TerrainVertices, { Vertex vertices[]; });

// Mirrors SakritCraft.Render.Shadows.ShadowPushConstants (96 bytes, scalar layout).
layout(push_constant, scalar) uniform PushConstants
{
    uint  vertexBuffer;
    uint  pad0;
    uint  pad1;
    uint  pad2;
    vec3  chunkOffset;   // chunk origin minus camera position
    float pad3;
    mat4  lightViewProj; // camera-relative, so it pairs with chunkOffset without any large floats
} pc;

void main()
{
    Vertex v = g_TerrainVertices[pc.vertexBuffer].vertices[gl_VertexIndex];
    vec3 rel = pc.chunkOffset + v.position;
    gl_Position = pc.lightViewProj * vec4(rel, 1.0);
}
