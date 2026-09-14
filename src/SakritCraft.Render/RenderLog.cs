using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SakritCraft.Render;

/// <summary>Severity of a renderer log line.</summary>
public enum RenderLogLevel
{
    Trace,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Minimal thread-safe console logger for the renderer. It lives in Render rather than Core so this
/// project has no dependency on a logging API that another team is still designing; when Core gains
/// a logger, <see cref="Sink"/> is the single seam to redirect through. Nothing in the steady-state
/// frame loop logs, so string formatting cost here is acceptable.
/// </summary>
public static class RenderLog
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Minimum level that is written. Trace is off by default.</summary>
    public static RenderLogLevel MinLevel { get; set; } = RenderLogLevel.Info;

    /// <summary>Replaceable output sink. Receives (level, category, message). Defaults to the console.</summary>
    public static Action<RenderLogLevel, string, string> Sink { get; set; } = ConsoleSink;

    public static void Trace(string category, string message) => Write(RenderLogLevel.Trace, category, message);
    public static void Info(string category, string message) => Write(RenderLogLevel.Info, category, message);
    public static void Warn(string category, string message) => Write(RenderLogLevel.Warn, category, message);
    public static void Error(string category, string message) => Write(RenderLogLevel.Error, category, message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write(RenderLogLevel level, string category, string message)
    {
        if (level < MinLevel)
        {
            return;
        }

        Sink(level, category, message);
    }

    private static void ConsoleSink(RenderLogLevel level, string category, string message)
    {
        lock (Gate)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                RenderLogLevel.Error => ConsoleColor.Red,
                RenderLogLevel.Warn => ConsoleColor.Yellow,
                RenderLogLevel.Trace => ConsoleColor.DarkGray,
                _ => ConsoleColor.Gray,
            };
            var t = Clock.Elapsed.TotalSeconds;
            Console.WriteLine($"[{t,8:F3}] [{LevelTag(level)}] [{category}] {message}");
            Console.ForegroundColor = previous;
        }
    }

    private static string LevelTag(RenderLogLevel level) => level switch
    {
        RenderLogLevel.Trace => "TRC",
        RenderLogLevel.Info => "INF",
        RenderLogLevel.Warn => "WRN",
        RenderLogLevel.Error => "ERR",
        _ => "???",
    };
}
