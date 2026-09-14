using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;

namespace SakritCraft.Render.Frame;

/// <summary>
/// GPU frame time via timestamp queries: two per frame slot, written at the very start and very end of
/// the frame's command buffer. Results are read back when the slot is reused, by which point the frame
/// timeline guarantees the work has completed, so the readback never blocks. Queries are reset on the
/// GPU inside the command buffer (vkCmdResetQueryPool), which keeps host and device in lock-step
/// without hostQueryReset bookkeeping.
/// </summary>
public sealed unsafe class GpuTimestamps : IDisposable
{
    private readonly VulkanDevice _device;
    private readonly QueryPool _pool;
    private readonly float _periodNs;
    private readonly ulong[] _results;
    private bool _disposed;

    /// <summary>False when the graphics queue has no timestamp support; every method becomes a no-op and <see cref="TryRead"/> returns false.</summary>
    public bool Supported { get; }

    public GpuTimestamps(VulkanDevice device, int framesInFlight)
    {
        _device = device;
        Supported = device.Capabilities.GraphicsTimestamps;
        _periodNs = device.Capabilities.TimestampPeriodNs;
        _results = new ulong[2];
        if (!Supported)
        {
            RenderLog.Warn("vk.timing", "Graphics queue has no timestamp support; GPU frame time unavailable.");
            return;
        }

        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = (uint)(framesInFlight * 2),
        };
        device.Vk.CreateQueryPool(device.Device, &info, null, out _pool).Check("vkCreateQueryPool");
        device.SetObjectName(ObjectType.QueryPool, _pool.Handle, "FrameTimestamps");
    }

    /// <summary>Resets this slot's two queries and writes the "frame start" timestamp. Record first in the command buffer.</summary>
    public void Begin(CommandBuffer cmd, int frameSlot)
    {
        if (!Supported) return;
        uint first = (uint)(frameSlot * 2);
        _device.Vk.CmdResetQueryPool(cmd, _pool, first, 2);
        // TOP_OF_PIPE in sync2 terms is "before any command"; ALL_COMMANDS at the end is "after all commands".
        _device.Vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.TopOfPipeBit, _pool, first);
    }

    /// <summary>Writes the "frame end" timestamp. Record last in the command buffer.</summary>
    public void End(CommandBuffer cmd, int frameSlot)
    {
        if (!Supported) return;
        _device.Vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.AllCommandsBit, _pool, (uint)(frameSlot * 2 + 1));
    }

    /// <summary>
    /// Reads the slot's elapsed GPU time in milliseconds. Returns false when the slot has never been
    /// submitted (first frames) or the results are not yet available, without blocking.
    /// </summary>
    public bool TryRead(int frameSlot, out double milliseconds)
    {
        milliseconds = 0;
        if (!Supported) return false;

        fixed (ulong* p = _results)
        {
            var result = _device.Vk.GetQueryPoolResults(
                _device.Device, _pool, (uint)(frameSlot * 2), 2,
                (nuint)(sizeof(ulong) * 2), p, sizeof(ulong), QueryResultFlags.Result64Bit);
            if (result == Result.NotReady)
            {
                return false;
            }

            result.Check("vkGetQueryPoolResults");
        }

        if (_results[1] < _results[0])
        {
            return false; // timestamp wrap or reset race; skip this sample
        }

        milliseconds = (_results[1] - _results[0]) * (double)_periodNs / 1_000_000.0;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Supported)
        {
            _device.Vk.DestroyQueryPool(_device.Device, _pool, null);
        }
    }
}
