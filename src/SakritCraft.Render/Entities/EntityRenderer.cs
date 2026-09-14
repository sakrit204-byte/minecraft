using System.Numerics;
using System.Runtime.InteropServices;
using SakritCraft.Render.Descriptors;
using SakritCraft.Render.Memory;
using SakritCraft.Render.Pipelines;
using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;

namespace SakritCraft.Render.Entities;

/// <summary>One drawn box. Mirrors the shader's EntityInstance; 32 bytes, scalar layout.</summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct EntityInstance
{
    /// <summary>Camera-relative centre, so no large float ever reaches the GPU.</summary>
    public Vector3 RelativePosition;
    public float Yaw;
    public Vector3 HalfExtent;
    /// <summary>Packed 0xAABBGGRR, authored in sRGB.</summary>
    public uint Colour;
}

/// <summary>
/// Draws every simulated entity as an oriented box.
/// <para>
/// Stand-in shapes on purpose. What the game needs first is to be able to see that a creature
/// is where the simulation says it is, moving as the simulation says it moves; a model would
/// prove none of that any better and would take far longer to be wrong in.
/// </para>
/// <para>
/// Geometry comes from the vertex index alone, so an entity costs one instance record and
/// nothing else: no mesh, no vertex buffer, no streaming.
/// </para>
/// </summary>
public sealed unsafe class EntityRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct EntityPushConstants
    {
        public uint FrameHandle;
        public uint InstanceBuffer;
        public uint Pad0;
        public uint Pad1;
    }

    private const int VerticesPerBox = 36;

    private readonly VulkanDevice _device;
    private readonly DescriptorHeap _heap;
    private readonly GraphicsPipeline _pipeline;
    private readonly GpuBuffer[] _instanceBuffers;
    private readonly BindlessHandle[] _instanceHandles;
    private readonly EntityInstance[] _staged;

    private int _count;

    public int Capacity { get; }
    public int DrawnLastFrame { get; private set; }

    public EntityRenderer(VulkanDevice device, SakritCraft.Render.Pipelines.PipelineCache pipelines,
                          DescriptorHeap heap, GpuAllocator allocator,
                          Format colorFormat, Format depthFormat, int framesInFlight, int capacity = 8192)
    {
        _device = device;
        _heap = heap;
        Capacity = capacity;
        _staged = new EntityInstance[capacity];

        _pipeline = pipelines.CreateGraphics(new GraphicsPipelineDesc
        {
            Name = "Entities",
            VertexShader = "entity.vert",
            FragmentShader = "entity.frag",
            ColorFormat = colorFormat,
            DepthFormat = depthFormat,
            DepthTest = true,
            DepthWrite = true,
            DepthCompare = CompareOp.GreaterOrEqual,   // reverse-Z
            CullMode = CullModeFlags.BackBit,
            FrontFace = FrontFace.CounterClockwise,
        });

        _instanceBuffers = new GpuBuffer[framesInFlight];
        _instanceHandles = new BindlessHandle[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _instanceBuffers[i] = new GpuBuffer(device, allocator,
                (ulong)(capacity * sizeof(EntityInstance)),
                BufferUsageFlags.StorageBufferBit, MemoryUsage.Staging, $"Entities.Instances{i}");
            _instanceHandles[i] = heap.RegisterStorageBuffer(
                _instanceBuffers[i].Handle, 0, _instanceBuffers[i].Size);
        }
    }

    /// <summary>Starts a frame's instance list.</summary>
    public void Begin() => _count = 0;

    /// <summary>Adds one box. Silently drops anything past capacity rather than growing mid-frame.</summary>
    public void Add(in EntityInstance instance)
    {
        if (_count >= Capacity) return;
        _staged[_count++] = instance;
    }

    /// <summary>Uploads and draws this frame's instances.</summary>
    public void Draw(CommandBuffer cmd, uint frameHandle, int frameIndex)
    {
        DrawnLastFrame = _count;
        if (_count == 0) return;

        _instanceBuffers[frameIndex].Write<EntityInstance>(_staged.AsSpan(0, _count));

        var vk = _device.Vk;
        _device.BeginLabel(cmd, "Entities");
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline.Handle);

        var push = new EntityPushConstants
        {
            FrameHandle = frameHandle,
            InstanceBuffer = _instanceHandles[frameIndex].Index,
        };
        vk.CmdPushConstants(cmd, _heap.PipelineLayout, ShaderStageFlags.All, 0,
            (uint)sizeof(EntityPushConstants), &push);

        // One instanced draw for every entity in the world.
        vk.CmdDraw(cmd, VerticesPerBox, (uint)_count, 0, 0);
        _device.EndLabel(cmd);
    }

    public void Dispose()
    {
        foreach (BindlessHandle handle in _instanceHandles) _heap.Free(handle);
        foreach (GpuBuffer buffer in _instanceBuffers) buffer.Dispose();
    }

    /// <summary>Colours for each species, so a creature is identifiable before it has a model.</summary>
    public static uint ColourFor(ushort speciesId) => speciesId switch
    {
        Content.Creatures.CreatureCatalog.Husk => 0xFF3F5A6B,          // sallow grey-green
        Content.Creatures.CreatureCatalog.Bonewalker => 0xFFD8DEE4,    // bone white
        Content.Creatures.CreatureCatalog.DeepStalker => 0xFF2A1F38,   // near-black violet
        Content.Creatures.CreatureCatalog.Gloomweaver => 0xFF2B2B45,   // dark indigo
        Content.Creatures.CreatureCatalog.Bloater => 0xFF2E6B35,       // sickly green
        Content.Creatures.CreatureCatalog.Deer => 0xFF4A78A8,          // warm brown (BGR packed)
        Content.Creatures.CreatureCatalog.Boar => 0xFF32405C,          // dark russet
        Content.Creatures.CreatureCatalog.Villager => 0xFF6B93BE,      // linen
        _ => 0xFF888888,
    };

    /// <summary>Colour for a dropped item, keyed loosely by category so a pile reads at a glance.</summary>
    public static uint ColourForItem(ushort itemId) => itemId switch
    {
        >= 20 and <= 28 => 0xFF4AA8D8,    // ores and metals: warm gold
        >= 40 and <= 46 => 0xFF3C6E96,    // organic: brown
        >= 60 and <= 64 => 0xFF4F5FC8,    // food: red
        >= 80 and <= 91 => 0xFFBEBEBE,    // tools: steel
        _ => 0xFF9E9E9E,
    };
}
