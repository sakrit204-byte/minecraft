namespace SakritCraft.Render;

/// <summary>Which swapchain present mode to ask for. Every choice falls back to FIFO, which Vulkan guarantees.</summary>
public enum PresentPreference
{
    /// <summary>Lowest latency without tearing: the newest frame replaces the queued one. Preferred on desktop.</summary>
    Mailbox,
    /// <summary>Classic vsync: frames queue and present in order.</summary>
    Fifo,
    /// <summary>Tearing allowed. Only for benchmarking.</summary>
    Immediate,
}

/// <summary>
/// Start-up configuration for the <see cref="Renderer"/>. Everything here is a decision the client
/// makes once; runtime state lives in the renderer. Defaults are tuned for the reference laptop
/// (RTX 4050, 1080p, 60 Hz) described in docs/MASTER-PLAN.html section 00.
/// </summary>
public sealed class RendererOptions
{
    /// <summary>Application name reported to the driver (app-specific driver profiles key off this).</summary>
    public string ApplicationName { get; init; } = "SakritCraft";

    /// <summary>
    /// Number of frames the CPU may run ahead of the GPU. 2 halves input latency versus 3 at the cost
    /// of occasionally starving the GPU when a CPU frame spikes; 3 is safer while the sim thread is
    /// young. Bounded to [2,3] because every per-frame resource is sized by it.
    /// </summary>
    public int FramesInFlight { get; init; } = 3;

    /// <summary>Present mode preference. See <see cref="PresentPreference"/>.</summary>
    public PresentPreference PresentMode { get; init; } = PresentPreference.Mailbox;

    /// <summary>
    /// CPU-side frame limiter in frames per second; 0 disables. With Mailbox presentation an uncapped
    /// triangle would run at thousands of fps and burn the laptop battery, so the client normally
    /// passes the monitor refresh rate here. FIFO already caps at refresh, so this is redundant there.
    /// </summary>
    public double TargetFrameRate { get; init; } = 60.0;

    /// <summary>
    /// Enable VK_LAYER_KHRONOS_validation if, and only if, the loader can find it. There is no Vulkan
    /// SDK on the reference machine, so this must never be a hard requirement.
    /// </summary>
    public bool EnableValidationIfAvailable { get; init; } = true;

    /// <summary>Directory containing GLSL sources. Watched for hot reload.</summary>
    public string ShaderDirectory { get; init; } = "shaders";

    /// <summary>File the driver pipeline cache is persisted to between runs. Cuts start-up pipeline compile time.</summary>
    public string PipelineCacheFile { get; init; } = Path.Combine("artifacts", "pipeline.cache");

    /// <summary>Enable shader hot reload via a file-system watcher on <see cref="ShaderDirectory"/>.</summary>
    public bool EnableShaderHotReload { get; init; } = true;

    /// <summary>Force a specific physical device by index (as enumerated by the loader). Null = auto-select best.</summary>
    public int? ForcePhysicalDeviceIndex { get; init; }

    /// <summary>How often the frame-timing readout updates the window title and console, in seconds.</summary>
    public double TimingReportInterval { get; init; } = 0.5;

    internal void Validate()
    {
        if (FramesInFlight is < 2 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(FramesInFlight), FramesInFlight, "FramesInFlight must be 2 or 3.");
        }

        if (TargetFrameRate < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TargetFrameRate), TargetFrameRate, "TargetFrameRate must be >= 0.");
        }
    }
}
