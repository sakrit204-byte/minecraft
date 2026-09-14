using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace SakritCraft.Render.Vulkan;

/// <summary>
/// Presentable image chain plus the per-image "render finished" semaphores. Format selection prefers
/// B8G8R8A8_SRGB so the hardware performs the linear-to-sRGB encode on write; present mode prefers
/// mailbox for latency and falls back to FIFO, which is the only mode Vulkan guarantees.
///
/// The render-finished semaphore is per swapchain <i>image</i>, not per frame in flight: a binary
/// semaphore handed to vkQueuePresentKHR may only be reused once that present has consumed it, and the
/// only reliable signal for that is acquiring the same image again. Per-frame semaphores are a
/// well-known validation error and an intermittent hang on some drivers.
/// </summary>
public sealed unsafe class Swapchain : IDisposable
{
    private const string Tag = "vk.swapchain";

    private readonly VulkanDevice _device;
    private readonly SurfaceKHR _surface;
    private readonly PresentPreference _preference;

    private SwapchainKHR _swapchain;
    private Image[] _images = Array.Empty<Image>();
    private ImageView[] _views = Array.Empty<ImageView>();
    private Semaphore[] _renderFinished = Array.Empty<Semaphore>();
    private bool _disposed;

    public Format ImageFormat { get; private set; }
    public ColorSpaceKHR ColorSpace { get; private set; }
    public PresentModeKHR PresentMode { get; private set; }
    public Extent2D Extent { get; private set; }
    public int ImageCount => _images.Length;
    /// <summary>False while the window is minimised (zero-area surface); nothing can be rendered.</summary>
    public bool IsValid => _swapchain.Handle != 0 && Extent.Width > 0 && Extent.Height > 0;
    public SwapchainKHR Handle => _swapchain;

    public Swapchain(VulkanDevice device, SurfaceKHR surface, PresentPreference preference, uint width, uint height)
    {
        _device = device;
        _surface = surface;
        _preference = preference;
        Recreate(width, height);
    }

    public Image GetImage(uint index) => _images[index];
    public ImageView GetView(uint index) => _views[index];
    public Semaphore GetRenderFinished(uint index) => _renderFinished[index];

