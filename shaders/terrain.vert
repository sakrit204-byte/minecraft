#version 460
// Terrain chunk vertex shader. Vertices are pulled from the chunk's range of a bindless geometry pool;
// positions are relative to the chunk origin, and the push constant carries the chunk origin relative
// to the camera (computed in double on the CPU), so no large float ever appears in this shader.
#include "include/frame.glsl"

// Mirrors TerrainRenderer.GpuTerrainVertex (16 bytes, scalar layout).
struct Vertex
{
    vec3 position;   // metres from the chunk origin
    uint nm;         // xyz: snorm8 normal, w: material id
};

SAKRIT_STORAGE_BUFFER(TerrainVertices, { Vertex vertices[]; });

// Mirrors TerrainRenderer.TerrainPushConstants (32 bytes, scalar layout).
layout(push_constant, scalar) uniform PushConstants
{
    uint  frameHandle;
    uint  vertexBuffer;
    uint  pad0;
    uint  pad1;
    vec3  chunkOffset;   // chunk origin minus camera position
    float pad2;
} pc;

layout(location = 0) out vec3 vRelPos;      // camera-relative position
layout(location = 1) out vec3 vNormal;
layout(location = 2) flat out uint vMaterial;

void main()
{
    // gl_VertexIndex already includes the draw's vertexOffset, so it indexes the pool directly.
    Vertex v = g_TerrainVertices[pc.vertexBuffer].vertices[gl_VertexIndex];
    FrameConstants f = SAKRIT_FRAME(pc.frameHandle);

    vec3 rel = pc.chunkOffset + v.position;
    gl_Position = f.viewProj * vec4(rel, 1.0);

    vRelPos = rel;
    vNormal = unpackSnorm4x8(v.nm).xyz;
    vMaterial = v.nm >> 24;
}
