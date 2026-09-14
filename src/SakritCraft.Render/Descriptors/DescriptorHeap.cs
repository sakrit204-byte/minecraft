using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SakritCraft.Render.Descriptors;

/// <summary>Which array of the bindless set a handle indexes. Mirrors the binding numbers in shaders/include/bindless.glsl.</summary>
public enum BindlessKind : byte
{
    SampledImage = 0,
    Sampler = 1,
    StorageBuffer = 2,
    StorageImage = 3,
}

/// <summary>
/// Index into one of the bindless arrays. Shaders receive the <see cref="Index"/> through push constants
/// or other buffers and index the matching descriptor array with it. 32 bits so it packs into a uint
/// in GLSL without conversion.
/// </summary>
public readonly record struct BindlessHandle(uint Index, BindlessKind Kind)
{
    public const uint InvalidIndex = uint.MaxValue;
    public static BindlessHandle Invalid(BindlessKind kind) => new(InvalidIndex, kind);
    public bool IsValid => Index != InvalidIndex;
}

/// <summary>
/// The single bindless descriptor set the whole renderer binds at set 0, plus the pipeline layout
/// every pipeline shares. Four arrays (sampled images, samplers, storage buffers, storage images) are
/// UPDATE_AFTER_BIND and PARTIALLY_BOUND, so a resource can be registered while previous frames are
/// still executing and unwritten slots are legal as long as no shader reads them.
///
/// Why this shape: the design doc wants "one material array for the whole world, no per-draw
/// descriptor churn" (section 00). One set, bound once per command buffer, and every draw addresses
/// its resources by integer means the CPU cost of a draw no longer depends on how many textures it
/// uses. Push constants (128 bytes, all stages) carry the per-draw indices.
///
/// Array sizes are clamped to the device's update-after-bind limits. Slot indices come from a
/// free-list so freed handles are recycled; there is no generation counter yet, so a stale handle is
/// a silent wrong read rather than a detected error. That is the next hardening step once the
/// material system exists.
/// </summary>
public sealed unsafe class DescriptorHeap : IDisposable
{
    private const string Tag = "vk.bindless";

    /// <summary>Bytes of push constant space every pipeline layout exposes. 128 is the Vulkan-guaranteed minimum.</summary>
    public const uint PushConstantBytes = 128;

    private sealed class SlotAllocator
    {
        private readonly Stack<uint> _free = new();
        private uint _next;
        public readonly uint Capacity;
        public uint Live { get; private set; }

        public SlotAllocator(uint capacity) => Capacity = capacity;

        public uint Allocate(string what)
        {
            Live++;
            if (_free.TryPop(out uint index))
            {
                return index;
            }

            if (_next >= Capacity)
            {
                throw new InvalidOperationException($"Bindless {what} array is full ({Capacity} slots). Raise the cap or free unused handles.");
            }

            return _next++;
        }

        public void Free(uint index)
        {
            Live--;
            _free.Push(index);
        }
    }

    private readonly VulkanDevice _device;
    private readonly SlotAllocator[] _slots;
    private readonly DescriptorPool _pool;
    private bool _disposed;

    public DescriptorSetLayout Layout { get; }
    public DescriptorSet Set { get; }
    /// <summary>Set 0 = this heap, plus a 128-byte push-constant range visible to all stages. Shared by every pipeline.</summary>
    public PipelineLayout PipelineLayout { get; }
    public uint SampledImageCapacity => _slots[(int)BindlessKind.SampledImage].Capacity;
    public uint SamplerCapacity => _slots[(int)BindlessKind.Sampler].Capacity;
    public uint StorageBufferCapacity => _slots[(int)BindlessKind.StorageBuffer].Capacity;
    public uint StorageImageCapacity => _slots[(int)BindlessKind.StorageImage].Capacity;

    public DescriptorHeap(VulkanDevice device)
    {
        _device = device;
        var vk = device.Vk;
        var caps = device.Capabilities;

        // Generous but bounded. Real budgets: a few thousand textures for the material atlas, one
        // storage buffer per chunk mesh, handful of samplers.
        uint images = Math.Min(16384u, caps.MaxUpdateAfterBindSampledImages);
        uint samplers = Math.Min(64u, caps.MaxUpdateAfterBindSamplers);
        uint buffers = Math.Min(16384u, caps.MaxUpdateAfterBindStorageBuffers);
        uint storageImages = Math.Min(2048u, caps.MaxUpdateAfterBindStorageImages);

        _slots = new[]
        {
            new SlotAllocator(images),
            new SlotAllocator(samplers),
            new SlotAllocator(buffers),
            new SlotAllocator(storageImages),
        };

        var bindings = stackalloc DescriptorSetLayoutBinding[4];
        bindings[0] = new DescriptorSetLayoutBinding((uint)BindlessKind.SampledImage, DescriptorType.SampledImage, images, ShaderStageFlags.All);
        bindings[1] = new DescriptorSetLayoutBinding((uint)BindlessKind.Sampler, DescriptorType.Sampler, samplers, ShaderStageFlags.All);
        bindings[2] = new DescriptorSetLayoutBinding((uint)BindlessKind.StorageBuffer, DescriptorType.StorageBuffer, buffers, ShaderStageFlags.All);
        bindings[3] = new DescriptorSetLayoutBinding((uint)BindlessKind.StorageImage, DescriptorType.StorageImage, storageImages, ShaderStageFlags.All);

        var bindingFlags = stackalloc DescriptorBindingFlags[4];
        for (int i = 0; i < 4; i++)
        {
            bindingFlags[i] = DescriptorBindingFlags.UpdateAfterBindBit
                            | DescriptorBindingFlags.PartiallyBoundBit
                            | DescriptorBindingFlags.UpdateUnusedWhilePendingBit;
        }

        var flagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 4,
            PBindingFlags = bindingFlags,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            PNext = &flagsInfo,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 4,
            PBindings = bindings,
        };
        vk.CreateDescriptorSetLayout(device.Device, &layoutInfo, null, out DescriptorSetLayout layout).Check("vkCreateDescriptorSetLayout");
        Layout = layout;

