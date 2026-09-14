using System.Runtime.InteropServices;
using SakritCraft.Render.Descriptors;
using SakritCraft.Render.Pipelines;
using SakritCraft.Render.Vulkan;
using SakritCraft.World.Generation;
using Silk.NET.Vulkan;

namespace SakritCraft.Render.Water;

/// <summary>
/// The sea surface.
/// <para>
/// A camera-centred grid generated from the vertex index alone, with no buffers: the sea is a plane,
/// so its geometry is a pure function of the index and there is nothing to stream or store.
/// Correctness against the land comes free from the depth test. Where the ground stands above sea
/// level it is nearer to the camera and wins; where the ground drops below, the water plane is
/// nearer and wins. No clipping, no shoreline mesh, no special case.
/// </para>
/// </summary>
public sealed unsafe class WaterRenderer
{
    [StructLayout(LayoutKind.Sequential, Size = 20)]
    private struct WaterPushConstants
    {
        public uint FrameHandle;
        public float SeaLevelRelative;
        public float Time;
        public float MaxRadius;
        public uint GridCells;
    }

    private readonly VulkanDevice _device;
    private readonly DescriptorHeap _heap;
    private readonly GraphicsPipeline _pipeline;

    /// <summary>Cells along one edge of the grid. Vertex count is six times its square.</summary>
    public uint GridCells { get; init; } = 176;

    /// <summary>How far the sheet extends from the camera, in metres.</summary>
    public float MaxRadius { get; init; } = 9000.0f;

    /// <summary>Water level in world metres. Matches the generator's sea level.</summary>
    public double SeaLevel { get; init; } = LandformFields.SeaLevel;

    public uint TriangleCount => GridCells * GridCells * 2;

    public WaterRenderer(VulkanDevice device, SakritCraft.Render.Pipelines.PipelineCache pipelines, DescriptorHeap heap,
                         Format colorFormat, Format depthFormat)
    {
        _device = device;
        _heap = heap;

        _pipeline = pipelines.CreateGraphics(new GraphicsPipelineDesc
        {
            Name = "Water",
            VertexShader = "water.vert",
            FragmentShader = "water.frag",
            ColorFormat = colorFormat,
            DepthFormat = depthFormat,
            DepthTest = true,
            // Depth is tested but not written. Water is opaque here, and leaving the surface out of
            // the depth buffer keeps it from interfering with anything drawn after it.
            DepthWrite = false,
            DepthCompare = CompareOp.GreaterOrEqual,   // reverse-Z
            CullMode = CullModeFlags.None,             // visible from below when swimming
        });
    }

    public void Draw(CommandBuffer cmd, uint frameHandle, double cameraHeight, float time)
    {
        var vk = _device.Vk;
        _device.BeginLabel(cmd, "Water");
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline.Handle);

        var push = new WaterPushConstants
        {
            FrameHandle = frameHandle,
            SeaLevelRelative = (float)(SeaLevel - cameraHeight),
            Time = time,
            MaxRadius = MaxRadius,
            GridCells = GridCells,
        };
        vk.CmdPushConstants(cmd, _heap.PipelineLayout, ShaderStageFlags.All, 0,
            (uint)sizeof(WaterPushConstants), &push);

        vk.CmdDraw(cmd, GridCells * GridCells * 6, 1, 0, 0);
        _device.EndLabel(cmd);
    }
}
