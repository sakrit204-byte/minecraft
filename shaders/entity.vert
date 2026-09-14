#version 460
// Entity boxes. Geometry comes entirely from the vertex index against an instance record, so
// creatures and dropped items need no mesh, no vertex buffer and no upload beyond their transforms.
// Stand-in shapes: what matters at this stage is that the simulation is visible and correctly
// placed, not that it is pretty.
#include "include/frame.glsl"

// Mirrors SakritCraft.Render.Entities.EntityInstance (32 bytes, scalar layout).
struct EntityInstance
{
    vec3  relPos;      // camera-relative centre, metres
    float yaw;
    vec3  halfExtent;  // metres
    uint  colour;      // packed 0xAABBGGRR
};

SAKRIT_STORAGE_BUFFER(EntityInstances, { EntityInstance instances[]; });

layout(push_constant, scalar) uniform PushConstants
{
    uint frameHandle;
    uint instanceBuffer;
    uint pad0;
    uint pad1;
} pc;

layout(location = 0) out vec3 vRelPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec3 vColour;

// Unit cube corners, and the thirty-six indices that wind every face counter-clockwise when
// seen from outside.
const vec3 CORNERS[8] = vec3[8](
    vec3(-1,-1,-1), vec3( 1,-1,-1), vec3( 1, 1,-1), vec3(-1, 1,-1),
    vec3(-1,-1, 1), vec3( 1,-1, 1), vec3( 1, 1, 1), vec3(-1, 1, 1));

const int INDICES[36] = int[36](
    0,3,2, 0,2,1,     // -Z
    4,5,6, 4,6,7,     // +Z
    0,4,7, 0,7,3,     // -X
    1,2,6, 1,6,5,     // +X
    0,1,5, 0,5,4,     // -Y
    3,7,6, 3,6,2);    // +Y

const vec3 FACE_NORMALS[6] = vec3[6](
    vec3(0,0,-1), vec3(0,0,1), vec3(-1,0,0), vec3(1,0,0), vec3(0,-1,0), vec3(0,1,0));

void main()
{
    EntityInstance instance = g_EntityInstances[pc.instanceBuffer].instances[gl_InstanceIndex];

    vec3 local = CORNERS[INDICES[gl_VertexIndex]] * instance.halfExtent;
    vec3 normal = FACE_NORMALS[gl_VertexIndex / 6];

    float c = cos(instance.yaw), s = sin(instance.yaw);
    mat3 rotation = mat3(c, 0.0, s,
                         0.0, 1.0, 0.0,
                        -s, 0.0, c);

    vec3 rel = instance.relPos + rotation * local;

    FrameConstants f = SAKRIT_FRAME(pc.frameHandle);
    gl_Position = f.viewProj * vec4(rel, 1.0);

    vRelPos = rel;
    vNormal = rotation * normal;
    vColour = vec3(
        float((instance.colour      ) & 0xFFu),
        float((instance.colour >>  8) & 0xFFu),
        float((instance.colour >> 16) & 0xFFu)) / 255.0;
    // Authored as sRGB so the constants read naturally; decode to linear for shading.
    vColour = pow(vColour, vec3(2.2));
}
