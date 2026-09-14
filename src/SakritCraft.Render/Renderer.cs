using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SakritCraft.Render.Cameras;
using SakritCraft.Render.Descriptors;
using SakritCraft.Render.Frame;
using SakritCraft.Render.Memory;
using SakritCraft.Render.Pipelines;
using SakritCraft.Render.Shaders;
using SakritCraft.Render.Sky;
using SakritCraft.Render.Terrain;
using SakritCraft.Render.Upload;
using SakritCraft.Render.Vulkan;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using PipelineCache = SakritCraft.Render.Pipelines.PipelineCache;

namespace SakritCraft.Render;

/// <summary>Tunable sun and atmosphere parameters until the section 08 lighting pipeline owns them.</summary>
public sealed class SceneLighting
{
    /// <summary>Sun elevation above the horizon, degrees. Low enough that slopes read as slopes.</summary>
    public float SunElevationDegrees = 27.0f;
    /// <summary>Sun azimuth, degrees clockwise from -Z (the camera's yaw-0 forward). Side light gives relief without shadows.</summary>
    public float SunAzimuthDegrees = 250.0f;
    public float SunIntensity = 3.2f;
    public Vector3 SunColor = new(1.0f, 0.93f, 0.82f);
    public Vector3 SkyZenith = new(0.14f, 0.31f, 0.72f);
    public Vector3 SkyHorizon = new(0.66f, 0.74f, 0.86f);
    public Vector3 GroundAmbient = new(0.20f, 0.17f, 0.13f);
    /// <summary>Extinction per metre. 0.0016 puts the 50% fog line around 430 m: clear-day aerial perspective.</summary>
    public float FogDensity = 0.0016f;
    public float FogHeightFalloff = 1.0f / 90.0f;
    public float Exposure = 0.9f;

    public Vector3 SunDirection
    {
        get
        {
            float el = SunElevationDegrees * MathF.PI / 180.0f;
            float az = SunAzimuthDegrees * MathF.PI / 180.0f;
            return Vector3.Normalize(new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), -MathF.Cos(az) * MathF.Cos(el)));
        }
    }
}

