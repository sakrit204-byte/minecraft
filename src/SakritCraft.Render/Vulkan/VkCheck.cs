using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace SakritCraft.Render.Vulkan;

/// <summary>
/// Thrown when a Vulkan call returns an error code. Carries the <see cref="Result"/> and the
/// call site so a log line is enough to find the failing call without a debugger attached.
/// </summary>
public sealed class VulkanException : Exception
{
    public Result Result { get; }
    public string Call { get; }

    public VulkanException(Result result, string call, string file, int line)
        : base($"{call} failed with {result} ({(int)result}) at {Path.GetFileName(file)}:{line}")
    {
        Result = result;
        Call = call;
    }
}

/// <summary>
/// Result checking for every Vulkan call. Vulkan error codes are negative; success codes
/// (<see cref="Result.Success"/>, <see cref="Result.SuboptimalKhr"/>, <see cref="Result.NotReady"/>,
/// <see cref="Result.Timeout"/>) are zero or positive and are passed through so callers that care
/// about them (swapchain acquire/present, query readback) can branch on the value.
/// </summary>
public static class VkCheck
{
    /// <summary>Throws <see cref="VulkanException"/> if <paramref name="result"/> is an error code; returns it otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Result Check(this Result result, string call,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if ((int)result < 0)
        {
            Throw(result, call, file, line);
        }

        return result;
    }

    [DoesNotReturn]
    private static void Throw(Result result, string call, string file, int line)
        => throw new VulkanException(result, call, file, line);
}
