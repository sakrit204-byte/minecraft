using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SakritCraft.Render.Vulkan;

/// <summary>
/// Snapshot of what one physical device supports, gathered once so that device creation, the
/// allocator and the capability report all read from the same data. Feature structs hold what the
/// device <i>advertises</i>; <see cref="VulkanDevice"/> decides what to enable.
/// </summary>
internal sealed unsafe class PhysicalDeviceInfo
{
    public required PhysicalDevice Handle { get; init; }
    public required int Index { get; init; }
    public required PhysicalDeviceProperties Properties { get; init; }
    public required PhysicalDeviceVulkan12Properties Properties12 { get; init; }
    public required PhysicalDeviceMemoryProperties Memory { get; init; }
    public required HashSet<string> Extensions { get; init; }

    public required PhysicalDeviceFeatures Features10 { get; init; }
    public required PhysicalDeviceVulkan11Features Features11 { get; init; }
    public required PhysicalDeviceVulkan12Features Features12 { get; init; }
    public required PhysicalDeviceVulkan13Features Features13 { get; init; }
    public required PhysicalDeviceMeshShaderFeaturesEXT MeshShader { get; init; }
    public required PhysicalDeviceRayQueryFeaturesKHR RayQuery { get; init; }
    public required PhysicalDeviceAccelerationStructureFeaturesKHR AccelerationStructure { get; init; }
    public required PhysicalDeviceDescriptorBufferFeaturesEXT DescriptorBuffer { get; init; }
    public required PhysicalDeviceDynamicRenderingLocalReadFeaturesKHR LocalRead { get; init; }

    public required uint GraphicsFamily { get; init; }
    /// <summary>Dedicated transfer family (TRANSFER without GRAPHICS/COMPUTE), or null to share the graphics queue.</summary>
    public required uint? TransferFamily { get; init; }
    /// <summary>Async compute family (COMPUTE without GRAPHICS), or null.</summary>
    public required uint? ComputeFamily { get; init; }
    public required bool GraphicsTimestamps { get; init; }
    public required long Score { get; init; }
    public required string RejectReason { get; init; }

    public required string Name { get; init; }

    public ulong DeviceLocalBytes
    {
        get
        {
            ulong total = 0;
            for (int i = 0; i < Memory.MemoryHeapCount; i++)
            {
                var heap = Memory.MemoryHeaps[i];
                if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                {
                    total += heap.Size;
                }
            }

            return total;
        }
    }

    public bool Has(string extension) => Extensions.Contains(extension);

}

/// <summary>
/// Enumerates physical devices, scores them and picks the best. Discrete GPUs win, then VRAM, then
/// optional feature support. A device missing any <i>required</i> 1.3 feature is rejected outright
/// with a logged reason so a failure on unknown hardware is diagnosable from the log alone.
/// </summary>
internal static unsafe class PhysicalDeviceSelector
{
    private const string Tag = "vk.select";

    internal const string RayQueryExtension = "VK_KHR_ray_query";
    internal const string MemoryBudgetExtension = "VK_EXT_memory_budget";

    public static PhysicalDeviceInfo Select(VulkanInstance instance, SurfaceKHR surface, RendererOptions options)
    {
        var vk = instance.Vk;
        uint count = 0;
        vk.EnumeratePhysicalDevices(instance.Instance, &count, null).Check("vkEnumeratePhysicalDevices");
        if (count == 0)
        {
            throw new PlatformNotSupportedException("No Vulkan physical devices found.");
        }

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
        {
            vk.EnumeratePhysicalDevices(instance.Instance, &count, p).Check("vkEnumeratePhysicalDevices");
        }

        PhysicalDeviceInfo? best = null;
        for (int i = 0; i < count; i++)
        {
            var info = Inspect(instance, devices[i], i, surface);
            string verdict = info.RejectReason.Length == 0 ? $"score {info.Score}" : $"rejected: {info.RejectReason}";
            RenderLog.Info(Tag, $"[{i}] {info.Name} ({info.Properties.DeviceType}, {info.DeviceLocalBytes / (1024 * 1024)} MiB local) -> {verdict}");

            if (options.ForcePhysicalDeviceIndex is int forced)
            {
                if (forced == i)
                {
                    if (info.RejectReason.Length > 0)
                    {
                        throw new PlatformNotSupportedException($"Forced device {i} ({info.Name}) is unusable: {info.RejectReason}");
                    }

                    best = info;
                }

                continue;
            }

            if (info.RejectReason.Length == 0 && (best is null || info.Score > best.Score))
            {
                best = info;
            }
        }

        if (best is null)
        {
            throw new PlatformNotSupportedException("No physical device satisfies SakritCraft's Vulkan 1.3 requirements. See log for per-device reasons.");
        }

        RenderLog.Info(Tag, $"Selected [{best.Index}] {best.Name}");
        return best;
    }