/// <summary>
/// Owns the whole graphics stack and drives one frame per <see cref="RenderFrame"/> call. The frame
/// loop is built around a single timeline semaphore: every submission signals the next value, each
/// frame slot remembers the value it signalled, and reusing a slot means waiting for that value on the
/// host. That replaces per-frame fences, gives the deferred-deletion queue and GPU timestamps a single
/// notion of "done", and never stalls the whole device. Swapchain acquire/present still use binary
/// semaphores because WSI requires them.
///
/// M2 frame: (1) drain finished chunk meshes into the transfer queue's batch, (2) record acquire
/// barriers for last frame's batch and promote those chunks to resident, (3) clear a reverse-Z depth
/// buffer, draw every resident chunk that survives frustum culling with camera-relative offsets, then
/// the sky where depth is still clear, (4) submit waiting on the swapchain acquire and on the upload
/// timeline. Camera-relative rendering is the load-bearing decision here: no matrix or vertex the GPU
/// sees ever contains a world position (docs/MASTER-PLAN.html section 03).
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    private const string Tag = "renderer";
    private const ulong StagingRingBytes = 64UL * 1024 * 1024;

    private readonly IWindow _window;
    private readonly RendererOptions _options;
    private readonly TerrainOptions _terrainOptions;
    private readonly string _titleBase;

    private readonly VulkanInstance _instance;
    private readonly SurfaceKHR _surface;
    private readonly VulkanDevice _device;
    private readonly GpuAllocator _allocator;
    private readonly DescriptorHeap _heap;
    private readonly Swapchain _swapchain;
    private readonly FrameContext[] _frames;
    private readonly Semaphore _timeline;
    private readonly GpuTimestamps _timestamps;
    private readonly DeferredDeletionQueue _deletions;
    private readonly ShaderCompiler _compiler;
    private readonly ShaderWatcher? _watcher;
    private readonly PipelineCache _pipelines;
    private readonly TransferUploader _uploader;
    private readonly Format _depthFormat;
    private GpuImage _depth;
    private readonly GpuBuffer[] _frameConstantBuffers;
    private readonly BindlessHandle[] _frameConstantHandles;
    private readonly GpuImage _defaultTexture;
    private readonly BindlessHandle _defaultTextureHandle;
    private readonly TerrainRenderer _terrain;
    private readonly SkyPass _sky;
    private readonly FrameTiming _timing;
    private readonly FramePacer _pacer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _changedShaders = new(8);
    private readonly byte[] _labelFrame = PinnedUtf8("Frame");
    private readonly byte[] _labelTerrain = PinnedUtf8("Terrain");
    private readonly byte[] _labelSky = PinnedUtf8("Sky");

    private FrameConstants _frameConstants;
    private RenderStats _stats;
    private ulong _timelineValue;
    private int _frameIndex;
    private bool _resizePending;
    private bool _disposed;

    public GpuCapabilities Capabilities => _device.Capabilities;
    public FrameTiming Timing => _timing;
    public VulkanDevice Device => _device;
    public GpuAllocator Allocator => _allocator;
    public DescriptorHeap Descriptors => _heap;
    public PipelineCache Pipelines => _pipelines;
    public TerrainRenderer Terrain => _terrain;
    public RenderStats Stats => _stats;
    /// <summary>The camera the client moves; the renderer reads it at the start of every frame.</summary>
    public Camera Camera { get; } = new();
    public SceneLighting Lighting { get; } = new();
    /// <summary>Debug view: draw terrain as lines. Ignored when the device lacks fillModeNonSolid.</summary>
    public bool Wireframe { get; set; }

    public Renderer(IWindow window, RendererOptions options, TerrainOptions terrainOptions)
    {
        options.Validate();
        _window = window;
        _options = options;
        _terrainOptions = terrainOptions;
        _titleBase = window.Title;

        var surfaceSource = window.VkSurface
            ?? throw new InvalidOperationException("Window was not created with a Vulkan context (use WindowOptions.DefaultVulkan).");

        var sw = Stopwatch.StartNew();
        _instance = new VulkanInstance(surfaceSource, options);
        _surface = _instance.CreateSurface(surfaceSource);
        var physical = PhysicalDeviceSelector.Select(_instance, _surface, options);
        _device = new VulkanDevice(_instance, physical);
        RenderLog.Info(Tag, Environment.NewLine + _device.Capabilities.Describe());

        _allocator = new GpuAllocator(_device);
        _heap = new DescriptorHeap(_device);

        var fb = window.FramebufferSize;
        _swapchain = new Swapchain(_device, _surface, options.PresentMode, (uint)fb.X, (uint)fb.Y);

        var timelineType = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };
        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &timelineType };
        _device.Vk.CreateSemaphore(_device.Device, &semInfo, null, out _timeline).Check("vkCreateSemaphore(timeline)");
        _device.SetObjectName(ObjectType.Semaphore, _timeline.Handle, "FrameTimeline");

        _frames = new FrameContext[options.FramesInFlight];
        for (int i = 0; i < _frames.Length; i++)
        {
            _frames[i] = new FrameContext(_device, i);
        }

        _timestamps = new GpuTimestamps(_device, options.FramesInFlight);
        _deletions = new DeferredDeletionQueue(_device);
        _uploader = new TransferUploader(_device, _allocator, options.FramesInFlight, StagingRingBytes);

        _depthFormat = FormatSupport.ChooseDepthFormat(_device);
        _depth = CreateDepth(_swapchain.Extent);
        RenderLog.Info(Tag, $"Depth: {_depthFormat}, reverse-Z, infinite far plane");

        // One small constants buffer per frame slot; the slot's timeline wait guarantees the GPU has
        // finished reading it before it is rewritten.
        _frameConstantBuffers = new GpuBuffer[options.FramesInFlight];
        _frameConstantHandles = new BindlessHandle[options.FramesInFlight];
        for (int i = 0; i < options.FramesInFlight; i++)
        {
            _frameConstantBuffers[i] = new GpuBuffer(_device, _allocator, (ulong)sizeof(FrameConstants),
                BufferUsageFlags.StorageBufferBit, MemoryUsage.CpuToGpu, $"FrameConstants[{i}]");
            _frameConstantHandles[i] = _heap.RegisterStorageBuffer(_frameConstantBuffers[i].Handle, 0, _frameConstantBuffers[i].Size);
        }

        _compiler = new ShaderCompiler(options.ShaderDirectory);
        _watcher = options.EnableShaderHotReload ? new ShaderWatcher(_compiler.Root) : null;
        _pipelines = new PipelineCache(_device, _compiler, _heap, options.PipelineCacheFile);

        _terrain = new TerrainRenderer(_device, _allocator, _heap, _pipelines, terrainOptions, _swapchain.ImageFormat, _depthFormat);
        _sky = new SkyPass(_device, _pipelines, _heap, _swapchain.ImageFormat, _depthFormat);

        // A 1x1 white texture at a known handle: the material system's "no texture" fallback, and the
        // first exercise of the image upload path (ownership transfer included) before any real asset.
        _defaultTexture = new GpuImage(_device, _allocator, new GpuImageDesc
        {
            Name = "Texture.DefaultWhite",
            Width = 1,
            Height = 1,
            Format = Format.R8G8B8A8Unorm,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
        });
        _defaultTextureHandle = _heap.RegisterSampledImage(_defaultTexture.View, ImageLayout.ShaderReadOnlyOptimal);
        UploadDefaultTexture();

        PlaceCameraAtStart();
        int queued = _terrain.RequestStartupRegion(Camera.Position);

        _timing = new FrameTiming(options.TimingReportInterval);
        _pacer = new FramePacer(options.TargetFrameRate);

        RenderLog.Info(Tag, $"Renderer ready in {sw.ElapsedMilliseconds} ms: {options.FramesInFlight} frames in flight, " +
                            $"{_swapchain.PresentMode}, pacer {(options.TargetFrameRate > 0 ? options.TargetFrameRate + " fps" : "off")}, " +
                            $"{queued} chunks queued, camera at ({Camera.Position.X:F1}, {Camera.Position.Y:F1}, {Camera.Position.Z:F1})");
    }

    /// <summary>Call from the window's framebuffer-resize event. Cheap: records intent, the next frame acts on it.</summary>
    public void NotifyFramebufferResize(int width, int height)
    {
        _resizePending = true;
    }

    /// <summary>Records, submits and presents one frame. Allocation-free in steady state.</summary>
    public void RenderFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timing.BeginFrame();

        PumpShaderHotReload();

        if (_resizePending)
        {
            RecreateSwapchain();
        }

        if (!_swapchain.IsValid)
        {
            // Minimised: nothing to draw. Yield so the message pump does not spin a core.
            Thread.Sleep(16);
            return;
        }

        var frame = _frames[_frameIndex];
        WaitForSlot(frame);

        if (_timestamps.TryRead(frame.Index, out double gpuMs))
        {
            _timing.RecordGpuTime(gpuMs);
        }

        // Uploads first so a mesh finished this frame is on its way to the GPU while we record.
        _uploader.BeginBatch();
        _terrain.PumpUploads(_uploader, _terrainOptions.UploadBytesPerFrame);
        _uploader.EndBatch();

        var acquire = _swapchain.Acquire(frame.ImageAcquired, out uint imageIndex);
        if (acquire == Result.ErrorOutOfDateKhr)
        {
            _resizePending = true;
            return;
        }

        _stats.ResetFrame();
        ulong uploadWait = Record(frame, imageIndex);
        Submit(frame, imageIndex, uploadWait);

        var present = _swapchain.Present(_device.GraphicsQueue, imageIndex);
        if (present == Result.ErrorOutOfDateKhr || present == Result.SuboptimalKhr || acquire == Result.SuboptimalKhr)
        {
            _resizePending = true;
        }

        _frameIndex = (_frameIndex + 1) % _frames.Length;
        _timing.EndCpuWork();

        if (_timing.TryReport(out string report))
        {
            Report(report);
        }

        _pacer.WaitForNextFrame();
    }

    // ---- Frame steps -----------------------------------------------------------------------------

    private void PumpShaderHotReload()
    {
        if (_watcher is not null && _watcher.TryDrain(_changedShaders))
        {
            _pipelines.NotifyShadersChanged(_changedShaders);
            _changedShaders.Clear();
        }

        // Retire replaced pipelines after the most recent submission; frames recorded before this point
        // may still reference them.
        _pipelines.PumpHotReload(_deletions, _timelineValue);
    }

    private void WaitForSlot(FrameContext frame)
    {
        var vk = _device.Vk;
        if (frame.SubmittedTimelineValue != 0)
        {
            var semaphore = _timeline;
            ulong value = frame.SubmittedTimelineValue;
            var waitInfo = new SemaphoreWaitInfo
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &semaphore,
                PValues = &value,
            };
            vk.WaitSemaphores(_device.Device, &waitInfo, ulong.MaxValue).Check("vkWaitSemaphores");
        }

        ulong completed = 0;
        vk.GetSemaphoreCounterValue(_device.Device, _timeline, &completed).Check("vkGetSemaphoreCounterValue");
        _deletions.Collect(completed);
        _terrain.CollectFrees(completed);
    }

    private void WriteFrameConstants(FrameContext frame)
    {
        var extent = _swapchain.Extent;
        Camera.AspectRatio = extent.Width / (float)extent.Height;

        ref var fc = ref _frameConstants;
        fc.ViewProjection = Camera.ViewProjectionForGpu;
        fc.CameraRight = Camera.Right;
        fc.TanHalfFovY = Camera.TanHalfFovY;
        fc.CameraUp = Camera.Up;
        fc.AspectRatio = Camera.AspectRatio;
        fc.CameraForward = Camera.Forward;
        fc.Time = (float)_clock.Elapsed.TotalSeconds;
        fc.SunDirection = Lighting.SunDirection;
        fc.SunIntensity = Lighting.SunIntensity;
        fc.SunColor = Lighting.SunColor;
        fc.FogDensity = Lighting.FogDensity;
        fc.SkyZenith = Lighting.SkyZenith;
        fc.FogHeightFalloff = Lighting.FogHeightFalloff;
        fc.SkyHorizon = Lighting.SkyHorizon;
        fc.Exposure = Lighting.Exposure;
        fc.GroundAmbient = Lighting.GroundAmbient;
        fc.CameraHeight = (float)Camera.Position.Y;
        fc.CameraPositionWrapped = new Vector3(
            (float)Wrap(Camera.Position.X, FrameConstants.PatternPeriod),
            (float)Wrap(Camera.Position.Y, FrameConstants.PatternPeriod),
            (float)Wrap(Camera.Position.Z, FrameConstants.PatternPeriod));

        _frameConstantBuffers[frame.Index].Write(MemoryMarshal.CreateReadOnlySpan(ref fc, 1));
    }

    /// <summary>Records the frame. Returns the upload-timeline value the submission must wait on (0 = none).</summary>
    private ulong Record(FrameContext frame, uint imageIndex)
    {
        var vk = _device.Vk;
        var cmd = frame.Begin();
        var extent = _swapchain.Extent;
        var image = _swapchain.GetImage(imageIndex);

        _timestamps.Begin(cmd, frame.Index);
        fixed (byte* label = _labelFrame)
        {
            _device.BeginLabel(cmd, label);
        }

        // Ownership acquires for last frame's uploads come first; anything acquired here is drawable below.
        ulong uploadWait = _uploader.RecordAcquires(cmd);
        _terrain.PromoteAcquired(_uploader.AcquiredThrough);

        WriteFrameConstants(frame);
        uint frameHandle = _frameConstantHandles[frame.Index].Index;

        // Undefined -> colour attachment. The acquire semaphore (waited at COLOR_ATTACHMENT_OUTPUT) is the
        // execution dependency on the presentation engine; the barrier is the layout transition.
        TransitionSwapchainImage(cmd, image,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);

        // Depth: contents are discarded (UNDEFINED) but the previous frame may still be writing the same
        // image, so the source scope covers its depth tests. Same queue, so this barrier orders against
        // every earlier submission.
        var depthBarrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            SrcAccessMask = AccessFlags2.DepthStencilAttachmentWriteBit,
            DstStageMask = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            DstAccessMask = AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _depth.Handle,
            SubresourceRange = _depth.FullRange,
        };
        var depthDependency = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &depthBarrier };
        vk.CmdPipelineBarrier2(cmd, &depthDependency);

        // Colour is cleared to black only as a safety net; the sky pass covers every pixel the terrain
        // leaves. Depth clears to 0 = far under reverse-Z.
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _swapchain.GetView(imageIndex),
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue { Color = new ClearColorValue(0.0f, 0.0f, 0.0f, 1.0f) },
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _depth.View,
            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(0.0f, 0) },
        };
        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment,
        };
        vk.CmdBeginRendering(cmd, &renderingInfo);

        var viewport = new Viewport(0, 0, extent.Width, extent.Height, 0.0f, 1.0f);
        var scissor = new Rect2D(new Offset2D(0, 0), extent);
        vk.CmdSetViewport(cmd, 0, 1, &viewport);
        vk.CmdSetScissor(cmd, 0, 1, &scissor);
        _heap.Bind(cmd, PipelineBindPoint.Graphics);

        fixed (byte* label = _labelTerrain)
        {
            _device.BeginLabel(cmd, label);
        }
        _terrain.Draw(cmd, Camera, frameHandle, Wireframe, ref _stats);
        _device.EndLabel(cmd);

        fixed (byte* label = _labelSky)
        {
            _device.BeginLabel(cmd, label);
        }
        _sky.Draw(cmd, frameHandle);
        _device.EndLabel(cmd);

        vk.CmdEndRendering(cmd);

        // Colour attachment -> present. BOTTOM_OF_PIPE with no access is the canonical "hand to WSI".
        TransitionSwapchainImage(cmd, image,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None);

        _device.EndLabel(cmd);
        _timestamps.End(cmd, frame.Index);
        frame.End();
        return uploadWait;
    }

    private void TransitionSwapchainImage(CommandBuffer cmd, Image image, ImageLayout from, ImageLayout to,
        PipelineStageFlags2 srcStage, AccessFlags2 srcAccess, PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _device.Vk.CmdPipelineBarrier2(cmd, &dependency);
    }

    private void Submit(FrameContext frame, uint imageIndex, ulong uploadWait)
    {
        ulong signalValue = ++_timelineValue;

        var waits = stackalloc SemaphoreSubmitInfo[2];
        uint waitCount = 0;
        waits[waitCount++] = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = frame.ImageAcquired,
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        };
        if (uploadWait != 0)
        {
            // The release half of the ownership transfer ran on the transfer queue; the acquire recorded
            // in this command buffer is only valid once that submission has completed. Waiting at the
            // consuming stages leaves earlier stages free to start.
            waits[waitCount++] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = _uploader.Timeline,
                Value = uploadWait,
                StageMask = PipelineStageFlags2.IndexInputBit | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit,
            };
        }

        var signals = stackalloc SemaphoreSubmitInfo[2];
        signals[0] = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = _swapchain.GetRenderFinished(imageIndex),
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };
        signals[1] = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = _timeline,
            Value = signalValue,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };
        var cmdInfo = new CommandBufferSubmitInfo
        {
            SType = StructureType.CommandBufferSubmitInfo,
            CommandBuffer = frame.Cmd,
        };
        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = waitCount,
            PWaitSemaphoreInfos = waits,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &cmdInfo,
            SignalSemaphoreInfoCount = 2,
            PSignalSemaphoreInfos = signals,
        };
        _device.Vk.QueueSubmit2(_device.GraphicsQueue, 1, &submit, default).Check("vkQueueSubmit2");

        frame.SubmittedTimelineValue = signalValue;
        frame.ImageIndex = imageIndex;
    }

    private void Report(string timingReport)
    {
        _terrain.FillStats(ref _stats);
        var alloc = _allocator.GetStats();
        _stats.VramUsedBytes = alloc.DeviceLocalUsedBytes;
        _stats.VramReservedBytes = alloc.DeviceLocalReservedBytes;

        string scene = $"tris {RenderStats.FormatTriangles(_stats.TrianglesDrawn)} in {_stats.DrawCalls} draws | " +
                       $"chunks {_stats.ChunksDrawn} drawn / {_stats.ChunksCulled} culled / {_stats.ChunksResident} resident " +
                       $"({_stats.ChunksUploading} uploading, {_stats.ChunksPending} pending, {_stats.ChunksEmpty} empty) | " +
                       $"geometry {RenderStats.FormatBytes(_stats.GeometryBytes)} | vram {RenderStats.FormatBytes(_stats.VramUsedBytes)} / {RenderStats.FormatBytes(_stats.VramReservedBytes)}";
        string camera = $"cam ({Camera.Position.X:F1}, {Camera.Position.Y:F1}, {Camera.Position.Z:F1}) yaw {Camera.Yaw * 180 / MathF.PI:F1} pitch {Camera.Pitch * 180 / MathF.PI:F1}";
        RenderLog.Info("frame", timingReport + " | " + scene + " | " + camera);
        _window.Title = $"{_titleBase}  |  {_timing.Fps:F0} fps  |  {RenderStats.FormatTriangles(_stats.TrianglesDrawn)} tris  |  " +
                        $"{_stats.ChunksDrawn}/{_stats.ChunksResident} chunks  |  vram {RenderStats.FormatBytes(_stats.VramUsedBytes)}  |  " +
                        $"{_swapchain.Extent.Width}x{_swapchain.Extent.Height}  |  reloads {_pipelines.HotReloadCount}";
    }

    // ---- Resources -------------------------------------------------------------------------------

    private GpuImage CreateDepth(Extent2D extent) => new(_device, _allocator, new GpuImageDesc
    {
        Name = "DepthBuffer",
        Width = extent.Width,
        Height = extent.Height,
        Format = _depthFormat,
        Usage = ImageUsageFlags.DepthStencilAttachmentBit,
    });

    private void UploadDefaultTexture()
    {
        _uploader.BeginBatch();
        if (!_uploader.TryStage(4, 4, out var slice))
        {
            throw new InvalidOperationException("Staging ring could not hold four bytes.");
        }

        slice.Span.Fill(0xFF);
        _uploader.CopyToImage(slice, _defaultTexture, 0, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
        _uploader.EndBatch();
    }

    /// <summary>
    /// Starts the camera above the region centre, turned toward the highest ground in the region and
    /// pitched down slightly so both the relief and the horizon are in frame. A fixed yaw would as
    /// often as not look at the flattest part of the map.
    /// </summary>
    private void PlaceCameraAtStart()
    {
        double x = _terrainOptions.CentreX;
        double z = _terrainOptions.CentreZ;
        double surface = _terrain.Field.SurfaceHeight(x, z);

        double chunkSize = new ChunkCoord(0, 0, 0).Size;
        double radius = Math.Min(_terrainOptions.RegionChunksX, _terrainOptions.RegionChunksZ) * chunkSize * 0.42;
        var landform = _terrain.Field.Landform;
        double bestHeight = double.MinValue, bestX = x, bestZ = z - 1.0;
        for (int i = 0; i < 64; i++)
        {
            double angle = i * (2.0 * Math.PI / 64.0);
            for (double r = radius * 0.5; r <= radius; r += radius * 0.25)
            {
                double sx = x + Math.Sin(angle) * r, sz = z - Math.Cos(angle) * r;
                double h = landform.Sample(sx, sz).BaseHeight;
                if (h > bestHeight) { bestHeight = h; bestX = sx; bestZ = sz; }
            }
        }

        // Eye level a little above the local ground, so ridges cut the horizon instead of lying flat
        // below the camera.
        Camera.Position = new Vector3D<double>(x, surface + 22.0, z);
        Camera.Yaw = (float)Math.Atan2(bestX - x, -(bestZ - z));
        Camera.Pitch = -12.0f * MathF.PI / 180.0f;
    }

    private void RecreateSwapchain()
    {
        _resizePending = false;
        // Not steady state: a full stall is the simplest correct way to make every in-flight reference
        // to the old images disappear before they are destroyed.
        _device.WaitIdle();
        _deletions.Collect(_timelineValue);

        var fb = _window.FramebufferSize;
        _swapchain.Recreate((uint)Math.Max(fb.X, 0), (uint)Math.Max(fb.Y, 0));
        if (!_swapchain.IsValid)
        {
            return;
        }

        if (_depth.Extent.Width != _swapchain.Extent.Width || _depth.Extent.Height != _swapchain.Extent.Height)
        {
            _depth.Dispose();
            _depth = CreateDepth(_swapchain.Extent);
        }

        if (_swapchain.ImageFormat != _sky.ColorFormat)
        {
            _pipelines.UpdateColorFormat(_swapchain.ImageFormat);
        }
    }

    /// <summary>Positive modulo in double, so the wrapped value is exact before it is narrowed to float.</summary>
    private static double Wrap(double value, double period)
    {
        double r = value % period;
        return r < 0 ? r + period : r;
    }

    private static byte[] PinnedUtf8(string s)
    {
        var bytes = GC.AllocateArray<byte>(Encoding.UTF8.GetByteCount(s) + 1, pinned: true);
        Encoding.UTF8.GetBytes(s, bytes);
        return bytes;
    }

    /// <summary>Tears everything down in reverse creation order after draining the GPU. Safe to call once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RenderLog.Info(Tag, $"Shutting down after {_timing.FrameCount} frames.");

        _device.WaitIdle();
        _watcher?.Dispose();
        _pipelines.Dispose();      // waits for background builds, saves driver cache
        _compiler.Dispose();

        _terrain.Dispose();        // stops workers, frees geometry pools
        _heap.Free(_defaultTextureHandle);
        _defaultTexture.Dispose();
        for (int i = 0; i < _frameConstantBuffers.Length; i++)
        {
            _heap.Free(_frameConstantHandles[i]);
            _frameConstantBuffers[i].Dispose();
        }

        _depth.Dispose();
        _uploader.Dispose();
        _deletions.Dispose();      // flushes everything; device is idle
        _timestamps.Dispose();
        foreach (var frame in _frames)
        {
            frame.Dispose();
        }

        _device.Vk.DestroySemaphore(_device.Device, _timeline, null);
        _swapchain.Dispose();
        _heap.Dispose();

        var stats = _allocator.GetStats();
        RenderLog.Info(Tag, $"Allocator at exit: {stats.AllocationCount} live allocations in {stats.BlockCount} blocks ({stats.UsedBytes} / {stats.ReservedBytes} bytes).");
        _allocator.Dispose();
        _device.Dispose();
        _instance.DestroySurface(_surface);
        _instance.Dispose();

        RenderLog.Info(Tag, $"Validation: {VulkanInstance.ValidationErrorCount} error(s), {VulkanInstance.ValidationMessageCount} message(s) total.");
    }
}
