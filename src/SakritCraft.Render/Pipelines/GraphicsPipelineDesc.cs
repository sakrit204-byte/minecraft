using Silk.NET.Vulkan;

namespace SakritCraft.Render.Pipelines;

/// <summary>
/// Immutable description of a graphics pipeline: shader files plus the fixed-function state that is
/// not dynamic. Vertex input is intentionally absent; every pipeline pulls vertices from bindless
/// storage buffers (vertex pulling), which is what the GPU meshing pipeline in docs section 06 writes
/// into and what mesh shaders need anyway. Viewport and scissor are always dynamic.
/// </summary>
public sealed record GraphicsPipelineDesc
{
    /// <summary>Debug name; also used for the VkPipeline object name.</summary>
    public required string Name { get; init; }
    /// <summary>Vertex shader path relative to the shader root, e.g. "triangle.vert".</summary>
    public required string VertexShader { get; init; }
    /// <summary>Fragment shader path relative to the shader root.</summary>
    /// <summary>
    /// Fragment shader file, or empty for a depth-only pipeline. A shadow pass writes nothing but
    /// depth, so it needs no fragment stage and no colour attachment at all.
    /// </summary>
    public string FragmentShader { get; init; } = string.Empty;
    /// <summary>Format of the single colour attachment (dynamic rendering has no render pass to infer it from).</summary>
    /// <summary>Colour target format, or Undefined for a depth-only pipeline.</summary>
    public Format ColorFormat { get; init; } = Format.Undefined;
    /// <summary>Depth attachment format, or Undefined for no depth.</summary>
    public Format DepthFormat { get; init; } = Format.Undefined;
    public PrimitiveTopology Topology { get; init; } = PrimitiveTopology.TriangleList;
    public CullModeFlags CullMode { get; init; } = CullModeFlags.None;
    public FrontFace FrontFace { get; init; } = FrontFace.CounterClockwise;
    public PolygonMode PolygonMode { get; init; } = PolygonMode.Fill;
    public bool BlendEnable { get; init; }
    public bool DepthTest { get; init; }
    public bool DepthWrite { get; init; }
    /// <summary>Reverse-Z by default: a floating-point depth buffer keeps far more precision that way.</summary>
    public CompareOp DepthCompare { get; init; } = CompareOp.GreaterOrEqual;

    /// <summary>Constant depth bias, in units of the depth buffer's smallest resolvable step.
    /// Shadow passes use this to push geometry away from the light and stop it shadowing itself.</summary>
    public float DepthBiasConstant { get; init; }

    /// <summary>Depth bias proportional to the slope, which is what handles grazing light angles
    /// where a constant bias is either useless or peels the shadow away from the caster.</summary>
    public float DepthBiasSlope { get; init; }
}

/// <summary>
/// A live graphics pipeline whose <see cref="Handle"/> may be swapped by hot reload. Read the handle on
/// the render thread only; <see cref="PipelineCache.PumpHotReload"/> is the only writer and it runs on
/// that thread. <see cref="Version"/> increments on every successful rebuild.
/// </summary>
public sealed class GraphicsPipeline
{
    public GraphicsPipelineDesc Desc { get; internal set; }
    public Pipeline Handle { get; internal set; }
    public int Version { get; internal set; }
    /// <summary>Absolute paths of every source file this pipeline was compiled from (shaders and their includes).</summary>
    public string[] Dependencies { get; internal set; } = Array.Empty<string>();

    internal bool BuildInFlight;
    internal bool Dirty;

    internal GraphicsPipeline(GraphicsPipelineDesc desc) => Desc = desc;
}
