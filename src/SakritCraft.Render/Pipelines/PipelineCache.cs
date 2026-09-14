using System.Collections.Concurrent;
using System.Diagnostics;
using SakritCraft.Render.Descriptors;
using SakritCraft.Render.Frame;
using SakritCraft.Render.Shaders;
using SakritCraft.Render.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace SakritCraft.Render.Pipelines;

/// <summary>
/// Owns every pipeline, the driver VkPipelineCache (persisted to disk between runs), and shader hot
/// reload. Start-up compiles are synchronous and fatal on error: a broken shader in the repo should
/// fail the run, not limp. Hot reload is the opposite: compilation and pipeline creation happen on a
/// thread-pool thread so the frame never hitches, and a compile error is logged with file:line while
/// the last good pipeline keeps drawing. The swap of the handle happens on the render thread in
/// <see cref="PumpHotReload"/>, and the old handle goes to the deferred deletion queue so frames still
/// in flight finish with the pipeline they were recorded with.
///
/// Dependency tracking is by absolute path, including every <c>#include</c> the compiler touched, so
/// saving a shared header rebuilds every pipeline that uses it.
/// </summary>
public sealed unsafe class PipelineCache : IDisposable
{
    private const string Tag = "pipeline";

    private sealed class BuildResult
    {
        public required GraphicsPipeline Target;
        public required GraphicsPipelineDesc Desc;
        public required bool Success;
        public required Pipeline Handle;
        public required string[] Dependencies;
        public required string Log;
        public required TimeSpan Duration;
    }

    private readonly VulkanDevice _device;
    private readonly ShaderCompiler _compiler;
    private readonly DescriptorHeap _heap;
    private readonly string _cacheFile;
    private readonly Silk.NET.Vulkan.PipelineCache _vkCache;
    private readonly List<GraphicsPipeline> _pipelines = new();
    private readonly ConcurrentQueue<BuildResult> _completed = new();
    private readonly List<Task> _inFlight = new();
    private readonly List<string> _scratchChanged = new(8);
    private bool _disposed;

    public int PipelineCount => _pipelines.Count;
    /// <summary>Total successful hot reloads since start-up. The client can show this in the title.</summary>
    public int HotReloadCount { get; private set; }
    /// <summary>Hot reloads that failed to compile and were rejected (last good pipeline kept).</summary>
    public int HotReloadFailures { get; private set; }

    public PipelineCache(VulkanDevice device, ShaderCompiler compiler, DescriptorHeap heap, string cacheFile)
    {
        _device = device;
        _compiler = compiler;
        _heap = heap;
        // One cache file per GPU: a laptop with a discrete and an integrated GPU would otherwise
        // overwrite one driver's cache with the other's on every switch.
        string full = Path.GetFullPath(cacheFile);
        var props = device.Properties;
        _cacheFile = Path.Combine(
            Path.GetDirectoryName(full)!,
            $"{Path.GetFileNameWithoutExtension(full)}-{props.VendorID:X4}-{props.DeviceID:X4}{Path.GetExtension(full)}");
        _vkCache = CreateVkCache();
    }

    /// <summary>Compiles and creates a pipeline now. Throws with the compiler log if a shader fails: start-up must be loud.</summary>
    public GraphicsPipeline CreateGraphics(GraphicsPipelineDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pipeline = new GraphicsPipeline(desc);
        var result = Build(pipeline, desc);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Pipeline '{desc.Name}' failed to build:{Environment.NewLine}{result.Log}");
        }

