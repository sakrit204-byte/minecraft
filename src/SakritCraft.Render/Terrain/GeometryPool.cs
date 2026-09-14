using SakritCraft.Render.Descriptors;
using SakritCraft.Render.Memory;
using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SakritCraft.Render.Terrain;

/// <summary>A sub-range of one pool buffer. <see cref="PoolIndex"/> selects the buffer, and therefore the bindless handle.</summary>
public readonly record struct GeometryRange(int PoolIndex, ulong Offset, ulong Size)
{
    public bool IsValid => Size != 0;
}

/// <summary>
/// Device-local geometry storage for chunk meshes: a handful of large VkBuffers, each carved into
/// per-chunk ranges by a <see cref="RangeAllocator"/>. One buffer per chunk would mean thousands of
/// VkBuffers and bindless slots churning as chunks stream; one range per chunk inside a few big
/// buffers means a chunk draw is a handle, an offset and a count, which is precisely the shape an
/// indirect draw command has. That is the door left open for GPU-driven culling in section 06.
///
/// Buffers are added on demand and never shrink; an empty buffer stays as warm capacity for the next
/// streaming burst. Every buffer carries TRANSFER_DST for the upload path; vertex pools also carry
/// STORAGE_BUFFER and a bindless handle for vertex pulling, index pools carry INDEX_BUFFER.
/// </summary>
public sealed class GeometryPool : IDisposable
{
    private const string Tag = "terrain.pool";

    private sealed class PoolBuffer
    {
        public required GpuBuffer Buffer;
        public required RangeAllocator Ranges;
        public required BindlessHandle Handle;
    }

    private readonly VulkanDevice _device;
    private readonly GpuAllocator _allocator;
    private readonly DescriptorHeap? _heap;
    private readonly ulong _bufferBytes;
    private readonly BufferUsageFlags _usage;
    private readonly string _name;
    private readonly List<PoolBuffer> _buffers = new(4);
    private bool _disposed;

    public int BufferCount => _buffers.Count;
    public ulong UsedBytes
    {
        get
        {
            ulong total = 0;
            foreach (var b in _buffers) total += b.Ranges.Used;
            return total;
        }
    }

    public ulong ReservedBytes => (ulong)_buffers.Count * _bufferBytes;

    /// <param name="heap">When given, each buffer is registered as a bindless storage buffer (vertex pools). Null for index pools.</param>
    public GeometryPool(VulkanDevice device, GpuAllocator allocator, DescriptorHeap? heap, ulong bufferBytes, BufferUsageFlags usage, string name)
    {
        _device = device;
        _allocator = allocator;
        _heap = heap;
        _bufferBytes = bufferBytes;
        _usage = usage | BufferUsageFlags.TransferDstBit | (heap is not null ? BufferUsageFlags.StorageBufferBit : BufferUsageFlags.None);
        _name = name;
    }

    /// <summary>Finds room in an existing buffer or creates a new one. Throws only if a single request exceeds the buffer size.</summary>
    public GeometryRange Allocate(ulong size, ulong alignment)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        if (size + alignment > _bufferBytes)
        {
            throw new InvalidOperationException($"Geometry range of {size} bytes exceeds the {_name} pool buffer size {_bufferBytes}.");
        }

        for (int i = 0; i < _buffers.Count; i++)
        {
            if (_buffers[i].Ranges.TryAllocate(size, alignment, out ulong offset))
            {
                return new GeometryRange(i, offset, size);
            }
        }

        var created = AddBuffer();
        if (!created.Ranges.TryAllocate(size, alignment, out ulong off))
        {
            throw new InvalidOperationException($"Fresh {_name} pool buffer cannot fit {size} bytes.");
        }

        return new GeometryRange(_buffers.Count - 1, off, size);
    }

    /// <summary>Returns a range. The caller guarantees the GPU is done with it (defer through the frame timeline).</summary>
    public void Free(in GeometryRange range)
    {
        if (!range.IsValid)
        {
            return;
        }

        _buffers[range.PoolIndex].Ranges.Free(range.Offset, range.Size);
    }

    public Buffer BufferOf(int poolIndex) => _buffers[poolIndex].Buffer.Handle;
    public BindlessHandle HandleOf(int poolIndex) => _buffers[poolIndex].Handle;

    private PoolBuffer AddBuffer()
    {
        int index = _buffers.Count;
        var buffer = new GpuBuffer(_device, _allocator, _bufferBytes, _usage, MemoryUsage.GpuOnly, $"{_name}[{index}]");
        var handle = _heap is not null
            ? _heap.RegisterStorageBuffer(buffer.Handle, 0, buffer.Size)
            : BindlessHandle.Invalid(BindlessKind.StorageBuffer);
        var pool = new PoolBuffer { Buffer = buffer, Ranges = new RangeAllocator(_bufferBytes), Handle = handle };
        _buffers.Add(pool);
        RenderLog.Info(Tag, $"New pool buffer {_name}[{index}]: {_bufferBytes >> 20} MiB" + (handle.IsValid ? $", bindless slot {handle.Index}" : ""));
        return pool;
    }

    /// <summary>Caller must have made the device idle.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        int live = 0;
        foreach (var b in _buffers)
        {
            live += b.Ranges.AllocationCount;
            if (b.Handle.IsValid) _heap!.Free(b.Handle);
            b.Buffer.Dispose();
        }

        if (live > 0)
        {
            RenderLog.Warn(Tag, $"{live} geometry range(s) in {_name} were still allocated at teardown.");
        }

        _buffers.Clear();
    }
}
