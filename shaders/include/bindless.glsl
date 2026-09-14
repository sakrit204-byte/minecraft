// SakritCraft bindless resource header.
// Mirrors SakritCraft.Render.Descriptors.DescriptorHeap: one descriptor set (set 0) with four
// runtime-sized arrays. Draws receive integer handles (push constants or buffers) and index these
// arrays; there is no per-draw descriptor binding anywhere in the engine.
#ifndef SAKRIT_BINDLESS_GLSL
#define SAKRIT_BINDLESS_GLSL

#extension GL_EXT_nonuniform_qualifier : require
#extension GL_EXT_scalar_block_layout : require
#extension GL_EXT_buffer_reference : require

#define SAKRIT_BINDLESS_SET            0
#define SAKRIT_BINDING_SAMPLED_IMAGES  0
#define SAKRIT_BINDING_SAMPLERS        1
#define SAKRIT_BINDING_STORAGE_BUFFERS 2
#define SAKRIT_BINDING_STORAGE_IMAGES  3

#define SAKRIT_INVALID_HANDLE 0xFFFFFFFFu

layout(set = SAKRIT_BINDLESS_SET, binding = SAKRIT_BINDING_SAMPLED_IMAGES) uniform texture2D g_Textures[];
layout(set = SAKRIT_BINDLESS_SET, binding = SAKRIT_BINDING_SAMPLERS)       uniform sampler   g_Samplers[];

// Layered images alias the same binding. The descriptor type is unchanged
// (VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE); only the GLSL view of it differs, which is legal and is the
// same trick the storage-buffer macros below use. A handle registered from a 2D-array image view is
// valid here and invalid in g_Textures, and vice versa.
layout(set = SAKRIT_BINDLESS_SET, binding = SAKRIT_BINDING_SAMPLED_IMAGES) uniform texture2DArray g_TextureArrays[];

// Comparison samplers alias the sampler binding for the same reason: VK_DESCRIPTOR_TYPE_SAMPLER
// covers both, and only the GLSL type differs. A handle registered from a sampler created with
// compareEnable belongs here and nowhere else.
layout(set = SAKRIT_BINDLESS_SET, binding = SAKRIT_BINDING_SAMPLERS) uniform samplerShadow g_SamplersShadow[];

// Storage buffers are declared at the use site because a GLSL buffer block needs a body. Several
// blocks with different layouts may alias binding 2; each is indexed by the handle of a buffer that
// was written with the matching layout. `scalar` layout matches C# StructLayout.Sequential exactly.
#define SAKRIT_STORAGE_BUFFER(Name, Body) \
    layout(set = SAKRIT_BINDLESS_SET, binding = SAKRIT_BINDING_STORAGE_BUFFERS, scalar) readonly buffer Name Body g_##Name[]

#define SAKRIT_RW_STORAGE_BUFFER(Name, Body) \
    layout(set = SAKRIT_BINDLESS_SET, binding = SAKRIT_BINDING_STORAGE_BUFFERS, scalar) buffer Name Body g_##Name[]

// Storage images need a format qualifier, so they are declared per use site too, e.g.
//   layout(set = 0, binding = SAKRIT_BINDING_STORAGE_IMAGES, rgba16f) uniform image2D g_HdrTargets[];

vec4 sakritSample(uint textureHandle, uint samplerHandle, vec2 uv)
{
    return texture(sampler2D(g_Textures[nonuniformEXT(textureHandle)], g_Samplers[nonuniformEXT(samplerHandle)]), uv);
}

vec4 sakritSampleLod(uint textureHandle, uint samplerHandle, vec2 uv, float lod)
{
    return textureLod(sampler2D(g_Textures[nonuniformEXT(textureHandle)], g_Samplers[nonuniformEXT(samplerHandle)]), uv, lod);
}

#endif // SAKRIT_BINDLESS_GLSL
