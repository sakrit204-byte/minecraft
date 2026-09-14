using System.Text;
using Silk.NET.Vulkan;

namespace SakritCraft.Render.Vulkan;

/// <summary>
/// Everything the rest of the engine needs to know about the GPU it is running on, captured once at
/// device creation. The design doc requires every ray-traced feature to have a rasterised twin
/// (docs/MASTER-PLAN.html section 08, "The no-ray-tracing path"); lighting, terrain culling and the
/// material system branch on these flags, so they are load-bearing, not diagnostics.
///
/// Flags describe features that were actually <b>enabled</b> on the logical device, not merely
/// advertised by the physical device, so a <c>true</c> here means the feature may be used right now.
/// </summary>
public readonly struct GpuCapabilities
{
    // Identity -------------------------------------------------------------------------------

    /// <summary>Driver-reported device name, e.g. "NVIDIA GeForce RTX 4050 Laptop GPU".</summary>
    public string DeviceName { get; init; }
    public PhysicalDeviceType DeviceType { get; init; }
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    /// <summary>Highest Vulkan version the device supports, e.g. "1.3.278".</summary>
    public string ApiVersion { get; init; }
    /// <summary>Human-readable driver name from VK_KHR_driver_properties (core in 1.2), e.g. "NVIDIA".</summary>
    public string DriverName { get; init; }
    /// <summary>Driver version string as the vendor formats it, e.g. "556.19".</summary>
    public string DriverInfo { get; init; }
    /// <summary>Sum of all DEVICE_LOCAL heaps in bytes. What "VRAM" means for budgeting.</summary>
    public ulong DeviceLocalMemoryBytes { get; init; }

    // Optional features (enabled on the logical device) -------------------------------------

    /// <summary>VK_KHR_ray_query + VK_KHR_acceleration_structure: inline ray queries in any shader stage.</summary>
    public bool RayQuery { get; init; }
    /// <summary>VK_KHR_acceleration_structure: BLAS/TLAS build. Required by <see cref="RayQuery"/>.</summary>
    public bool AccelerationStructure { get; init; }
    /// <summary>VK_EXT_mesh_shader: mesh + task shaders for GPU-driven terrain clusters.</summary>
    public bool MeshShader { get; init; }
    /// <summary>VK_EXT_descriptor_buffer: descriptors as plain memory. Alternative bindless path.</summary>
    public bool DescriptorBuffer { get; init; }
    /// <summary>VK_KHR_dynamic_rendering_local_read: read attachments inside a dynamic render pass (tile-friendly deferred).</summary>
    public bool DynamicRenderingLocalRead { get; init; }
    /// <summary>VK_EXT_memory_budget: heap budget/usage queries for streaming decisions.</summary>
    public bool MemoryBudget { get; init; }
    /// <summary>VK_EXT_debug_utils available on the instance: object names and labels are being emitted.</summary>
    public bool DebugUtils { get; init; }
    /// <summary>Validation layer was found and enabled.</summary>
    public bool ValidationLayer { get; init; }
    /// <summary>Anisotropic filtering was enabled.</summary>
    public bool SamplerAnisotropy { get; init; }
    /// <summary>Wireframe (fillModeNonSolid) was enabled. Debug views only.</summary>
    public bool WireframeFill { get; init; }
    /// <summary>The graphics queue supports timestamp queries; GPU frame timing is available.</summary>
    public bool GraphicsTimestamps { get; init; }

    // Limits ---------------------------------------------------------------------------------

    /// <summary>Nanoseconds per timestamp tick.</summary>
    public float TimestampPeriodNs { get; init; }
    /// <summary>Required distance between linear and optimal-tiled resources inside one VkDeviceMemory.</summary>
    public ulong BufferImageGranularity { get; init; }
    public ulong NonCoherentAtomSize { get; init; }
    public ulong MinUniformBufferOffsetAlignment { get; init; }
    public ulong MinStorageBufferOffsetAlignment { get; init; }
    public uint MaxPushConstantsSize { get; init; }
    public uint MaxMemoryAllocationCount { get; init; }
    public float MaxSamplerAnisotropy { get; init; }
    public uint MaxUpdateAfterBindSampledImages { get; init; }
    public uint MaxUpdateAfterBindStorageBuffers { get; init; }
    public uint MaxUpdateAfterBindStorageImages { get; init; }
    public uint MaxUpdateAfterBindSamplers { get; init; }

    // Derived --------------------------------------------------------------------------------

    /// <summary>
    /// True when the physically-based lighting path (ray-traced sun shadows, probe GI via ray query) can
    /// run. False means the cascaded-shadow-map / rasterised-probe fallback must be used.
    /// </summary>
    public bool SupportsRayTracedLighting => RayQuery && AccelerationStructure;

    /// <summary>True when terrain may use the mesh-shader cluster path instead of CPU-built indirect draws.</summary>
    public bool SupportsGpuDrivenTerrain => MeshShader;

    /// <summary>Multi-line human-readable dump for the start-up log.</summary>
    public string Describe()
    {
        var sb = new StringBuilder(512);
        sb.Append("GPU            : ").Append(DeviceName).Append(" (").Append(DeviceType).Append(')').AppendLine();
        sb.Append("Vulkan         : ").Append(ApiVersion).AppendLine();
        sb.Append("Driver         : ").Append(DriverName).Append(' ').Append(DriverInfo).AppendLine();
        sb.Append("VRAM           : ").Append(DeviceLocalMemoryBytes / (1024.0 * 1024.0 * 1024.0)).Append(" GiB").AppendLine();
        sb.Append("Ray query      : ").Append(Flag(RayQuery)).AppendLine();
        sb.Append("Accel struct   : ").Append(Flag(AccelerationStructure)).AppendLine();
        sb.Append("Mesh shader    : ").Append(Flag(MeshShader)).AppendLine();
        sb.Append("Descr. buffer  : ").Append(Flag(DescriptorBuffer)).AppendLine();
        sb.Append("Local read     : ").Append(Flag(DynamicRenderingLocalRead)).AppendLine();
        sb.Append("Memory budget  : ").Append(Flag(MemoryBudget)).AppendLine();
        sb.Append("Debug utils    : ").Append(Flag(DebugUtils)).AppendLine();
        sb.Append("Validation     : ").Append(Flag(ValidationLayer)).AppendLine();
        sb.Append("GPU timestamps : ").Append(Flag(GraphicsTimestamps)).Append(" (").Append(TimestampPeriodNs).Append(" ns/tick)").AppendLine();
        sb.Append("Bindless limits: images ").Append(MaxUpdateAfterBindSampledImages)
          .Append(", storage buffers ").Append(MaxUpdateAfterBindStorageBuffers)
          .Append(", storage images ").Append(MaxUpdateAfterBindStorageImages)
          .Append(", samplers ").Append(MaxUpdateAfterBindSamplers).AppendLine();
        sb.Append("Lighting path  : ").Append(SupportsRayTracedLighting ? "ray traced" : "rasterised fallback").AppendLine();
        sb.Append("Terrain path   : ").Append(SupportsGpuDrivenTerrain ? "mesh shader clusters" : "indirect draws");
        return sb.ToString();
    }

    private static string Flag(bool b) => b ? "yes" : "no";
}
