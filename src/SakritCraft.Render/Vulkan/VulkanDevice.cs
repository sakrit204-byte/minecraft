using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SakritCraft.Render.Vulkan;

/// <summary>
/// The logical device: queues, enabled features, swapchain extension, and the debug-naming helper
/// every other object uses. Feature enabling is explicit and mirrored into <see cref="Capabilities"/>
/// so a flag there is a promise that the feature is usable, not a hope.
///
/// Queue policy: one graphics queue that also presents; a dedicated transfer queue when the device
/// has a transfer-only family (chunk uploads must not stall graphics, docs section 06 "Streaming");
/// an async compute queue when a compute-only family exists. Missing families alias the graphics queue
/// so callers never null-check.
/// </summary>
public sealed unsafe class VulkanDevice : IDisposable
{
    private const string Tag = "vk.device";

    private readonly VulkanInstance _instance;
    private bool _disposed;

    public Vk Vk { get; }
    public Instance Instance => _instance.Instance;
    public PhysicalDevice PhysicalDevice { get; }
    public Device Device { get; }
    public KhrSwapchain SwapchainExt { get; }
    public KhrSurface SurfaceExt => _instance.SurfaceExt;
    public GpuCapabilities Capabilities { get; }

    public Queue GraphicsQueue { get; }
    public uint GraphicsFamily { get; }
    public Queue TransferQueue { get; }
    public uint TransferFamily { get; }
    public Queue ComputeQueue { get; }
    public uint ComputeFamily { get; }
    /// <summary>True when transfer work runs on its own hardware queue rather than aliasing graphics.</summary>
    public bool HasDedicatedTransferQueue { get; }
    public bool HasAsyncComputeQueue { get; }

    public PhysicalDeviceProperties Properties { get; }
    public PhysicalDeviceMemoryProperties MemoryProperties { get; }
    /// <summary>Extensions actually enabled on this device.</summary>
    public IReadOnlySet<string> EnabledExtensions { get; }

    internal VulkanDevice(VulkanInstance instance, PhysicalDeviceInfo info)
    {
        _instance = instance;
        Vk = instance.Vk;
        PhysicalDevice = info.Handle;
        Properties = info.Properties;
        MemoryProperties = info.Memory;

        // ---- Extensions: required + every optional one the device has.
        var extensions = new List<string> { KhrSwapchain.ExtensionName };
        bool meshShader = info.Has(ExtMeshShader.ExtensionName) && info.MeshShader.MeshShader;
        bool accel = info.Has(KhrAccelerationStructure.ExtensionName)
                     && info.Has(KhrDeferredHostOperations.ExtensionName)
                     && info.AccelerationStructure.AccelerationStructure;
        bool rayQuery = accel && info.Has(PhysicalDeviceSelector.RayQueryExtension) && info.RayQuery.RayQuery;
        bool descriptorBuffer = info.Has(ExtDescriptorBuffer.ExtensionName) && info.DescriptorBuffer.DescriptorBuffer;
        bool localRead = info.Has(KhrDynamicRenderingLocalRead.ExtensionName) && info.LocalRead.DynamicRenderingLocalRead;
        bool memoryBudget = info.Has(PhysicalDeviceSelector.MemoryBudgetExtension);

        if (meshShader) extensions.Add(ExtMeshShader.ExtensionName);
        if (accel)
        {
            extensions.Add(KhrAccelerationStructure.ExtensionName);
            extensions.Add(KhrDeferredHostOperations.ExtensionName);
        }
        if (rayQuery) extensions.Add(PhysicalDeviceSelector.RayQueryExtension);
        if (descriptorBuffer) extensions.Add(ExtDescriptorBuffer.ExtensionName);
        if (localRead) extensions.Add(KhrDynamicRenderingLocalRead.ExtensionName);
        if (memoryBudget) extensions.Add(PhysicalDeviceSelector.MemoryBudgetExtension);

        // ---- Features. Required ones were verified by the selector; optional ones are gated here.
        var f10 = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = info.Features10.SamplerAnisotropy,
            FillModeNonSolid = info.Features10.FillModeNonSolid,
            MultiDrawIndirect = info.Features10.MultiDrawIndirect,
            DrawIndirectFirstInstance = info.Features10.DrawIndirectFirstInstance,
            ShaderInt64 = info.Features10.ShaderInt64,
            ShaderInt16 = info.Features10.ShaderInt16,
            FragmentStoresAndAtomics = info.Features10.FragmentStoresAndAtomics,
            VertexPipelineStoresAndAtomics = info.Features10.VertexPipelineStoresAndAtomics,
            IndependentBlend = info.Features10.IndependentBlend,
            DepthClamp = info.Features10.DepthClamp,
            SampleRateShading = info.Features10.SampleRateShading,
            TextureCompressionBC = info.Features10.TextureCompressionBC,
            ShaderStorageImageWriteWithoutFormat = info.Features10.ShaderStorageImageWriteWithoutFormat,
            ShaderStorageImageReadWithoutFormat = info.Features10.ShaderStorageImageReadWithoutFormat,
        };

