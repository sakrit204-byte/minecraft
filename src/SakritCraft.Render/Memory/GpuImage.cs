using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;

namespace SakritCraft.Render.Memory;

/// <summary>Creation parameters for a <see cref="GpuImage"/>. Defaults describe a single-mip 2D colour texture.</summary>
public sealed record GpuImageDesc
{
    public required string Name { get; init; }
    public required uint Width { get; init; }
    public required uint Height { get; init; }
    public uint Depth { get; init; } = 1;
    public required Format Format { get; init; }
    public required ImageUsageFlags Usage { get; init; }
    public uint MipLevels { get; init; } = 1;
    public uint ArrayLayers { get; init; } = 1;
    public SampleCountFlags Samples { get; init; } = SampleCountFlags.Count1Bit;
    public ImageType Type { get; init; } = ImageType.Type2D;
    public ImageViewType ViewType { get; init; } = ImageViewType.Type2D;
    public MemoryUsage MemoryUsage { get; init; } = MemoryUsage.GpuOnly;
}

/// <summary>
/// A VkImage with OPTIMAL tiling, its memory from the <see cref="GpuAllocator"/>, and one default view
/// covering every mip and layer. Optimal tiling is the only tiling this engine uses: linear images are
/// slower to sample and serve no purpose once uploads go through staging buffers. That decision is
/// what lets the allocator honour <c>bufferImageGranularity</c> by simply never mixing images and
/// buffers in a block (<see cref="MemoryBlock"/>): every image asks for a non-linear range.
///
/// Layout is deliberately not tracked here. A render target changes layout every frame inside a
/// command buffer the renderer already reasons about; a texture changes once at upload. Tracking it
/// on the object would give a false sense of safety across queues and in-flight frames.
/// </summary>
public sealed unsafe class GpuImage : IDisposable
{
    private readonly VulkanDevice _device;
    private readonly GpuAllocator _allocator;
    private GpuAllocation _allocation;
    private bool _disposed;

    public Image Handle { get; }
    /// <summary>View over every mip and layer, with the aspect implied by the format.</summary>
    public ImageView View { get; }
    public Format Format { get; }
    public Extent3D Extent { get; }
    public uint MipLevels { get; }
    public uint ArrayLayers { get; }
    public ImageUsageFlags Usage { get; }
    public ImageAspectFlags Aspect { get; }
    public string Name { get; }
    /// <summary>Bytes of device memory the image occupies (driver-reported requirement, not the pixel count).</summary>
    public ulong AllocatedBytes => _allocation.Size;

    public GpuImage(VulkanDevice device, GpuAllocator allocator, GpuImageDesc desc)
    {
        if (desc.Width == 0 || desc.Height == 0 || desc.Depth == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desc), "Image extent must be non-zero in every dimension.");
        }

        _device = device;
        _allocator = allocator;
        Format = desc.Format;
        Extent = new Extent3D(desc.Width, desc.Height, desc.Depth);
        MipLevels = desc.MipLevels;
        ArrayLayers = desc.ArrayLayers;
        Usage = desc.Usage;
        Aspect = AspectOf(desc.Format);
        Name = desc.Name;

        var vk = device.Vk;
        var createInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = desc.Type,
            Format = desc.Format,
            Extent = Extent,
            MipLevels = desc.MipLevels,
            ArrayLayers = desc.ArrayLayers,
            Samples = desc.Samples,
            Tiling = ImageTiling.Optimal,
            Usage = desc.Usage,
            SharingMode = SharingMode.Exclusive, // cross-queue use goes through explicit ownership transfer
            InitialLayout = ImageLayout.Undefined,
        };
        vk.CreateImage(device.Device, &createInfo, null, out Image image).Check("vkCreateImage");
        Handle = image;

        vk.GetImageMemoryRequirements(device.Device, image, out MemoryRequirements requirements);
        _allocation = allocator.Allocate(requirements, desc.MemoryUsage, linear: false, desc.Name);
        vk.BindImageMemory(device.Device, image, _allocation.Memory, _allocation.Offset).Check("vkBindImageMemory");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = desc.ViewType,
            Format = desc.Format,
            Components = new ComponentMapping(ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity),
            SubresourceRange = new ImageSubresourceRange(Aspect, 0, desc.MipLevels, 0, desc.ArrayLayers),
        };
        vk.CreateImageView(device.Device, &viewInfo, null, out ImageView view).Check("vkCreateImageView");
        View = view;

        device.SetObjectName(ObjectType.Image, image.Handle, desc.Name);
        device.SetObjectName(ObjectType.ImageView, view.Handle, desc.Name + ".View");
    }

    /// <summary>Subresource range covering the whole image, for barriers.</summary>
    public ImageSubresourceRange FullRange => new(Aspect, 0, MipLevels, 0, ArrayLayers);

    /// <summary>Aspect flags a format implies. Combined depth-stencil formats must be addressed with both bits in barriers.</summary>
    public static ImageAspectFlags AspectOf(Format format) => format switch
    {
        Format.D16Unorm or Format.D32Sfloat or Format.X8D24UnormPack32 => ImageAspectFlags.DepthBit,
        Format.S8Uint => ImageAspectFlags.StencilBit,
        Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
        _ => ImageAspectFlags.ColorBit,
    };

    public static bool HasStencil(Format format) => (AspectOf(format) & ImageAspectFlags.StencilBit) != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var vk = _device.Vk;
        vk.DestroyImageView(_device.Device, View, null);
        vk.DestroyImage(_device.Device, Handle, null);
        _allocator.Free(_allocation);
        _allocation = default;
    }
}