    private static PhysicalDeviceInfo Inspect(VulkanInstance instance, PhysicalDevice device, int index, SurfaceKHR surface)
    {
        var vk = instance.Vk;

        // --- Properties (+ 1.2 properties for driver name/info and update-after-bind limits)
        var props12 = new PhysicalDeviceVulkan12Properties { SType = StructureType.PhysicalDeviceVulkan12Properties };
        var props2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &props12 };
        vk.GetPhysicalDeviceProperties2(device, &props2);
        var props = props2.Properties;
        string name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "<unnamed>";

        // --- Extensions
        var extensions = new HashSet<string>(StringComparer.Ordinal);
        uint extCount = 0;
        vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &extCount, null).Check("vkEnumerateDeviceExtensionProperties");
        var extProps = new ExtensionProperties[extCount];
        fixed (ExtensionProperties* p = extProps)
        {
            vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &extCount, p).Check("vkEnumerateDeviceExtensionProperties");
            for (uint i = 0; i < extCount; i++)
            {
                extensions.Add(SilkMarshal.PtrToString((nint)p[i].ExtensionName)!);
            }
        }

        // --- Features. Only chain extension structs the device actually advertises; chaining an
        //     unknown one is a validation error and some drivers scribble on it.
        var f11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features };
        var f12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features };
        var f13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var mesh = new PhysicalDeviceMeshShaderFeaturesEXT { SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt };
        var rayQuery = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr };
        var accel = new PhysicalDeviceAccelerationStructureFeaturesKHR { SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr };
        var descBuf = new PhysicalDeviceDescriptorBufferFeaturesEXT { SType = StructureType.PhysicalDeviceDescriptorBufferFeaturesExt };
        var localRead = new PhysicalDeviceDynamicRenderingLocalReadFeaturesKHR { SType = StructureType.PhysicalDeviceDynamicRenderingLocalReadFeaturesKhr };

        var f2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2 };
        void** tail = &f2.PNext;
        Chain(ref tail, &f11, &f11.PNext);
        Chain(ref tail, &f12, &f12.PNext);
        Chain(ref tail, &f13, &f13.PNext);
        if (extensions.Contains(ExtMeshShader.ExtensionName)) Chain(ref tail, &mesh, &mesh.PNext);
        if (extensions.Contains(RayQueryExtension)) Chain(ref tail, &rayQuery, &rayQuery.PNext);
        if (extensions.Contains(KhrAccelerationStructure.ExtensionName)) Chain(ref tail, &accel, &accel.PNext);
        if (extensions.Contains(ExtDescriptorBuffer.ExtensionName)) Chain(ref tail, &descBuf, &descBuf.PNext);
        if (extensions.Contains(KhrDynamicRenderingLocalRead.ExtensionName)) Chain(ref tail, &localRead, &localRead.PNext);

        vk.GetPhysicalDeviceFeatures2(device, &f2);

        // --- Memory
        vk.GetPhysicalDeviceMemoryProperties(device, out PhysicalDeviceMemoryProperties memory);

        // --- Queues
        uint familyCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, null);
        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* p = families)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, p);
        }

        uint graphics = uint.MaxValue;
        uint? transfer = null;
        uint? compute = null;
        bool timestamps = false;
        for (uint i = 0; i < familyCount; i++)
        {
            var flags = families[i].QueueFlags;
            bool hasGraphics = (flags & QueueFlags.GraphicsBit) != 0;
            bool hasCompute = (flags & QueueFlags.ComputeBit) != 0;
            bool hasTransfer = (flags & QueueFlags.TransferBit) != 0;

            if (hasGraphics && graphics == uint.MaxValue)
            {
                instance.SurfaceExt.GetPhysicalDeviceSurfaceSupport(device, i, surface, out Bool32 present).Check("vkGetPhysicalDeviceSurfaceSupportKHR");
                if (present)
                {
                    graphics = i;
                    timestamps = families[i].TimestampValidBits != 0;
                }
            }

            if (hasTransfer && !hasGraphics && !hasCompute && transfer is null)
            {
                transfer = i;
            }

            if (hasCompute && !hasGraphics && compute is null)
            {
                compute = i;
            }
        }

        // --- Verdict
        string reject = "";
        long score = 0;
        if (props.ApiVersion < Vk.Version13)
        {
            var v = (Version32)props.ApiVersion;
            reject = $"Vulkan {v.Major}.{v.Minor} < 1.3";
        }
        else if (graphics == uint.MaxValue)
        {
            reject = "no graphics queue family that can present to the surface";
        }
        else if (!extensions.Contains(KhrSwapchain.ExtensionName))
        {
            reject = "VK_KHR_swapchain missing";
        }
        else
        {
            reject = MissingRequiredFeature(f12, f13);
        }

        if (reject.Length == 0)
        {
            score += props.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 1_000_000_000L,
                PhysicalDeviceType.IntegratedGpu => 100_000_000L,
                PhysicalDeviceType.VirtualGpu => 10_000_000L,
                _ => 0L,
            };

            ulong localBytes = 0;
            for (int i = 0; i < memory.MemoryHeapCount; i++)
            {
                if ((memory.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                {
                    localBytes += memory.MemoryHeaps[i].Size;
                }
            }

            score += (long)(localBytes / (1024UL * 1024UL)); // MiB
            if (rayQuery.RayQuery && accel.AccelerationStructure) score += 1_000_000;
            if (mesh.MeshShader) score += 1_000_000;
            if (descBuf.DescriptorBuffer) score += 250_000;
            if (localRead.DynamicRenderingLocalRead) score += 100_000;
        }

        return new PhysicalDeviceInfo
        {
            Handle = device,
            Index = index,
            Name = name,
            Properties = props,
            Properties12 = props12,
            Memory = memory,
            Extensions = extensions,
            Features10 = f2.Features,
            Features11 = f11,
            Features12 = f12,
            Features13 = f13,
            MeshShader = mesh,
            RayQuery = rayQuery,
            AccelerationStructure = accel,
            DescriptorBuffer = descBuf,
            LocalRead = localRead,
            GraphicsFamily = graphics,
            TransferFamily = transfer,
            ComputeFamily = compute,
            GraphicsTimestamps = timestamps,
            Score = score,
            RejectReason = reject,
        };
    }

    /// <summary>Returns an empty string when every feature the renderer core depends on is available.</summary>
    private static string MissingRequiredFeature(in PhysicalDeviceVulkan12Features f12, in PhysicalDeviceVulkan13Features f13)
    {
        if (!f13.DynamicRendering) return "dynamicRendering";
        if (!f13.Synchronization2) return "synchronization2";
        if (!f13.Maintenance4) return "maintenance4";
        if (!f12.TimelineSemaphore) return "timelineSemaphore";
        if (!f12.BufferDeviceAddress) return "bufferDeviceAddress";
        if (!f12.DescriptorIndexing) return "descriptorIndexing";
        if (!f12.RuntimeDescriptorArray) return "runtimeDescriptorArray";
        if (!f12.DescriptorBindingPartiallyBound) return "descriptorBindingPartiallyBound";
        if (!f12.DescriptorBindingSampledImageUpdateAfterBind) return "descriptorBindingSampledImageUpdateAfterBind";
        if (!f12.DescriptorBindingStorageBufferUpdateAfterBind) return "descriptorBindingStorageBufferUpdateAfterBind";
        if (!f12.DescriptorBindingStorageImageUpdateAfterBind) return "descriptorBindingStorageImageUpdateAfterBind";
        if (!f12.DescriptorBindingUpdateUnusedWhilePending) return "descriptorBindingUpdateUnusedWhilePending";
        if (!f12.ShaderSampledImageArrayNonUniformIndexing) return "shaderSampledImageArrayNonUniformIndexing";
        if (!f12.ShaderStorageBufferArrayNonUniformIndexing) return "shaderStorageBufferArrayNonUniformIndexing";
        if (!f12.ScalarBlockLayout) return "scalarBlockLayout";
        if (!f12.HostQueryReset) return "hostQueryReset";
        return "";
    }

    private static void Chain(ref void** tail, void* node, void** nodeNext)
    {
        *tail = node;
        tail = nodeNext;
    }
}
