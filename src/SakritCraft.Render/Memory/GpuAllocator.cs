using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;

namespace SakritCraft.Render.Memory;

/// <summary>Where a resource lives and who writes it. Drives memory-type selection.</summary>
public enum MemoryUsage
{
    /// <summary>DEVICE_LOCAL only. Textures, vertex/index buffers, GPU-written data. Filled via staging or compute.</summary>
    GpuOnly,
    /// <summary>HOST_VISIBLE|HOST_COHERENT, DEVICE_LOCAL preferred (ReBAR / 256 MiB BAR heap). Per-frame constants, staging.</summary>
    CpuToGpu,
    /// <summary>HOST_VISIBLE, HOST_CACHED preferred. Readback (screenshots, GPU timing exports, picking).</summary>
    GpuToCpu,
    /// <summary>
    /// HOST_VISIBLE|HOST_COHERENT and deliberately <i>not</i> DEVICE_LOCAL when a choice exists. Upload staging
    /// is written once by the CPU and read once by the DMA engine, so it belongs in system RAM: putting it in
    /// the 256 MiB BAR heap would evict the per-frame constants that genuinely benefit from being there.
    /// </summary>
    Staging,
}

/// <summary>
/// A sub-range of a <see cref="MemoryBlock"/>. Plain data: callers store it and hand it back to
/// <see cref="GpuAllocator.Free"/>. <see cref="MappedPtr"/> already includes the offset.
/// </summary>
public readonly unsafe struct GpuAllocation
{
    public readonly DeviceMemory Memory;
    public readonly ulong Offset;
    public readonly ulong Size;
    public readonly byte* MappedPtr;
    public readonly uint MemoryTypeIndex;
    public readonly bool IsCoherent;
    internal readonly int BlockId;

    internal GpuAllocation(DeviceMemory memory, ulong offset, ulong size, byte* mapped, uint typeIndex, bool coherent, int blockId)
    {
        Memory = memory;
        Offset = offset;
        Size = size;
        MappedPtr = mapped;
        MemoryTypeIndex = typeIndex;
        IsCoherent = coherent;
        BlockId = blockId;
    }

    public bool IsValid => Memory.Handle != 0;
    public bool IsMapped => MappedPtr != null;
}

/// <summary>Live counters for the overlay and for streaming budget decisions.</summary>
public struct AllocatorStats
{
    public int BlockCount;
    public int AllocationCount;
    public ulong ReservedBytes;
    public ulong UsedBytes;
    public ulong DeviceLocalReservedBytes;
    public ulong DeviceLocalUsedBytes;
}

/// <summary>
/// One VkDeviceMemory carved up by a sorted free-list (first fit, coalescing on free). Blocks are
/// homogeneous in resource class: a block holds only linear resources (buffers, linear images) or
/// only optimal-tiled images. That is how <c>bufferImageGranularity</c> is honoured: the rule only
/// constrains linear and non-linear resources sharing a block, and here they never do, so no
/// per-allocation page bookkeeping is needed. This is the same strategy VMA offers as its
/// "separate pools" mode; it costs a little fragmentation and buys a much simpler allocator.
/// </summary>
internal sealed unsafe class MemoryBlock
{
    private readonly RangeAllocator _ranges;

    public readonly int Id;
    public readonly DeviceMemory Memory;
    public readonly ulong Size;
    public readonly uint MemoryTypeIndex;
    public readonly bool IsLinear;
    public readonly bool IsCoherent;
    public readonly byte* Mapped;
    public ulong Used => _ranges.Used;
    public int AllocationCount => _ranges.AllocationCount;
    public bool IsEmpty => _ranges.IsEmpty;

    public MemoryBlock(int id, DeviceMemory memory, ulong size, uint typeIndex, bool linear, bool coherent, byte* mapped)
    {
        Id = id;
        Memory = memory;
        Size = size;
        MemoryTypeIndex = typeIndex;
        IsLinear = linear;
        IsCoherent = coherent;
        Mapped = mapped;
        _ranges = new RangeAllocator(size);
    }

    public bool TryAllocate(ulong size, ulong alignment, out ulong offset) => _ranges.TryAllocate(size, alignment, out offset);

    public void Free(ulong offset, ulong size) => _ranges.Free(offset, size);
}

/// <summary>
/// Device memory suballocator. Allocates large blocks with vkAllocateMemory (64 MiB device-local,
/// 16 MiB host-visible) and hands out ranges from them, so the driver sees hundreds of allocations
/// over a session instead of hundreds of thousands, and <c>maxMemoryAllocationCount</c> (4096 on many
/// drivers) is never a constraint. Requests larger than a block get a dedicated block of exactly their
/// size, still tracked here so teardown and statistics stay uniform.
///
/// Every block is allocated with <c>VK_MEMORY_ALLOCATE_DEVICE_ADDRESS_BIT</c> because buffer device
/// address is a required feature and any buffer may be addressed from a shader.
/// Host-visible blocks are persistently mapped; mapping per write is a driver round-trip for nothing.
/// Thread-safe: worldgen workers will allocate upload staging from their own threads.
/// </summary>
public sealed unsafe class GpuAllocator : IDisposable
{
    private const string Tag = "vk.alloc";
    private const ulong DeviceLocalBlockSize = 64UL * 1024 * 1024;
    private const ulong HostVisibleBlockSize = 16UL * 1024 * 1024;

