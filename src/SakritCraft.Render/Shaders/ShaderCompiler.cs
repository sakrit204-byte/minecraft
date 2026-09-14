using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

namespace SakritCraft.Render.Shaders;

/// <summary>Outcome of compiling one GLSL file. On failure <see cref="Spirv"/> is empty and <see cref="Log"/> explains why.</summary>
public sealed class ShaderCompileResult
{
    public required bool Success { get; init; }
    public required string SourcePath { get; init; }
    public required ShaderStageFlags Stage { get; init; }
    public required byte[] Spirv { get; init; }
    /// <summary>Compiler diagnostics (errors and warnings), already formatted with file:line.</summary>
    public required string Log { get; init; }
    public required int WarningCount { get; init; }
    /// <summary>Absolute paths of the source and every file it included, for hot-reload dependency tracking.</summary>
    public required string[] Dependencies { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>A successful result carrying no code, for pipeline stages that are deliberately absent.</summary>
    public static ShaderCompileResult Empty { get; } = new()
    {
        Success = true,
        SourcePath = string.Empty,
        Stage = ShaderStageFlags.All,
        Spirv = Array.Empty<byte>(),
        Log = string.Empty,
        WarningCount = 0,
        Dependencies = Array.Empty<string>(),
        Duration = TimeSpan.Zero,
    };
}

/// <summary>
/// GLSL to SPIR-V through the Shaderc native library that ships in the Silk.NET.Shaderc package. There
/// is deliberately no dependency on a Vulkan SDK being installed: the reference machine has none, and
/// the same compiler serves start-up compilation and hot reload so the two paths cannot diverge.
///
/// <c>#include</c> is supported through Shaderc's include callbacks: quoted includes resolve relative to
/// the including file, angle-bracket includes resolve against <c>&lt;root&gt;/include</c>. Every file
/// touched is recorded so the pipeline cache can rebuild the right pipelines when a header changes.
/// Compiles are serialised with a lock; Shaderc compiler objects are cheap and this class is not on
/// the frame path.
/// </summary>
public sealed unsafe class ShaderCompiler : IDisposable
{
    private const string Tag = "shader";

    private sealed class IncludeContext
    {
        public required string Root;
        public readonly List<string> Dependencies = new(4);
        public readonly List<string> Errors = new(1);
    }

    private readonly Shaderc _api;
    private readonly Compiler* _compiler;
    private readonly object _gate = new();
    private readonly bool _optimize;
    private bool _disposed;

    /// <summary>Absolute path of the shader source root.</summary>
    public string Root { get; }

    public ShaderCompiler(string shaderRoot, bool optimize = true)
    {
        Root = Path.GetFullPath(shaderRoot);
        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException($"Shader directory not found: {Root}");
        }

        _optimize = optimize;
        _api = Shaderc.GetApi();
        _compiler = _api.CompilerInitialize();
        if (_compiler == null)
        {
            throw new InvalidOperationException("shaderc_compiler_initialize returned null; is shaderc_shared.dll present next to the executable?");
        }

        uint version = 0, revision = 0;
        _api.GetSpvVersion(ref version, ref revision);
        RenderLog.Info(Tag, $"Shaderc ready (SPIR-V {version >> 16}.{(version >> 8) & 0xFF}, rev {revision}); root {Root}");
    }