        var poolSizes = stackalloc DescriptorPoolSize[4];
        poolSizes[0] = new DescriptorPoolSize(DescriptorType.SampledImage, images);
        poolSizes[1] = new DescriptorPoolSize(DescriptorType.Sampler, samplers);
        poolSizes[2] = new DescriptorPoolSize(DescriptorType.StorageBuffer, buffers);
        poolSizes[3] = new DescriptorPoolSize(DescriptorType.StorageImage, storageImages);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = 1,
            PoolSizeCount = 4,
            PPoolSizes = poolSizes,
        };
        vk.CreateDescriptorPool(device.Device, &poolInfo, null, out _pool).Check("vkCreateDescriptorPool");

        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        vk.AllocateDescriptorSets(device.Device, &allocInfo, out DescriptorSet set).Check("vkAllocateDescriptorSets");
        Set = set;

        var pushRange = new PushConstantRange(ShaderStageFlags.All, 0, PushConstantBytes);
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &layout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        vk.CreatePipelineLayout(device.Device, &pipelineLayoutInfo, null, out PipelineLayout pipelineLayout).Check("vkCreatePipelineLayout");
        PipelineLayout = pipelineLayout;

        device.SetObjectName(ObjectType.DescriptorSetLayout, layout.Handle, "Bindless.Layout");
        device.SetObjectName(ObjectType.DescriptorPool, _pool.Handle, "Bindless.Pool");
        device.SetObjectName(ObjectType.DescriptorSet, set.Handle, "Bindless.Set");
        device.SetObjectName(ObjectType.PipelineLayout, pipelineLayout.Handle, "Bindless.PipelineLayout");

        RenderLog.Info(Tag, $"Bindless set: {images} sampled images, {samplers} samplers, {buffers} storage buffers, {storageImages} storage images; push constants {PushConstantBytes} B");
    }

    /// <summary>Registers a storage buffer range and returns the index shaders use to reach it.</summary>
    public BindlessHandle RegisterStorageBuffer(Buffer buffer, ulong offset, ulong range)
    {
        uint index = _slots[(int)BindlessKind.StorageBuffer].Allocate("storage buffer");
        var info = new DescriptorBufferInfo(buffer, offset, range);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = Set,
            DstBinding = (uint)BindlessKind.StorageBuffer,
            DstArrayElement = index,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &info,
        };
        _device.Vk.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);
        return new BindlessHandle(index, BindlessKind.StorageBuffer);
    }

    /// <summary>Registers an image view for sampling (combine with a sampler handle in the shader).</summary>
    public BindlessHandle RegisterSampledImage(ImageView view, ImageLayout layout)
        => RegisterImage(view, layout, default, BindlessKind.SampledImage, DescriptorType.SampledImage);

    /// <summary>Registers an image view for storage (imageLoad/imageStore).</summary>
    public BindlessHandle RegisterStorageImage(ImageView view)
        => RegisterImage(view, ImageLayout.General, default, BindlessKind.StorageImage, DescriptorType.StorageImage);

    public BindlessHandle RegisterSampler(Sampler sampler)
        => RegisterImage(default, ImageLayout.Undefined, sampler, BindlessKind.Sampler, DescriptorType.Sampler);

    /// <summary>
    /// Releases a slot for reuse. The caller must ensure no in-flight frame still reads it (defer through
    /// the deletion queue alongside the resource itself).
    /// </summary>
    public void Free(BindlessHandle handle)
    {
        if (!handle.IsValid)
        {
            return;
        }

        _slots[(int)handle.Kind].Free(handle.Index);
    }

    /// <summary>Binds the heap at set 0. Once per command buffer per bind point is enough.</summary>
    public void Bind(CommandBuffer cmd, PipelineBindPoint bindPoint)
    {
        var set = Set;
        _device.Vk.CmdBindDescriptorSets(cmd, bindPoint, PipelineLayout, 0, 1, &set, 0, null);
    }

    private BindlessHandle RegisterImage(ImageView view, ImageLayout layout, Sampler sampler, BindlessKind kind, DescriptorType type)
    {
        uint index = _slots[(int)kind].Allocate(kind.ToString());
        var info = new DescriptorImageInfo(sampler, view, layout);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = Set,
            DstBinding = (uint)kind,
            DstArrayElement = index,
            DescriptorCount = 1,
            DescriptorType = type,
            PImageInfo = &info,
        };
        _device.Vk.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);
        return new BindlessHandle(index, kind);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var vk = _device.Vk;
        vk.DestroyPipelineLayout(_device.Device, PipelineLayout, null);
        vk.DestroyDescriptorPool(_device.Device, _pool, null); // frees the set
        vk.DestroyDescriptorSetLayout(_device.Device, Layout, null);
    }
}