    /// <summary>
    /// (Re)builds the swapchain for the given framebuffer size. The caller must guarantee no GPU work
    /// still references the old images (the renderer waits for the device to go idle first; resize is
    /// not a steady-state path). A zero-area size tears down the views and leaves <see cref="IsValid"/> false.
    /// </summary>
    public void Recreate(uint width, uint height)
    {
        var vk = _device.Vk;
        var ext = _device.SwapchainExt;

        DestroyImageViewsAndSemaphores();

        var instanceSurface = _device.SurfaceExt;
        instanceSurface.GetPhysicalDeviceSurfaceCapabilities(_device.PhysicalDevice, _surface, out SurfaceCapabilitiesKHR caps)
            .Check("vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        // Extent: the surface dictates it on Windows; 0xFFFFFFFF means "you choose" (clamped).
        Extent2D extent;
        if (caps.CurrentExtent.Width != uint.MaxValue)
        {
            extent = caps.CurrentExtent;
        }
        else
        {
            extent = new Extent2D(
                Math.Clamp(width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Math.Clamp(height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        }

        if (extent.Width == 0 || extent.Height == 0)
        {
            // Minimised. Keep the old swapchain handle alive (it is still valid) but report invalid so
            // the renderer skips frames until the window comes back.
            Extent = extent;
            RenderLog.Trace(Tag, "Zero-area surface; swapchain idle.");
            return;
        }

        var (format, colorSpace) = ChooseFormat(instanceSurface);
        var presentMode = ChoosePresentMode(instanceSurface);

        // Mailbox needs three images to actually decouple; FIFO is happy with min+1. Respect the max.
        uint imageCount = Math.Max(caps.MinImageCount + 1, presentMode == PresentModeKHR.MailboxKhr ? 3u : 2u);
        if (caps.MaxImageCount > 0)
        {
            imageCount = Math.Min(imageCount, caps.MaxImageCount);
        }

        var composite = (caps.SupportedCompositeAlpha & CompositeAlphaFlagsKHR.OpaqueBitKhr) != 0
            ? CompositeAlphaFlagsKHR.OpaqueBitKhr
            : CompositeAlphaFlagsKHR.InheritBitKhr;

        var usage = ImageUsageFlags.ColorAttachmentBit;
        if ((caps.SupportedUsageFlags & ImageUsageFlags.TransferDstBit) != 0)
        {
            usage |= ImageUsageFlags.TransferDstBit; // lets a post pass blit straight into the backbuffer
        }

        var oldSwapchain = _swapchain;
        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = format,
            ImageColorSpace = colorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = usage,
            ImageSharingMode = SharingMode.Exclusive, // graphics queue presents; no cross-family sharing
            PreTransform = (caps.SupportedTransforms & SurfaceTransformFlagsKHR.IdentityBitKhr) != 0
                ? SurfaceTransformFlagsKHR.IdentityBitKhr
                : caps.CurrentTransform,
            CompositeAlpha = composite,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = oldSwapchain,
        };

        ext.CreateSwapchain(_device.Device, &createInfo, null, out SwapchainKHR swapchain).Check("vkCreateSwapchainKHR");
        _swapchain = swapchain;
        if (oldSwapchain.Handle != 0)
        {
            ext.DestroySwapchain(_device.Device, oldSwapchain, null);
        }

        ImageFormat = format;
        ColorSpace = colorSpace;
        PresentMode = presentMode;
        Extent = extent;

        uint count = 0;
        ext.GetSwapchainImages(_device.Device, _swapchain, &count, null).Check("vkGetSwapchainImagesKHR");
        _images = new Image[count];
        fixed (Image* p = _images)
        {
            ext.GetSwapchainImages(_device.Device, _swapchain, &count, p).Check("vkGetSwapchainImagesKHR");
        }

        _views = new ImageView[count];
        _renderFinished = new Semaphore[count];
        for (uint i = 0; i < count; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[i],
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = new ComponentMapping(ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity),
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            vk.CreateImageView(_device.Device, &viewInfo, null, out _views[i]).Check("vkCreateImageView");

            var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            vk.CreateSemaphore(_device.Device, &semInfo, null, out _renderFinished[i]).Check("vkCreateSemaphore");

            _device.SetObjectName(ObjectType.Image, _images[i].Handle, $"Swapchain.Image[{i}]");
            _device.SetObjectName(ObjectType.ImageView, _views[i].Handle, $"Swapchain.View[{i}]");
            _device.SetObjectName(ObjectType.Semaphore, _renderFinished[i].Handle, $"Swapchain.RenderFinished[{i}]");
        }

        _device.SetObjectName(ObjectType.SwapchainKhr, _swapchain.Handle, "Swapchain");
        RenderLog.Info(Tag, $"{extent.Width}x{extent.Height}, {count} images, {format}/{colorSpace}, {presentMode}");
    }

    /// <summary>
    /// Acquires the next image. Returns <see cref="Result.ErrorOutOfDateKhr"/> or <see cref="Result.SuboptimalKhr"/>
    /// for the caller to handle; throws on any other error.
    /// </summary>
    public Result Acquire(Semaphore signal, out uint imageIndex)
    {
        uint index = 0;
        var result = _device.SwapchainExt.AcquireNextImage(_device.Device, _swapchain, ulong.MaxValue, signal, default, &index);
        imageIndex = index;
        if (result == Result.ErrorOutOfDateKhr)
        {
            return result;
        }

        return result.Check("vkAcquireNextImageKHR");
    }

    /// <summary>Presents <paramref name="imageIndex"/> after its render-finished semaphore. Out-of-date is returned, not thrown.</summary>
    public Result Present(Queue queue, uint imageIndex)
    {
        var wait = _renderFinished[imageIndex];
        var swapchain = _swapchain;
        var info = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &wait,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };
        var result = _device.SwapchainExt.QueuePresent(queue, &info);
        if (result == Result.ErrorOutOfDateKhr)
        {
            return result;
        }

        return result.Check("vkQueuePresentKHR");
    }

    private (Format, ColorSpaceKHR) ChooseFormat(Silk.NET.Vulkan.Extensions.KHR.KhrSurface surfaceExt)
    {
        uint count = 0;
        surfaceExt.GetPhysicalDeviceSurfaceFormats(_device.PhysicalDevice, _surface, &count, null).Check("vkGetPhysicalDeviceSurfaceFormatsKHR");
        var formats = stackalloc SurfaceFormatKHR[(int)count];
        surfaceExt.GetPhysicalDeviceSurfaceFormats(_device.PhysicalDevice, _surface, &count, formats).Check("vkGetPhysicalDeviceSurfaceFormatsKHR");

        // Preference order. sRGB formats let the ROP do the encode for free; the linear-space renderer
        // never has to think about gamma at the backbuffer.
        ReadOnlySpan<Format> preferred = stackalloc Format[] { Format.B8G8R8A8Srgb, Format.R8G8B8A8Srgb, Format.B8G8R8A8Unorm, Format.R8G8B8A8Unorm };
        foreach (var want in preferred)
        {
            for (uint i = 0; i < count; i++)
            {
                if (formats[i].Format == want && formats[i].ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return (want, formats[i].ColorSpace);
                }
            }
        }

        RenderLog.Warn(Tag, $"No preferred surface format; falling back to {formats[0].Format}/{formats[0].ColorSpace}");
        return (formats[0].Format, formats[0].ColorSpace);
    }

    private PresentModeKHR ChoosePresentMode(Silk.NET.Vulkan.Extensions.KHR.KhrSurface surfaceExt)
    {
        uint count = 0;
        surfaceExt.GetPhysicalDeviceSurfacePresentModes(_device.PhysicalDevice, _surface, &count, null).Check("vkGetPhysicalDeviceSurfacePresentModesKHR");
        var modes = stackalloc PresentModeKHR[(int)count];
        surfaceExt.GetPhysicalDeviceSurfacePresentModes(_device.PhysicalDevice, _surface, &count, modes).Check("vkGetPhysicalDeviceSurfacePresentModesKHR");

        var want = _preference switch
        {
            PresentPreference.Mailbox => PresentModeKHR.MailboxKhr,
            PresentPreference.Immediate => PresentModeKHR.ImmediateKhr,
            _ => PresentModeKHR.FifoKhr,
        };

        for (uint i = 0; i < count; i++)
        {
            if (modes[i] == want)
            {
                return want;
            }
        }

        if (want != PresentModeKHR.FifoKhr)
        {
            RenderLog.Warn(Tag, $"{want} not supported; using FIFO.");
        }

        return PresentModeKHR.FifoKhr;
    }

    private void DestroyImageViewsAndSemaphores()
    {
        var vk = _device.Vk;
        for (int i = 0; i < _views.Length; i++)
        {
            if (_views[i].Handle != 0) vk.DestroyImageView(_device.Device, _views[i], null);
            if (_renderFinished[i].Handle != 0) vk.DestroySemaphore(_device.Device, _renderFinished[i], null);
        }

        _views = Array.Empty<ImageView>();
        _renderFinished = Array.Empty<Semaphore>();
        _images = Array.Empty<Image>();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyImageViewsAndSemaphores();
        if (_swapchain.Handle != 0)
        {
            _device.SwapchainExt.DestroySwapchain(_device.Device, _swapchain, null);
            _swapchain = default;
        }
    }
}
