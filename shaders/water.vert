#version 460
// Sea surface. A camera-centred grid generated entirely from the vertex index, with no vertex or
// index buffer at all: the sea is a plane, so its geometry is a function of the index and nothing
// needs to be stored or streamed.
//
// Ring spacing grows exponentially with distance so that triangles stay a roughly constant size on
// screen. A uniform grid would either be too coarse at the shoreline, where the eye is, or waste
// hundreds of thousands of triangles on water kilometres away that covers a few pixels.
#include "include/frame.glsl"

layout(push_constant, scalar) uniform PushConstants
{
    uint  frameHandle;
    float seaLevelRelative;   // sea level minus camera height, so this stays a small float
    float time;
    float maxRadius;
    uint  gridCells;
} pc;

layout(location = 0) out vec3 vRelPos;

void main()
{
    uint quad = uint(gl_VertexIndex) / 6u;
    uint corner = uint(gl_VertexIndex) % 6u;

    uint n = pc.gridCells;
    uvec2 cell = uvec2(quad % n, quad / n);

    // Two triangles per cell.
    const ivec2 offsets[6] = ivec2[6](
        ivec2(0, 0), ivec2(1, 0), ivec2(1, 1),
        ivec2(0, 0), ivec2(1, 1), ivec2(0, 1));

    vec2 grid = (vec2(ivec2(cell) + offsets[corner]) / float(n)) * 2.0 - 1.0;

    // Square rings: the Chebyshev norm keeps the grid a set of nested squares, which tessellates
    // without the singularity a radial layout has at the centre.
    float ring = max(abs(grid.x), abs(grid.y));
    vec2 direction = ring > 1e-6 ? grid / ring : vec2(0.0);

    const float k = 6.0;
    float radius = pc.maxRadius * (exp(k * ring) - 1.0) / (exp(k) - 1.0);

    vec3 rel = vec3(direction.x * radius, pc.seaLevelRelative, direction.y * radius);

    FrameConstants f = SAKRIT_FRAME(pc.frameHandle);
    gl_Position = f.viewProj * vec4(rel, 1.0);
    vRelPos = rel;
}
