using Silk.NET.Vulkan;

namespace SakritCraft.Render.Vulkan;

/// <summary>
/// Format capability queries. Vulkan guarantees very little about which formats are usable for which
/// purpose, so anything that is not covered by the spec's mandatory-support tables must be asked for.
/// </summary>
public static class FormatSupport
{
    /// <summary>
    /// Picks the depth attachment format. Preference order matters: D32_SFLOAT is the whole point of
    /// reverse-Z (a float buffer's precision is highest near 0, which reverse-Z maps to the far
    /// distance, giving uniform relative precision over kilometres); the combined formats are fallbacks
    /// that waste a stencil plane we do not use. D24 is last because its fixed-point precision loses
    /// most of the reverse-Z benefit. Vulkan mandates that at least one of D32_SFLOAT or D24_UNORM_S8
    /// (and one of D32_SFLOAT_S8 or D24_UNORM_S8) is supported, so this cannot fail on a conformant driver.
    /// </summary>
    public static Format ChooseDepthFormat(VulkanDevice device)
    {
        ReadOnlySpan<Format> candidates = stackalloc Format[]
        {
            Format.D32Sfloat,
            Format.D32SfloatS8Uint,
            Format.D24UnormS8Uint,
        };

        foreach (var format in candidates)
        {
            if (Supports(device, format, ImageTiling.Optimal, FormatFeatureFlags.DepthStencilAttachmentBit))
            {
                return format;
            }
        }

        throw new PlatformNotSupportedException("No depth format with DEPTH_STENCIL_ATTACHMENT support in optimal tiling; driver is non-conformant.");
    }

    /// <summary>True when <paramref name="format"/> supports every flag in <paramref name="features"/> for the given tiling.</summary>
    /// <summary>
    /// Picks the shadow-map format. D16 is preferred over D32 here, the opposite of the main depth
    /// buffer: a cascade covers a bounded slice with a conventional 0-to-1 depth range, so sixteen
    /// bits are ample, and halving the bandwidth matters when four layers are written every frame.
    /// The format must also support sampled reads, which the attachment-only check does not imply.
    /// </summary>
    public static Format ChooseShadowFormat(VulkanDevice device)
    {
        ReadOnlySpan<Format> candidates = stackalloc Format[]
        {
            Format.D16Unorm,
            Format.D32Sfloat,
            Format.D24UnormS8Uint,
            Format.D32SfloatS8Uint,
        };

        foreach (var format in candidates)
        {
            if (Supports(device, format, ImageTiling.Optimal,
                    FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit))
            {
                return format;
            }
        }

        throw new InvalidOperationException("No sampleable depth format is available for shadow maps.");
    }

    public static bool Supports(VulkanDevice device, Format format, ImageTiling tiling, FormatFeatureFlags features)
    {
        device.Vk.GetPhysicalDeviceFormatProperties(device.PhysicalDevice, format, out FormatProperties props);
        var available = tiling == ImageTiling.Optimal ? props.OptimalTilingFeatures : props.LinearTilingFeatures;
        return (available & features) == features;
    }
}
