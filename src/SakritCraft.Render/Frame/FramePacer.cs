using System.Diagnostics;

namespace SakritCraft.Render.Frame;

/// <summary>
/// CPU-side frame limiter for present modes that do not block (mailbox, immediate). Sleeps in coarse
/// steps while more than ~1.5 ms remain, then spins on the high-resolution clock for the remainder, so
/// the cadence is stable to well under a millisecond without burning a whole core. Targets are
/// scheduled from the previous target rather than from "now" so jitter does not accumulate; a frame
/// that overruns resets the schedule instead of trying to catch up with a burst.
/// </summary>
public sealed class FramePacer
{
    private readonly long _periodTicks;
    private long _nextTargetTick;

    /// <summary>Target frames per second; 0 disables pacing.</summary>
    public double TargetFps { get; }
    public bool Enabled => _periodTicks > 0;

    public FramePacer(double targetFps)
    {
        TargetFps = targetFps;
        _periodTicks = targetFps > 0 ? (long)(Stopwatch.Frequency / targetFps) : 0;
        _nextTargetTick = Stopwatch.GetTimestamp() + _periodTicks;
    }

    /// <summary>Blocks until the next frame slot. Returns immediately when disabled or already late.</summary>
    public void WaitForNextFrame()
    {
        if (!Enabled)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long remaining = _nextTargetTick - now;
        if (remaining <= 0)
        {
            // Overran: re-anchor so we do not spin through several targets at once.
            _nextTargetTick = now + _periodTicks;
            return;
        }

        long sleepThreshold = Stopwatch.Frequency * 3 / 2000; // 1.5 ms
        while (remaining > sleepThreshold)
        {
            Thread.Sleep(1);
            now = Stopwatch.GetTimestamp();
            remaining = _nextTargetTick - now;
        }

        while (Stopwatch.GetTimestamp() < _nextTargetTick)
        {
            Thread.SpinWait(64);
        }

        _nextTargetTick += _periodTicks;
    }
}