    private readonly VulkanDevice _device;
    private readonly PhysicalDeviceMemoryProperties _memProps;
    private readonly object _gate = new();
    private readonly List<MemoryBlock?> _blocks = new(32);
    private int _nextBlockId;
    private bool _disposed;

    public GpuAllocator(VulkanDevice device)
    {
        _device = device;
        _memProps = device.MemoryProperties;
        RenderLog.Info(Tag, $"{_memProps.MemoryTypeCount} memory types, {_memProps.MemoryHeapCount} heaps, bufferImageGranularity {device.Capabilities.BufferImageGranularity}, max allocations {device.Capabilities.MaxMemoryAllocationCount}");
    }

    /// <summary>
    /// Allocates memory satisfying <paramref name="requirements"/>. <paramref name="linear"/> is true
    /// for buffers and LINEAR-tiled images, false for OPTIMAL-tiled images (see <see cref="MemoryBlock"/>).
    /// </summary>
    public GpuAllocation Allocate(in MemoryRequirements requirements, MemoryUsage usage, bool linear, string debugName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        uint typeIndex = SelectMemoryType(requirements.MemoryTypeBits, usage);
        var typeFlags = _memProps.MemoryTypes[(int)typeIndex].PropertyFlags;
        bool hostVisible = (typeFlags & MemoryPropertyFlags.HostVisibleBit) != 0;
        bool coherent = (typeFlags & MemoryPropertyFlags.HostCoherentBit) != 0;
        bool deviceLocal = (typeFlags & MemoryPropertyFlags.DeviceLocalBit) != 0;

        ulong alignment = Math.Max(requirements.Alignment, 1UL);
        if (hostVisible && !coherent)
        {
            // Flushes work on nonCoherentAtomSize multiples; aligning here keeps flush ranges inside the allocation.
            alignment = Math.Max(alignment, _device.Capabilities.NonCoherentAtomSize);
        }

        lock (_gate)
        {
            // First fit across existing compatible blocks.
            for (int i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (block is null || block.MemoryTypeIndex != typeIndex || block.IsLinear != linear)
                {
                    continue;
                }

                if (block.TryAllocate(requirements.Size, alignment, out ulong offset))
                {
                    return Wrap(block, offset, requirements.Size);
                }
            }

            // No room: new block.
            ulong blockSize = deviceLocal && !hostVisible ? DeviceLocalBlockSize : HostVisibleBlockSize;
            ulong heapSize = _memProps.MemoryHeaps[(int)_memProps.MemoryTypes[(int)typeIndex].HeapIndex].Size;
            blockSize = Math.Min(blockSize, Math.Max(heapSize / 8, 1UL << 20));
            bool dedicated = requirements.Size + alignment > blockSize;
            if (dedicated)
            {
                blockSize = requirements.Size;
            }

            var newBlock = CreateBlock(blockSize, typeIndex, linear, hostVisible, coherent, dedicated ? debugName : null);
            if (!newBlock.TryAllocate(requirements.Size, alignment, out ulong off))
            {
                throw new InvalidOperationException($"Fresh block of {blockSize} bytes cannot fit {requirements.Size} @ {alignment} for '{debugName}'.");
            }

            return Wrap(newBlock, off, requirements.Size);
        }
    }

    /// <summary>Returns the range to its block. Empty non-dedicated blocks are kept as a warm cache; use <see cref="Trim"/> to release them.</summary>
    public void Free(in GpuAllocation allocation)
    {
        if (!allocation.IsValid)
        {
            return;
        }

        lock (_gate)
        {
            var block = _blocks[allocation.BlockId];
            if (block is null || block.Memory.Handle != allocation.Memory.Handle)
            {
                throw new InvalidOperationException("GpuAllocation does not belong to this allocator or was already freed.");
            }

            block.Free(allocation.Offset, allocation.Size);
        }
    }

