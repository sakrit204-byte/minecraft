using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SakritCraft.Render.Memory;

/// <summary>
/// A VkBuffer bound to a <see cref="GpuAllocator"/> range, with its device address resolved at creation.
/// Every buffer gets <c>SHADER_DEVICE_ADDRESS</c> usage so the bindless layer can hand out raw pointers
/// (buffer references) as well as descriptor indices without a second buffer type.
/// </summary>
public sealed unsafe class GpuBuffer : IDisposable
{
    private readonly VulkanDevice _device;
    private readonly GpuAllocator _allocator;
    private GpuAllocation _allocation;
    private bool _disposed;

    public Buffer Handle { get; }
    public ulong Size { get; }
    public BufferUsageFlags Usage { get; }
    public MemoryUsage MemoryUsage { get; }
    /// <summary>GPU virtual address for use with GL_EXT_buffer_reference.</summary>
    public ulong DeviceAddress { get; }
    public string Name { get; }
    public bool IsMapped => _allocation.IsMapped;
    public byte* MappedPtr => _allocation.MappedPtr;

    public GpuBuffer(VulkanDevice device, GpuAllocator allocator, ulong size, BufferUsageFlags usage, MemoryUsage memoryUsage, string name)
    {
        if (size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Buffer size must be non-zero.");
        }

        _device = device;
        _allocator = allocator;
        Size = size;
        Usage = usage | BufferUsageFlags.ShaderDeviceAddressBit;
        MemoryUsage = memoryUsage;
        Name = name;

        var vk = device.Vk;
        var createInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = Usage,
            SharingMode = SharingMode.Exclusive,
        };
        vk.CreateBuffer(device.Device, &createInfo, null, out Buffer buffer).Check("vkCreateBuffer");
        Handle = buffer;

        vk.GetBufferMemoryRequirements(device.Device, buffer, out MemoryRequirements requirements);
        _allocation = allocator.Allocate(requirements, memoryUsage, linear: true, name);
        vk.BindBufferMemory(device.Device, buffer, _allocation.Memory, _allocation.Offset).Check("vkBindBufferMemory");

        var addressInfo = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = buffer };
        DeviceAddress = vk.GetBufferDeviceAddress(device.Device, &addressInfo);

        device.SetObjectName(ObjectType.Buffer, buffer.Handle, name);
    }

    /// <summary>Mapped bytes for host-visible buffers. Throws for GPU-only buffers rather than returning an empty span.</summary>
    public Span<byte> Mapped
    {
        get
        {
            if (!IsMapped)
            {
                throw new InvalidOperationException($"Buffer '{Name}' is not host-visible; upload through a staging buffer.");
            }

            return new Span<byte>(_allocation.MappedPtr, checked((int)Size));
        }
    }

    /// <summary>Copies <paramref name="data"/> into the mapped range at <paramref name="byteOffset"/>, flushing if the memory is not coherent.</summary>
    public void Write<T>(ReadOnlySpan<T> data, ulong byteOffset = 0) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(data);
        if (byteOffset + (ulong)bytes.Length > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(data), $"Write of {bytes.Length} bytes at {byteOffset} exceeds buffer '{Name}' size {Size}.");
        }

        bytes.CopyTo(Mapped.Slice((int)byteOffset));
        if (!_allocation.IsCoherent)
        {
            Flush(byteOffset, (ulong)bytes.Length);
        }
    }

    /// <summary>Makes host writes visible to the device for non-coherent memory. No-op for coherent memory.</summary>
    public void Flush(ulong byteOffset, ulong size)
    {
        if (_allocation.IsCoherent)
        {
            return;
        }

        ulong atom = _device.Capabilities.NonCoherentAtomSize;
        ulong start = (_allocation.Offset + byteOffset) & ~(atom - 1);
        ulong end = Math.Min(AlignUp(_allocation.Offset + byteOffset + size, atom), _allocation.Offset + _allocation.Size);
        var range = new MappedMemoryRange
        {
            SType = StructureType.MappedMemoryRange,
            Memory = _allocation.Memory,
            Offset = start,
            Size = end - start,
        };
        _device.Vk.FlushMappedMemoryRanges(_device.Device, 1, &range).Check("vkFlushMappedMemoryRanges");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.Vk.DestroyBuffer(_device.Device, Handle, null);
        _allocator.Free(_allocation);
        _allocation = default;
    }
}
