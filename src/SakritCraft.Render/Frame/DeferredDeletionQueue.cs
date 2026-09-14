using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace SakritCraft.Render.Frame;

/// <summary>
/// Destroys GPU objects only once the frame timeline proves the GPU is finished with them. Anything
/// replaced while frames are in flight (a hot-reloaded pipeline, a resized render target, a chunk mesh
/// that was unloaded) is parked here with the timeline value of the last submission that may reference
/// it, and <see cref="Collect"/> destroys it after that value completes. The queue is a plain list of
/// structs so the steady-state per-frame collect does not allocate.
/// </summary>
public sealed unsafe class DeferredDeletionQueue : IDisposable
{
    private enum Kind : byte
    {
        Pipeline,
        ShaderModule,
        ImageView,
        Image,
        Sampler,
        Managed,
    }

    private struct Entry
    {
        public ulong RetireAfter;
        public Kind Kind;
        public ulong Handle;
        public IDisposable? Managed;
    }

    private readonly VulkanDevice _device;
    private readonly List<Entry> _entries = new(64);
    private bool _disposed;

    public int PendingCount => _entries.Count;

    public DeferredDeletionQueue(VulkanDevice device) => _device = device;

    /// <summary>Destroy <paramref name="pipeline"/> once timeline value <paramref name="retireAfter"/> has completed.</summary>
    public void Defer(Pipeline pipeline, ulong retireAfter) => Add(Kind.Pipeline, pipeline.Handle, null, retireAfter);
    public void Defer(ShaderModule module, ulong retireAfter) => Add(Kind.ShaderModule, module.Handle, null, retireAfter);
    public void Defer(ImageView view, ulong retireAfter) => Add(Kind.ImageView, view.Handle, null, retireAfter);
    public void Defer(Image image, ulong retireAfter) => Add(Kind.Image, image.Handle, null, retireAfter);
    public void Defer(Sampler sampler, ulong retireAfter) => Add(Kind.Sampler, sampler.Handle, null, retireAfter);
    /// <summary>Dispose a managed wrapper (e.g. a <see cref="Memory.GpuBuffer"/>) once the GPU is done with it.</summary>
    public void Defer(IDisposable resource, ulong retireAfter) => Add(Kind.Managed, 0, resource, retireAfter);

    /// <summary>Destroys every entry whose retire value is at or below <paramref name="completedValue"/>.</summary>
    public void Collect(ulong completedValue)
    {
        // Swap-remove keeps this O(n) with no allocation; order of destruction does not matter here.
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].RetireAfter <= completedValue)
            {
                Destroy(_entries[i]);
                int last = _entries.Count - 1;
                _entries[i] = _entries[last];
                _entries.RemoveAt(last);
            }
        }
    }

    /// <summary>Destroys everything regardless of timeline. Only after the device is idle.</summary>
    public void Flush()
    {
        foreach (var entry in _entries)
        {
            Destroy(entry);
        }

        _entries.Clear();
    }

    private void Add(Kind kind, ulong handle, IDisposable? managed, ulong retireAfter)
    {
        if (handle == 0 && managed is null)
        {
            return;
        }

        _entries.Add(new Entry { RetireAfter = retireAfter, Kind = kind, Handle = handle, Managed = managed });
    }

    private void Destroy(in Entry entry)
    {
        var vk = _device.Vk;
        switch (entry.Kind)
        {
            case Kind.Pipeline: vk.DestroyPipeline(_device.Device, new Pipeline(entry.Handle), null); break;
            case Kind.ShaderModule: vk.DestroyShaderModule(_device.Device, new ShaderModule(entry.Handle), null); break;
            case Kind.ImageView: vk.DestroyImageView(_device.Device, new ImageView(entry.Handle), null); break;
            case Kind.Image: vk.DestroyImage(_device.Device, new Image(entry.Handle), null); break;
            case Kind.Sampler: vk.DestroySampler(_device.Device, new Sampler(entry.Handle), null); break;
            case Kind.Managed: entry.Managed!.Dispose(); break;
            default: throw new InvalidOperationException($"Unknown deferred deletion kind {entry.Kind}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Flush();
    }
}