    /// <summary>Releases every block that holds no allocations. Call between streaming bursts, never per frame.</summary>
    public void Trim()
    {
        lock (_gate)
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (block is not null && block.IsEmpty)
                {
                    DestroyBlock(block);
                    _blocks[i] = null;
                }
            }
        }
    }

    public AllocatorStats GetStats()
    {
        var stats = new AllocatorStats();
        lock (_gate)
        {
            foreach (var block in _blocks)
            {
                if (block is null) continue;
                bool deviceLocal = (_memProps.MemoryTypes[(int)block.MemoryTypeIndex].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0;
                stats.BlockCount++;
                stats.AllocationCount += block.AllocationCount;
                stats.ReservedBytes += block.Size;
                stats.UsedBytes += block.Used;
                if (deviceLocal)
                {
                    stats.DeviceLocalReservedBytes += block.Size;
                    stats.DeviceLocalUsedBytes += block.Used;
                }
            }
        }

        return stats;
    }

    private GpuAllocation Wrap(MemoryBlock block, ulong offset, ulong size)
        => new(block.Memory, offset, size, block.Mapped != null ? block.Mapped + offset : null, block.MemoryTypeIndex, block.IsCoherent, block.Id);

    private MemoryBlock CreateBlock(ulong size, uint typeIndex, bool linear, bool hostVisible, bool coherent, string? dedicatedName)
    {
        var vk = _device.Vk;
        var flagsInfo = new MemoryAllocateFlagsInfo
        {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.AddressBit,
        };
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = &flagsInfo,
            AllocationSize = size,
            MemoryTypeIndex = typeIndex,
        };
        vk.AllocateMemory(_device.Device, &allocInfo, null, out DeviceMemory memory).Check("vkAllocateMemory");

        byte* mapped = null;
        if (hostVisible)
        {
            void* ptr;
            vk.MapMemory(_device.Device, memory, 0, size, 0, &ptr).Check("vkMapMemory");
            mapped = (byte*)ptr;
        }

        int id = _nextBlockId++;
        var block = new MemoryBlock(id, memory, size, typeIndex, linear, coherent, mapped);
        _blocks.Add(block);

        string name = dedicatedName is null
            ? $"Memory.Block[{id}] type{typeIndex} {(linear ? "linear" : "optimal")} {size >> 20}MiB"
            : $"Memory.Dedicated[{id}] {dedicatedName}";
        _device.SetObjectName(ObjectType.DeviceMemory, memory.Handle, name);
        RenderLog.Trace(Tag, $"New block: {name}");

        if (_blocks.Count > _device.Capabilities.MaxMemoryAllocationCount / 2)
        {
            RenderLog.Warn(Tag, $"{_blocks.Count} device memory blocks live; approaching maxMemoryAllocationCount {_device.Capabilities.MaxMemoryAllocationCount}.");
        }

        return block;
    }

    private void DestroyBlock(MemoryBlock block)
    {
        if (block.Mapped != null)
        {
            _device.Vk.UnmapMemory(_device.Device, block.Memory);
        }

        _device.Vk.FreeMemory(_device.Device, block.Memory, null);
    }

    private uint SelectMemoryType(uint typeBits, MemoryUsage usage)
    {
        MemoryPropertyFlags required, preferred, avoid = MemoryPropertyFlags.None;
        switch (usage)
        {
            case MemoryUsage.GpuOnly:
                required = MemoryPropertyFlags.DeviceLocalBit;
                preferred = MemoryPropertyFlags.None;
                break;
            case MemoryUsage.CpuToGpu:
                required = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                preferred = MemoryPropertyFlags.DeviceLocalBit;
                break;
            case MemoryUsage.GpuToCpu:
                required = MemoryPropertyFlags.HostVisibleBit;
                preferred = MemoryPropertyFlags.HostCachedBit | MemoryPropertyFlags.HostCoherentBit;
                break;
            case MemoryUsage.Staging:
                required = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                preferred = MemoryPropertyFlags.None;
                avoid = MemoryPropertyFlags.DeviceLocalBit;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(usage));
        }

        // Pass 0: required + preferred, without the avoided flags. Pass 1: required + all preferred.
        // Pass 2: required only. Pass 3 (GpuOnly only): anything allowed (an integrated GPU may expose a
        // single host-visible, device-local type).
        int best = avoid != MemoryPropertyFlags.None ? FindType(typeBits, required | preferred, avoid) : -1;
        if (best < 0) best = FindType(typeBits, required | preferred);
        if (best < 0) best = FindType(typeBits, required);
        if (best < 0 && usage == MemoryUsage.GpuOnly) best = FindType(typeBits, MemoryPropertyFlags.None);
        if (best < 0)
        {
            throw new InvalidOperationException($"No memory type satisfies typeBits 0x{typeBits:X} with {required}.");
        }

        return (uint)best;
    }

    private int FindType(uint typeBits, MemoryPropertyFlags flags, MemoryPropertyFlags avoid = MemoryPropertyFlags.None)
    {
        for (int i = 0; i < _memProps.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) == 0) continue;
            var props = _memProps.MemoryTypes[i].PropertyFlags;
            if ((props & flags) == flags && (props & avoid) == 0) return i;
        }

        return -1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            int leaked = 0;
            foreach (var block in _blocks)
            {
                if (block is null) continue;
                leaked += block.AllocationCount;
                DestroyBlock(block);
            }

            _blocks.Clear();
            if (leaked > 0)
            {
                RenderLog.Warn(Tag, $"{leaked} GPU allocation(s) were never freed before allocator teardown.");
            }
        }
    }
}