        Apply(result, null, 0);
        _pipelines.Add(pipeline);
        return pipeline;
    }

    /// <summary>
    /// Called by the renderer with the paths the <see cref="ShaderWatcher"/> reported. Starts an async
    /// rebuild for every pipeline that depends on any of them.
    /// </summary>
    public void NotifyShadersChanged(List<string> changedPaths)
    {
        if (_disposed || changedPaths.Count == 0)
        {
            return;
        }

        foreach (var pipeline in _pipelines)
        {
            bool affected = false;
            foreach (var changed in changedPaths)
            {
                foreach (var dep in pipeline.Dependencies)
                {
                    if (string.Equals(dep, changed, StringComparison.OrdinalIgnoreCase))
                    {
                        affected = true;
                        break;
                    }
                }

                if (affected) break;
            }

            if (!affected)
            {
                continue;
            }

            if (pipeline.BuildInFlight)
            {
                pipeline.Dirty = true; // another save landed mid-build; rebuild again when it finishes
                continue;
            }

            StartAsyncBuild(pipeline, pipeline.Desc);
        }

        foreach (var changed in changedPaths)
        {
            RenderLog.Info(Tag, $"Changed: {Path.GetRelativePath(_compiler.Root, changed)}");
        }
    }

    /// <summary>
    /// Render-thread step: applies finished async builds. Successful ones swap the live handle and
    /// retire the old one after <paramref name="retireAfter"/>; failed ones are logged and dropped.
    /// Allocation-free when nothing has completed.
    /// </summary>
    public void PumpHotReload(DeferredDeletionQueue deletions, ulong retireAfter)
    {
        while (_completed.TryDequeue(out var result))
        {
            var pipeline = result.Target;
            pipeline.BuildInFlight = false;

            if (_disposed)
            {
                if (result.Success) _device.Vk.DestroyPipeline(_device.Device, result.Handle, null);
                continue;
            }

            if (result.Success)
            {
                Apply(result, deletions, retireAfter);
                HotReloadCount++;
                RenderLog.Info(Tag, $"Hot reload OK: '{pipeline.Desc.Name}' v{pipeline.Version} in {result.Duration.TotalMilliseconds:F0} ms" +
                                    (result.Log.Length > 0 ? Environment.NewLine + result.Log : ""));
            }
            else
            {
                HotReloadFailures++;
                RenderLog.Error(Tag, $"Hot reload FAILED for '{pipeline.Desc.Name}'; keeping v{pipeline.Version}.{Environment.NewLine}{result.Log}");
            }

            if (pipeline.Dirty)
            {
                pipeline.Dirty = false;
                StartAsyncBuild(pipeline, pipeline.Desc);
            }
        }
    }

    /// <summary>
    /// Rebuilds, synchronously, every pipeline whose colour format differs from the swapchain's. Called
    /// after a swapchain recreate while the device is idle, so old handles are destroyed immediately.
    /// </summary>
    public void UpdateColorFormat(Format colorFormat)
    {
        foreach (var pipeline in _pipelines)
        {
            if (pipeline.Desc.ColorFormat == colorFormat)
            {
                continue;
            }

            var desc = pipeline.Desc with { ColorFormat = colorFormat };
            var result = Build(pipeline, desc);
            if (!result.Success)
            {
                throw new InvalidOperationException($"Pipeline '{desc.Name}' failed to rebuild for {colorFormat}:{Environment.NewLine}{result.Log}");
            }

            var old = pipeline.Handle;
            Apply(result, null, 0);
            _device.Vk.DestroyPipeline(_device.Device, old, null);
            RenderLog.Info(Tag, $"Rebuilt '{desc.Name}' for colour format {colorFormat}");
        }
    }

    // ---- Build machinery -----------------------------------------------------------------------

    private void StartAsyncBuild(GraphicsPipeline pipeline, GraphicsPipelineDesc desc)
    {
        pipeline.BuildInFlight = true;
        var task = Task.Run(() =>
        {
            BuildResult result;
            try
            {
                result = Build(pipeline, desc);
            }
            catch (Exception ex)
            {
                result = new BuildResult
                {
                    Target = pipeline, Desc = desc, Success = false, Handle = default,
                    Dependencies = pipeline.Dependencies, Log = $"exception during build: {ex}", Duration = TimeSpan.Zero,
                };
            }

            _completed.Enqueue(result);
        });
        _inFlight.Add(task);
        _inFlight.RemoveAll(t => t.IsCompleted);
    }

    private void Apply(BuildResult result, DeferredDeletionQueue? deletions, ulong retireAfter)
    {
        var pipeline = result.Target;
        if (pipeline.Handle.Handle != 0 && deletions is not null)
        {
            deletions.Defer(pipeline.Handle, retireAfter);
        }

        pipeline.Handle = result.Handle;
        pipeline.Desc = result.Desc;
        pipeline.Dependencies = result.Dependencies;
        pipeline.Version++;
        _device.SetObjectName(ObjectType.Pipeline, result.Handle.Handle, $"{result.Desc.Name} v{pipeline.Version}");
    }

    private BuildResult Build(GraphicsPipeline target, GraphicsPipelineDesc desc)
    {
        var sw = Stopwatch.StartNew();
        var vs = _compiler.Compile(desc.VertexShader);
        var fs = _compiler.Compile(desc.FragmentShader);

        var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in vs.Dependencies) deps.Add(d);
        foreach (var d in fs.Dependencies) deps.Add(d);
        var depArray = deps.ToArray();

        string log = JoinLogs(vs, fs);
        if (!vs.Success || !fs.Success)
        {
            return new BuildResult { Target = target, Desc = desc, Success = false, Handle = default, Dependencies = depArray, Log = log, Duration = sw.Elapsed };
        }

        var vk = _device.Vk;
        ShaderModule vsModule = CreateModule(vs.Spirv, desc.Name + ".vert");
        ShaderModule fsModule = CreateModule(fs.Spirv, desc.Name + ".frag");
        byte* entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vsModule,
                PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fsModule,
                PName = entry,
            };

            // Vertex pulling: no vertex input bindings at all.
            var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = desc.Topology,
                PrimitiveRestartEnable = false,
            };
            var viewport = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1, // pointers null: dynamic
            };
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = desc.PolygonMode,
                CullMode = desc.CullMode,
                FrontFace = desc.FrontFace,
                LineWidth = 1.0f,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                DepthBiasEnable = false,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = desc.DepthTest && desc.DepthFormat != Format.Undefined,
                DepthWriteEnable = desc.DepthWrite && desc.DepthFormat != Format.Undefined,
                DepthCompareOp = desc.DepthCompare,
            };
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = desc.BlendEnable,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };
            var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };
            var colorFormat = desc.ColorFormat;
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &colorFormat,
                DepthAttachmentFormat = desc.DepthFormat,
                StencilAttachmentFormat = Format.Undefined,
            };
            var createInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewport,
                PRasterizationState = &raster,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = _heap.PipelineLayout,
                RenderPass = default, // dynamic rendering
                Subpass = 0,
            };

            vk.CreateGraphicsPipelines(_device.Device, _vkCache, 1, &createInfo, null, out Pipeline pipeline).Check("vkCreateGraphicsPipelines");
            return new BuildResult { Target = target, Desc = desc, Success = true, Handle = pipeline, Dependencies = depArray, Log = log, Duration = sw.Elapsed };
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            vk.DestroyShaderModule(_device.Device, vsModule, null);
            vk.DestroyShaderModule(_device.Device, fsModule, null);
        }
    }

    private ShaderModule CreateModule(byte[] spirv, string name)
    {
        fixed (byte* code = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            _device.Vk.CreateShaderModule(_device.Device, &info, null, out ShaderModule module).Check("vkCreateShaderModule");
            _device.SetObjectName(ObjectType.ShaderModule, module.Handle, name);
            return module;
        }
    }

    private static string JoinLogs(ShaderCompileResult a, ShaderCompileResult b)
    {
        if (a.Log.Length == 0) return b.Log;
        if (b.Log.Length == 0) return a.Log;
        return a.Log + Environment.NewLine + b.Log;
    }

    // ---- Driver pipeline cache persistence -------------------------------------------------------

    private Silk.NET.Vulkan.PipelineCache CreateVkCache()
    {
        byte[] initial = Array.Empty<byte>();
        if (File.Exists(_cacheFile))
        {
            try
            {
                var data = File.ReadAllBytes(_cacheFile);
                if (IsCacheCompatible(data))
                {
                    initial = data;
                    RenderLog.Info(Tag, $"Loaded pipeline cache ({data.Length / 1024} KiB) from {_cacheFile}");
                }
                else
                {
                    RenderLog.Info(Tag, "Pipeline cache on disk is for a different device/driver; starting empty.");
                }
            }
            catch (IOException ex)
            {
                RenderLog.Warn(Tag, $"Could not read pipeline cache: {ex.Message}");
            }
        }

        fixed (byte* p = initial)
        {
            var info = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)initial.Length,
                PInitialData = initial.Length > 0 ? p : null,
            };
            _device.Vk.CreatePipelineCache(_device.Device, &info, null, out Silk.NET.Vulkan.PipelineCache cache).Check("vkCreatePipelineCache");
            _device.SetObjectName(ObjectType.PipelineCache, cache.Handle, "DriverPipelineCache");
            return cache;
        }
    }

    /// <summary>Checks the standard 32-byte header (length, version, vendor, device, UUID) against this device.</summary>
    private bool IsCacheCompatible(byte[] data)
    {
        if (data.Length < 32) return false;
        uint headerLength = BitConverter.ToUInt32(data, 0);
        uint headerVersion = BitConverter.ToUInt32(data, 4);
        uint vendor = BitConverter.ToUInt32(data, 8);
        uint deviceId = BitConverter.ToUInt32(data, 12);
        if (headerLength != 32 || headerVersion != 1) return false;
        var props = _device.Properties;
        if (vendor != props.VendorID || deviceId != props.DeviceID) return false;
        for (int i = 0; i < 16; i++)
        {
            if (data[16 + i] != props.PipelineCacheUuid[i]) return false;
        }

        return true;
    }

    private void SaveVkCache()
    {
        try
        {
            nuint size = 0;
            _device.Vk.GetPipelineCacheData(_device.Device, _vkCache, &size, null).Check("vkGetPipelineCacheData");
            if (size == 0) return;
            var data = new byte[(int)size];
            fixed (byte* p = data)
            {
                _device.Vk.GetPipelineCacheData(_device.Device, _vkCache, &size, p).Check("vkGetPipelineCacheData");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            File.WriteAllBytes(_cacheFile, data);
            RenderLog.Info(Tag, $"Saved pipeline cache ({size / 1024} KiB) to {_cacheFile}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RenderLog.Warn(Tag, $"Could not save pipeline cache: {ex.Message}");
        }
    }

    /// <summary>Caller must have made the device idle first; live pipelines are destroyed immediately.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Let background builds finish so they do not touch a destroyed device, then discard their output.
        if (_inFlight.Count > 0)
        {
            try { Task.WaitAll(_inFlight.ToArray(), TimeSpan.FromSeconds(10)); }
            catch (AggregateException) { /* build exceptions were already captured into results */ }
        }

        while (_completed.TryDequeue(out var result))
        {
            if (result.Success) _device.Vk.DestroyPipeline(_device.Device, result.Handle, null);
        }

        SaveVkCache();
        foreach (var pipeline in _pipelines)
        {
            if (pipeline.Handle.Handle != 0)
            {
                _device.Vk.DestroyPipeline(_device.Device, pipeline.Handle, null);
                pipeline.Handle = default;
            }
        }

        _pipelines.Clear();
        _device.Vk.DestroyPipelineCache(_device.Device, _vkCache, null);
    }
}
