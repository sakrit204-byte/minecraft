using System.Runtime.InteropServices;
using SakritCraft.Render.Descriptors;
using SakritCraft.Render.Pipelines;
using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;
using PipelineCache = SakritCraft.Render.Pipelines.PipelineCache;

namespace SakritCraft.Render.Sky;

/// <summary>
/// Draws the analytic sky as the background. One oversized triangle at depth 0 (the far plane under
/// reverse-Z), depth-tested with GREATER_OR_EQUAL and no depth write, so it shades only the pixels the
/// terrain left untouched. Drawing it after the terrain instead of clearing to a colour first costs
/// nothing extra and skips the sky shader on every covered pixel.
/// </summary>
public sealed unsafe class SkyPass
{
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct SkyPushConstants
    {
        public uint FrameHandle;
        public uint Pad0, Pad1, Pad2;
    }

    private readonly VulkanDevice _device;
    private readonly DescriptorHeap _heap;
    private readonly GraphicsPipeline _pipeline;

    /// <summary>Colour format the pipeline was last built for; the renderer compares it after a swapchain recreate.</summary>
    public Format ColorFormat => _pipeline.Desc.ColorFormat;

    public SkyPass(VulkanDevice device, PipelineCache pipelines, DescriptorHeap heap, Format colorFormat, Format depthFormat)
    {
        _device = device;
        _heap = heap;
        _pipeline = pipelines.CreateGraphics(new GraphicsPipelineDesc
        {
            Name = "Sky",
            VertexShader = "sky.vert",
            FragmentShader = "sky.frag",
            ColorFormat = colorFormat,
            DepthFormat = depthFormat,
            DepthTest = true,
            DepthWrite = false,
            DepthCompare = CompareOp.GreaterOrEqual,
            CullMode = CullModeFlags.None,
        });
    }

    /// <summary>Records the sky draw. Call inside the main rendering pass, after opaque geometry.</summary>
    public void Draw(CommandBuffer cmd, uint frameHandle)
    {
        var vk = _device.Vk;
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline.Handle);
        var push = new SkyPushConstants { FrameHandle = frameHandle };
        vk.CmdPushConstants(cmd, _heap.PipelineLayout, ShaderStageFlags.All, 0, (uint)sizeof(SkyPushConstants), &push);
        vk.CmdDraw(cmd, 3, 1, 0, 0);
    }
}