        var f11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            ShaderDrawParameters = info.Features11.ShaderDrawParameters,
            StorageBuffer16BitAccess = info.Features11.StorageBuffer16BitAccess,
        };

        var f12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            // Required
            TimelineSemaphore = true,
            BufferDeviceAddress = true,
            DescriptorIndexing = true,
            RuntimeDescriptorArray = true,
            DescriptorBindingPartiallyBound = true,
            DescriptorBindingSampledImageUpdateAfterBind = true,
            DescriptorBindingStorageBufferUpdateAfterBind = true,
            DescriptorBindingStorageImageUpdateAfterBind = true,
            DescriptorBindingUpdateUnusedWhilePending = true,
            ShaderSampledImageArrayNonUniformIndexing = true,
            ShaderStorageBufferArrayNonUniformIndexing = true,
            ScalarBlockLayout = true,
            HostQueryReset = true,
            // Optional
            DescriptorBindingUniformBufferUpdateAfterBind = info.Features12.DescriptorBindingUniformBufferUpdateAfterBind,
            DescriptorBindingVariableDescriptorCount = info.Features12.DescriptorBindingVariableDescriptorCount,
            ShaderStorageImageArrayNonUniformIndexing = info.Features12.ShaderStorageImageArrayNonUniformIndexing,
            ShaderUniformBufferArrayNonUniformIndexing = info.Features12.ShaderUniformBufferArrayNonUniformIndexing,
            DrawIndirectCount = info.Features12.DrawIndirectCount,
            SamplerFilterMinmax = info.Features12.SamplerFilterMinmax,
            ShaderFloat16 = info.Features12.ShaderFloat16,
            ShaderInt8 = info.Features12.ShaderInt8,
            StorageBuffer8BitAccess = info.Features12.StorageBuffer8BitAccess,
            UniformAndStorageBuffer8BitAccess = info.Features12.UniformAndStorageBuffer8BitAccess,
            ShaderSubgroupExtendedTypes = info.Features12.ShaderSubgroupExtendedTypes,
            SeparateDepthStencilLayouts = info.Features12.SeparateDepthStencilLayouts,
            VulkanMemoryModel = info.Features12.VulkanMemoryModel,
            VulkanMemoryModelDeviceScope = info.Features12.VulkanMemoryModelDeviceScope,
            ShaderBufferInt64Atomics = info.Features12.ShaderBufferInt64Atomics,
            SamplerMirrorClampToEdge = info.Features12.SamplerMirrorClampToEdge,
        };

        var f13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,
            Maintenance4 = true,
            ShaderDemoteToHelperInvocation = info.Features13.ShaderDemoteToHelperInvocation,
            SubgroupSizeControl = info.Features13.SubgroupSizeControl,
            ComputeFullSubgroups = info.Features13.ComputeFullSubgroups,
            ShaderIntegerDotProduct = info.Features13.ShaderIntegerDotProduct,
            ShaderZeroInitializeWorkgroupMemory = info.Features13.ShaderZeroInitializeWorkgroupMemory,
        };

        var meshFeatures = new PhysicalDeviceMeshShaderFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt,
            MeshShader = true,
            TaskShader = info.MeshShader.TaskShader,
            MeshShaderQueries = info.MeshShader.MeshShaderQueries,
        };
        var accelFeatures = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            AccelerationStructure = true,
            DescriptorBindingAccelerationStructureUpdateAfterBind = info.AccelerationStructure.DescriptorBindingAccelerationStructureUpdateAfterBind,
        };
        var rayQueryFeatures = new PhysicalDeviceRayQueryFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
            RayQuery = true,
        };
        var descBufFeatures = new PhysicalDeviceDescriptorBufferFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceDescriptorBufferFeaturesExt,
            DescriptorBuffer = true,
        };
        var localReadFeatures = new PhysicalDeviceDynamicRenderingLocalReadFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingLocalReadFeaturesKhr,
            DynamicRenderingLocalRead = true,
        };

        var f2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, Features = f10 };
        void** tail = &f2.PNext;
        Chain(ref tail, &f11, &f11.PNext);
        Chain(ref tail, &f12, &f12.PNext);
        Chain(ref tail, &f13, &f13.PNext);
        if (meshShader) Chain(ref tail, &meshFeatures, &meshFeatures.PNext);
        if (accel) Chain(ref tail, &accelFeatures, &accelFeatures.PNext);
        if (rayQuery) Chain(ref tail, &rayQueryFeatures, &rayQueryFeatures.PNext);
        if (descriptorBuffer) Chain(ref tail, &descBufFeatures, &descBufFeatures.PNext);
        if (localRead) Chain(ref tail, &localReadFeatures, &localReadFeatures.PNext);

        // ---- Queues
        GraphicsFamily = info.GraphicsFamily;
        HasDedicatedTransferQueue = info.TransferFamily.HasValue;
        HasAsyncComputeQueue = info.ComputeFamily.HasValue;
        TransferFamily = info.TransferFamily ?? GraphicsFamily;
        ComputeFamily = info.ComputeFamily ?? GraphicsFamily;

        float priority = 1.0f;
        var queueInfos = stackalloc DeviceQueueCreateInfo[3];
        uint queueInfoCount = 0;
        queueInfos[queueInfoCount++] = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = GraphicsFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        if (HasDedicatedTransferQueue)
        {
            queueInfos[queueInfoCount++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = TransferFamily,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }
        if (HasAsyncComputeQueue && ComputeFamily != TransferFamily)
        {
            queueInfos[queueInfoCount++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = ComputeFamily,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }

        byte** extNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);
        try
        {
            var createInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &f2,
                QueueCreateInfoCount = queueInfoCount,
                PQueueCreateInfos = queueInfos,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = extNames,
                PEnabledFeatures = null, // features come through the pNext chain
            };

            Vk.CreateDevice(PhysicalDevice, &createInfo, null, out Device device).Check("vkCreateDevice");
            Device = device;
        }
        finally
        {
            SilkMarshal.Free((nint)extNames);
        }

        EnabledExtensions = new HashSet<string>(extensions, StringComparer.Ordinal);
        RenderLog.Info(Tag, $"Device extensions: {string.Join(", ", extensions)}");

        GraphicsQueue = Vk.GetDeviceQueue(Device, GraphicsFamily, 0);
        TransferQueue = HasDedicatedTransferQueue ? Vk.GetDeviceQueue(Device, TransferFamily, 0) : GraphicsQueue;
        ComputeQueue = HasAsyncComputeQueue
            ? (ComputeFamily == TransferFamily ? TransferQueue : Vk.GetDeviceQueue(Device, ComputeFamily, 0))
            : GraphicsQueue;

        if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain swapchainExt))
        {
            throw new PlatformNotSupportedException("VK_KHR_swapchain function table could not be loaded.");
        }

        SwapchainExt = swapchainExt;

        // ---- Capability report
        var apiVersion = (Version32)info.Properties.ApiVersion;
        var props12 = info.Properties12;
        Capabilities = new GpuCapabilities
        {
            DeviceName = info.Name,
            DeviceType = info.Properties.DeviceType,
            VendorId = info.Properties.VendorID,
            DeviceId = info.Properties.DeviceID,
            ApiVersion = $"{apiVersion.Major}.{apiVersion.Minor}.{apiVersion.Patch}",
            DriverName = SilkMarshal.PtrToString((nint)props12.DriverName) ?? "?",
            DriverInfo = SilkMarshal.PtrToString((nint)props12.DriverInfo) ?? "?",
            DeviceLocalMemoryBytes = info.DeviceLocalBytes,
            RayQuery = rayQuery,
            AccelerationStructure = accel,
            MeshShader = meshShader,
            DescriptorBuffer = descriptorBuffer,
            DynamicRenderingLocalRead = localRead,
            MemoryBudget = memoryBudget,
            DebugUtils = instance.DebugUtils is not null,
            ValidationLayer = instance.ValidationEnabled,
            SamplerAnisotropy = f10.SamplerAnisotropy,
            WireframeFill = f10.FillModeNonSolid,
            GraphicsTimestamps = info.GraphicsTimestamps && info.Properties.Limits.TimestampPeriod > 0,
            TimestampPeriodNs = info.Properties.Limits.TimestampPeriod,
            BufferImageGranularity = info.Properties.Limits.BufferImageGranularity,
            NonCoherentAtomSize = info.Properties.Limits.NonCoherentAtomSize,
            MinUniformBufferOffsetAlignment = info.Properties.Limits.MinUniformBufferOffsetAlignment,
            MinStorageBufferOffsetAlignment = info.Properties.Limits.MinStorageBufferOffsetAlignment,
            MaxPushConstantsSize = info.Properties.Limits.MaxPushConstantsSize,
            MaxMemoryAllocationCount = info.Properties.Limits.MaxMemoryAllocationCount,
            MaxSamplerAnisotropy = info.Properties.Limits.MaxSamplerAnisotropy,
            MaxUpdateAfterBindSampledImages = props12.MaxDescriptorSetUpdateAfterBindSampledImages,
            MaxUpdateAfterBindStorageBuffers = props12.MaxDescriptorSetUpdateAfterBindStorageBuffers,
            MaxUpdateAfterBindStorageImages = props12.MaxDescriptorSetUpdateAfterBindStorageImages,
            MaxUpdateAfterBindSamplers = props12.MaxDescriptorSetUpdateAfterBindSamplers,
        };

        SetObjectName(ObjectType.Instance, (ulong)Instance.Handle, "Instance");
        SetObjectName(ObjectType.PhysicalDevice, (ulong)PhysicalDevice.Handle, info.Name);
        SetObjectName(ObjectType.Device, (ulong)Device.Handle, "Device");
        SetObjectName(ObjectType.Queue, (ulong)GraphicsQueue.Handle, "Queue.Graphics");
        if (HasDedicatedTransferQueue) SetObjectName(ObjectType.Queue, (ulong)TransferQueue.Handle, "Queue.Transfer");
        if (HasAsyncComputeQueue && ComputeQueue.Handle != TransferQueue.Handle) SetObjectName(ObjectType.Queue, (ulong)ComputeQueue.Handle, "Queue.Compute");

        RenderLog.Info(Tag, "Queues: graphics family " + GraphicsFamily
            + (HasDedicatedTransferQueue ? $", dedicated transfer family {TransferFamily}" : ", transfer shares graphics")
            + (HasAsyncComputeQueue ? $", async compute family {ComputeFamily}" : ", compute shares graphics"));
    }

    /// <summary>
    /// Names a Vulkan object for validation messages and GPU debuggers (RenderDoc, Nsight). No-op when
    /// VK_EXT_debug_utils is absent. Called at creation time only, never per frame.
    /// </summary>
    public void SetObjectName(ObjectType type, ulong handle, string name)
    {
        var du = _instance.DebugUtils;
        if (du is null || handle == 0)
        {
            return;
        }

        byte* namePtr = (byte*)SilkMarshal.StringToPtr(name);
        try
        {
            var info = new DebugUtilsObjectNameInfoEXT
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = type,
                ObjectHandle = handle,
                PObjectName = namePtr,
            };
            du.SetDebugUtilsObjectName(Device, &info).Check("vkSetDebugUtilsObjectNameEXT");
        }
        finally
        {
            SilkMarshal.Free((nint)namePtr);
        }
    }

    /// <summary>Begins a labelled region in a command buffer for GPU debuggers. No-op without debug utils.</summary>
    public void BeginLabel(CommandBuffer cmd, string name)
    {
        var du = _instance.DebugUtils;
        if (du is null)
        {
            return;
        }

        byte* namePtr = (byte*)SilkMarshal.StringToPtr(name);
        try
        {
            var label = new DebugUtilsLabelEXT { SType = StructureType.DebugUtilsLabelExt, PLabelName = namePtr };
            du.CmdBeginDebugUtilsLabel(cmd, &label);
        }
        finally
        {
            SilkMarshal.Free((nint)namePtr);
        }
    }

    /// <summary>Allocation-free variant for the frame loop: <paramref name="utf8Name"/> must be a pinned, null-terminated UTF-8 string.</summary>
    public void BeginLabel(CommandBuffer cmd, byte* utf8Name)
    {
        var du = _instance.DebugUtils;
        if (du is null)
        {
            return;
        }

        var label = new DebugUtilsLabelEXT { SType = StructureType.DebugUtilsLabelExt, PLabelName = utf8Name };
        du.CmdBeginDebugUtilsLabel(cmd, &label);
    }

    public void EndLabel(CommandBuffer cmd) => _instance.DebugUtils?.CmdEndDebugUtilsLabel(cmd);

    /// <summary>
    /// Full device stall. Legitimate only at shutdown and swapchain recreation; the steady-state frame
    /// loop synchronises through the frame timeline semaphore instead.
    /// </summary>
    public void WaitIdle() => Vk.DeviceWaitIdle(Device).Check("vkDeviceWaitIdle");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SwapchainExt.Dispose();
        Vk.DestroyDevice(Device, null);
    }

    private static void Chain(ref void** tail, void* node, void** nodeNext)
    {
        *tail = node;
        tail = nodeNext;
    }
}
