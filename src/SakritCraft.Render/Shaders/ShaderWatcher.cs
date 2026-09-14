using System.Diagnostics;

namespace SakritCraft.Render.Shaders;

/// <summary>
/// Watches the shader root for saves and hands the render thread a debounced list of changed files.
/// FileSystemWatcher raises several events per save (truncate, write, attribute change, rename from a
/// temp file) and fires them on a thread-pool thread; this class coalesces them per path and only
/// releases a path once it has been quiet for <see cref="DebounceMilliseconds"/>, so a single save
/// yields a single recompile and never observes a half-written file.
///
/// <see cref="TryDrain"/> is the only method the render thread calls. It takes a fast path with no lock
/// when nothing is pending, so the steady-state cost is one volatile read per frame.
/// </summary>
public sealed class ShaderWatcher : IDisposable
{
    private const string Tag = "shader.watch";
    private const int DebounceMilliseconds = 60;

    private readonly FileSystemWatcher _watcher;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ready = new(4);
    private int _pendingCount;
    private bool _disposed;

    public string Root { get; }

    public ShaderWatcher(string root)
    {
        Root = Path.GetFullPath(root);
        _watcher = new FileSystemWatcher(Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += (_, e) => RenderLog.Warn(Tag, $"Watcher error: {e.GetException().Message}");
        _watcher.EnableRaisingEvents = true;
        RenderLog.Info(Tag, $"Hot reload armed on {Root}");
    }

    /// <summary>
    /// Appends every path whose last change is older than the debounce window to <paramref name="changed"/>.
    /// Returns true if anything was appended. Allocation-free when idle.
    /// </summary>
    public bool TryDrain(List<string> changed)
    {
        if (Volatile.Read(ref _pendingCount) == 0)
        {
            return false;
        }

        long now = Stopwatch.GetTimestamp();
        long threshold = Stopwatch.Frequency * DebounceMilliseconds / 1000;
        lock (_gate)
        {
            _ready.Clear();
            foreach (var kv in _pending)
            {
                if (now - kv.Value >= threshold)
                {
                    _ready.Add(kv.Key);
                }
            }

            foreach (var path in _ready)
            {
                _pending.Remove(path);
                changed.Add(path);
            }

            Volatile.Write(ref _pendingCount, _pending.Count);
            return _ready.Count > 0;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Touch(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Editors that write to a temp file and rename over the target report the final name here.
        Touch(e.FullPath);
    }

    private void Touch(string fullPath)
    {
        if (Directory.Exists(fullPath))
        {
            return;
        }

        string ext = Path.GetExtension(fullPath);
        if (ext.Length == 0 || ext.EndsWith('~') || ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || ext.Equals(".swp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_gate)
        {
            _pending[Path.GetFullPath(fullPath)] = Stopwatch.GetTimestamp();
            Volatile.Write(ref _pendingCount, _pending.Count);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}