    /// <summary>Maps a GLSL file extension to a pipeline stage. Throws for unknown extensions so a typo is loud.</summary>
    public static ShaderStageFlags StageFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".vert" => ShaderStageFlags.VertexBit,
        ".frag" => ShaderStageFlags.FragmentBit,
        ".comp" => ShaderStageFlags.ComputeBit,
        ".geom" => ShaderStageFlags.GeometryBit,
        ".tesc" => ShaderStageFlags.TessellationControlBit,
        ".tese" => ShaderStageFlags.TessellationEvaluationBit,
        ".mesh" => ShaderStageFlags.MeshBitExt,
        ".task" => ShaderStageFlags.TaskBitExt,
        ".rgen" => ShaderStageFlags.RaygenBitKhr,
        ".rchit" => ShaderStageFlags.ClosestHitBitKhr,
        ".rahit" => ShaderStageFlags.AnyHitBitKhr,
        ".rmiss" => ShaderStageFlags.MissBitKhr,
        ".rint" => ShaderStageFlags.IntersectionBitKhr,
        ".rcall" => ShaderStageFlags.CallableBitKhr,
        var ext => throw new ArgumentException($"Unknown shader extension '{ext}' for {path}"),
    };

    /// <summary>True for files the compiler treats as includable headers rather than entry points.</summary>
    public static bool IsHeader(string path) => Path.GetExtension(path).ToLowerInvariant() is ".glsl" or ".h" or ".glslh";

    /// <summary>Resolves a path relative to <see cref="Root"/> (absolute paths pass through) and normalises it.</summary>
    public string Resolve(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));

    /// <summary>Compiles <paramref name="path"/> (relative to the root or absolute). Never throws for shader errors; check <see cref="ShaderCompileResult.Success"/>.</summary>
    public ShaderCompileResult Compile(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string fullPath = Resolve(path);
        var stage = StageFromPath(fullPath);
        var sw = Stopwatch.StartNew();

        byte[] source;
        try
        {
            source = ReadAllBytesRetrying(fullPath);
        }
        catch (IOException ex)
        {
            return Failure(fullPath, stage, $"{fullPath}: cannot read source: {ex.Message}", new[] { fullPath }, sw.Elapsed);
        }

        var context = new IncludeContext { Root = Root };
        context.Dependencies.Add(fullPath);
        var contextHandle = GCHandle.Alloc(context);

        lock (_gate)
        {
            CompileOptions* options = _api.CompileOptionsInitialize();
            CompilationResult* result = null;
            try
            {
                _api.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
                _api.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
                _api.CompileOptionsSetTargetSpirv(options, SpirvVersion.Shaderc16);
                _api.CompileOptionsSetOptimizationLevel(options, _optimize ? OptimizationLevel.Performance : OptimizationLevel.Zero);
                // Debug info costs nothing at runtime and makes RenderDoc/Nsight show real names.
                _api.CompileOptionsSetGenerateDebugInfo(options);
                _api.CompileOptionsSetIncludeCallbacks(
                    options,
                    new PfnIncludeResolveFn(&ResolveInclude),
                    new PfnIncludeResultReleaseFn(&ReleaseInclude),
                    (void*)GCHandle.ToIntPtr(contextHandle));
                AddMacro(options, "SAKRIT_VULKAN", "1");
                AddMacro(options, StageMacro(stage), "1");

                fixed (byte* src = source)
                {
                    result = _api.CompileIntoSpv(_compiler, src, (nuint)source.Length, ShaderKind(stage), fullPath, "main", options);
                }

                var status = _api.ResultGetCompilationStatus(result);
                string log = _api.ResultGetErrorMessageS(result) ?? string.Empty;
                int warnings = (int)_api.ResultGetNumWarnings(result);
                if (context.Errors.Count > 0)
                {
                    log = string.Join(Environment.NewLine, context.Errors) + Environment.NewLine + log;
                }

                if (status != CompilationStatus.Success)
                {
                    return Failure(fullPath, stage, log.TrimEnd(), context.Dependencies.ToArray(), sw.Elapsed, status);
                }

                nuint length = _api.ResultGetLength(result);
                byte* bytes = _api.ResultGetBytes(result);
                var spirv = new byte[(int)length];
                new ReadOnlySpan<byte>(bytes, (int)length).CopyTo(spirv);

                return new ShaderCompileResult
                {
                    Success = true,
                    SourcePath = fullPath,
                    Stage = stage,
                    Spirv = spirv,
                    Log = log.TrimEnd(),
                    WarningCount = warnings,
                    Dependencies = context.Dependencies.ToArray(),
                    Duration = sw.Elapsed,
                };
            }
            finally
            {
                if (result != null) _api.ResultRelease(result);
                _api.CompileOptionsRelease(options);
                contextHandle.Free();
            }
        }
    }

    private static ShaderCompileResult Failure(string path, ShaderStageFlags stage, string log, string[] deps, TimeSpan elapsed, CompilationStatus status = CompilationStatus.CompilationError)
        => new()
        {
            Success = false,
            SourcePath = path,
            Stage = stage,
            Spirv = Array.Empty<byte>(),
            Log = log.Length > 0 ? log : $"{path}: {status}",
            WarningCount = 0,
            Dependencies = deps,
            Duration = elapsed,
        };

    private void AddMacro(CompileOptions* options, string name, string value)
    {
        byte* n = (byte*)SilkMarshal.StringToPtr(name);
        byte* v = (byte*)SilkMarshal.StringToPtr(value);
        try
        {
            _api.CompileOptionsAddMacroDefinition(options, n, (nuint)Encoding.UTF8.GetByteCount(name), v, (nuint)Encoding.UTF8.GetByteCount(value));
        }
        finally
        {
            SilkMarshal.Free((nint)n);
            SilkMarshal.Free((nint)v);
        }
    }

    private static ShaderKind ShaderKind(ShaderStageFlags stage) => stage switch
    {
        ShaderStageFlags.VertexBit => Silk.NET.Shaderc.ShaderKind.VertexShader,
        ShaderStageFlags.FragmentBit => Silk.NET.Shaderc.ShaderKind.FragmentShader,
        ShaderStageFlags.ComputeBit => Silk.NET.Shaderc.ShaderKind.ComputeShader,
        ShaderStageFlags.GeometryBit => Silk.NET.Shaderc.ShaderKind.GeometryShader,
        ShaderStageFlags.TessellationControlBit => Silk.NET.Shaderc.ShaderKind.TessControlShader,
        ShaderStageFlags.TessellationEvaluationBit => Silk.NET.Shaderc.ShaderKind.TessEvaluationShader,
        ShaderStageFlags.MeshBitExt => Silk.NET.Shaderc.ShaderKind.MeshShader,
        ShaderStageFlags.TaskBitExt => Silk.NET.Shaderc.ShaderKind.TaskShader,
        ShaderStageFlags.RaygenBitKhr => Silk.NET.Shaderc.ShaderKind.RaygenShader,
        ShaderStageFlags.ClosestHitBitKhr => Silk.NET.Shaderc.ShaderKind.ClosesthitShader,
        ShaderStageFlags.AnyHitBitKhr => Silk.NET.Shaderc.ShaderKind.AnyhitShader,
        ShaderStageFlags.MissBitKhr => Silk.NET.Shaderc.ShaderKind.MissShader,
        ShaderStageFlags.IntersectionBitKhr => Silk.NET.Shaderc.ShaderKind.IntersectionShader,
        ShaderStageFlags.CallableBitKhr => Silk.NET.Shaderc.ShaderKind.CallableShader,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static string StageMacro(ShaderStageFlags stage) => stage switch
    {
        ShaderStageFlags.VertexBit => "SAKRIT_STAGE_VERTEX",
        ShaderStageFlags.FragmentBit => "SAKRIT_STAGE_FRAGMENT",
        ShaderStageFlags.ComputeBit => "SAKRIT_STAGE_COMPUTE",
        ShaderStageFlags.MeshBitExt => "SAKRIT_STAGE_MESH",
        ShaderStageFlags.TaskBitExt => "SAKRIT_STAGE_TASK",
        _ => "SAKRIT_STAGE_OTHER",
    };

    /// <summary>
    /// Editors save in several steps (truncate, write, rename) and a watcher can fire while the file
    /// is locked or half-written. A few short retries make hot reload robust to that.
    /// </summary>
    private static byte[] ReadAllBytesRetrying(string path)
    {
        IOException? last = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(15);
            }
        }

        throw last!;
    }

    // ---- Include callbacks -------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IncludeResult* ResolveInclude(void* userData, byte* requestedSource, int type, byte* requestingSource, nuint depth)
    {
        var result = (IncludeResult*)NativeMemory.AllocZeroed((nuint)sizeof(IncludeResult));
        try
        {
            var context = (IncludeContext)GCHandle.FromIntPtr((nint)userData).Target!;
            string requested = SilkMarshal.PtrToString((nint)requestedSource) ?? "";
            string requester = SilkMarshal.PtrToString((nint)requestingSource) ?? "";

            string? resolved = null;
            if (depth > 32)
            {
                context.Errors.Add($"{requester}: include depth exceeds 32 (cycle?) while including '{requested}'");
            }
            else if (type == (int)IncludeType.Relative)
            {
                string dir = Path.GetDirectoryName(requester) ?? context.Root;
                string candidate = Path.GetFullPath(Path.Combine(dir, requested));
                if (File.Exists(candidate)) resolved = candidate;
            }

            if (resolved is null && depth <= 32)
            {
                // Standard (<...>) includes and unresolved relative ones fall back to <root>/include then <root>.
                string a = Path.GetFullPath(Path.Combine(context.Root, "include", requested));
                string b = Path.GetFullPath(Path.Combine(context.Root, requested));
                if (File.Exists(a)) resolved = a;
                else if (File.Exists(b)) resolved = b;
            }

            if (resolved is null)
            {
                string message = $"cannot resolve #include \"{requested}\" from {requester}";
                context.Errors.Add(message);
                // Shaderc convention: empty source name + content = error message.
                result->SourceName = AllocUtf8("", out nuint nameLen);
                result->SourceNameLength = nameLen;
                result->Content = AllocUtf8(message, out nuint contentLen);
                result->ContentLength = contentLen;
                return result;
            }

            if (!context.Dependencies.Contains(resolved))
            {
                context.Dependencies.Add(resolved);
            }

            byte[] bytes = ReadAllBytesRetrying(resolved);
            result->SourceName = AllocUtf8(resolved, out nuint n);
            result->SourceNameLength = n;
            byte* content = (byte*)NativeMemory.Alloc((nuint)Math.Max(bytes.Length, 1));
            bytes.AsSpan().CopyTo(new Span<byte>(content, bytes.Length));
            result->Content = content;
            result->ContentLength = (nuint)bytes.Length;
            return result;
        }
        catch (Exception ex)
        {
            result->SourceName = AllocUtf8("", out nuint nameLen);
            result->SourceNameLength = nameLen;
            result->Content = AllocUtf8("include resolver threw: " + ex.Message, out nuint contentLen);
            result->ContentLength = contentLen;
            return result;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ReleaseInclude(void* userData, IncludeResult* result)
    {
        if (result == null)
        {
            return;
        }

        if (result->Content != null) NativeMemory.Free(result->Content);
        if (result->SourceName != null) NativeMemory.Free(result->SourceName);
        NativeMemory.Free(result);
    }

    private static byte* AllocUtf8(string s, out nuint length)
    {
        int count = Encoding.UTF8.GetByteCount(s);
        byte* ptr = (byte*)NativeMemory.Alloc((nuint)(count + 1));
        Encoding.UTF8.GetBytes(s, new Span<byte>(ptr, count));
        ptr[count] = 0;
        length = (nuint)count;
        return ptr;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _api.CompilerRelease(_compiler);
        }

        _api.Dispose();
    }
}
