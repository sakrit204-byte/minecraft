using System.Diagnostics;

namespace SakritCraft.Render.Frame;

/// <summary>
/// Frame-time statistics with a periodic text report. Per-frame work is a handful of arithmetic ops
/// on fields; the only allocation is the report string, produced a few times a second by
/// <see cref="TryReport"/>. Exponential moving averages smooth the display while the worst frame in
/// the window is kept so a hitch is visible even when the average looks fine.
/// </summary>
public sealed class FrameTiming
{
    private readonly double _reportIntervalSeconds;
    private readonly double _tickToMs = 1000.0 / Stopwatch.Frequency;

    private long _frameStartTick;
    private long _lastFrameStartTick;
    private long _lastReportTick;
    private int _framesSinceReport;
    private double _cpuFrameMsAvg;
    private double _cpuWorkMsAvg;
    private double _gpuMsAvg;
    private double _cpuFrameMsMax;
    private double _gpuMsMax;
    private bool _hasGpu;
    private long _allocAtFrameStart;
    private long _allocBytesSinceReport;
    private long _allocBytesMaxFrame;

    /// <summary>Wall-clock time between successive frame starts, smoothed. Includes pacing sleeps.</summary>
    public double CpuFrameMs => _cpuFrameMsAvg;
    /// <summary>CPU time spent inside the render call (record + submit + present), smoothed.</summary>
    public double CpuWorkMs => _cpuWorkMsAvg;
    /// <summary>GPU time from first to last command of a frame, smoothed. NaN when timestamps are unsupported.</summary>
    public double GpuMs => _hasGpu ? _gpuMsAvg : double.NaN;
    /// <summary>Frames presented over the last report interval divided by that interval.</summary>
    public double Fps { get; private set; }
    /// <summary>Total frames since start-up.</summary>
    public long FrameCount { get; private set; }
    /// <summary>Average managed bytes allocated per frame by the render thread over the last report interval. Target: 0.</summary>
    public long AllocatedBytesPerFrame { get; private set; }

    public FrameTiming(double reportIntervalSeconds)
    {
        _reportIntervalSeconds = reportIntervalSeconds;
        long now = Stopwatch.GetTimestamp();
        _lastFrameStartTick = now;
        _lastReportTick = now;
    }

    public void BeginFrame()
    {
        _allocAtFrameStart = GC.GetAllocatedBytesForCurrentThread();
        _frameStartTick = Stopwatch.GetTimestamp();
        double frameMs = (_frameStartTick - _lastFrameStartTick) * _tickToMs;
        _lastFrameStartTick = _frameStartTick;
        if (FrameCount > 0)
        {
            _cpuFrameMsAvg = Ema(_cpuFrameMsAvg, frameMs);
            if (frameMs > _cpuFrameMsMax) _cpuFrameMsMax = frameMs;
        }

        FrameCount++;
        _framesSinceReport++;
    }

    /// <summary>Call after present, before any pacing sleep.</summary>
    public void EndCpuWork()
    {
        double workMs = (Stopwatch.GetTimestamp() - _frameStartTick) * _tickToMs;
        _cpuWorkMsAvg = Ema(_cpuWorkMsAvg, workMs);

        // Managed bytes allocated by the render thread between BeginFrame and here. The steady-state
        // target is zero; anything else is a GC pause waiting to happen (docs section 02, "Memory discipline").
        long allocated = GC.GetAllocatedBytesForCurrentThread() - _allocAtFrameStart;
        _allocBytesSinceReport += allocated;
        if (allocated > _allocBytesMaxFrame) _allocBytesMaxFrame = allocated;
    }

    public void RecordGpuTime(double milliseconds)
    {
        _hasGpu = true;
        _gpuMsAvg = Ema(_gpuMsAvg, milliseconds);
        if (milliseconds > _gpuMsMax) _gpuMsMax = milliseconds;
    }

    /// <summary>
    /// Produces a one-line report once per interval and resets the interval's max/FPS counters. Returns
    /// false (and allocates nothing) on every other call.
    /// </summary>
    public bool TryReport(out string line)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _lastReportTick) / (double)Stopwatch.Frequency;
        if (elapsed < _reportIntervalSeconds)
        {
            line = string.Empty;
            return false;
        }

        Fps = _framesSinceReport / elapsed;
        long allocPerFrame = _framesSinceReport > 0 ? _allocBytesSinceReport / _framesSinceReport : 0;
        AllocatedBytesPerFrame = allocPerFrame;
        string gpu = _hasGpu ? $"gpu {_gpuMsAvg,5:F3} ms (max {_gpuMsMax,5:F3})" : "gpu n/a";
        line = $"{Fps,6:F1} fps | cpu {_cpuFrameMsAvg,5:F2} ms (work {_cpuWorkMsAvg,5:F2}, max {_cpuFrameMsMax,5:F2}) | {gpu} | alloc {allocPerFrame} B/frame (max {_allocBytesMaxFrame})";

        _lastReportTick = now;
        _framesSinceReport = 0;
        _cpuFrameMsMax = 0;
        _gpuMsMax = 0;
        _allocBytesSinceReport = 0;
        _allocBytesMaxFrame = 0;
        return true;
    }

    private static double Ema(double current, double sample) => current == 0 ? sample : current + (sample - current) * 0.1;
}
