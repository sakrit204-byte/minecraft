using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SakritCraft.Render.Vulkan;

/// <summary>
/// Owns the <see cref="Vk"/> entry points, the <see cref="Instance"/>, the surface extension, and the
/// debug messenger. Instance-level concerns only: everything that needs a physical device lives in
/// <see cref="VulkanDevice"/>. Validation is opportunistic because the reference machine has no SDK:
/// the layer is enabled when the loader finds it and silently skipped otherwise.
/// </summary>
public sealed unsafe class VulkanInstance : IDisposable
{
    private const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";
    private const string Tag = "vk.instance";

    private static int s_validationMessages;
    private static int s_validationErrors;

    private readonly DebugUtilsMessengerEXT _messenger;
    private bool _disposed;

    public Vk Vk { get; }
    public Instance Instance { get; }
    public KhrSurface SurfaceExt { get; }
    /// <summary>Null when VK_EXT_debug_utils is not present; callers must branch.</summary>
    public ExtDebugUtils? DebugUtils { get; }
    public bool ValidationEnabled { get; }
    /// <summary>Loader-reported instance version (may exceed the device's version).</summary>
    public Version32 LoaderVersion { get; }

    /// <summary>Total validation-layer messages of warning severity or worse since start-up.</summary>
    public static int ValidationMessageCount => Volatile.Read(ref s_validationMessages);
    /// <summary>Validation-layer messages of error severity since start-up. Non-zero on exit is a bug.</summary>
    public static int ValidationErrorCount => Volatile.Read(ref s_validationErrors);

    public VulkanInstance(IVkSurface surfaceSource, RendererOptions options)
    {
        Vk = Vk.GetApi();

        uint loaderVersion = 0;
        Vk.EnumerateInstanceVersion(ref loaderVersion).Check("vkEnumerateInstanceVersion");
        LoaderVersion = (Version32)loaderVersion;
        if (loaderVersion < Vk.Version13)
        {
            throw new PlatformNotSupportedException(
                $"Vulkan loader reports {LoaderVersion.Major}.{LoaderVersion.Minor}; SakritCraft needs 1.3 or newer.");
        }

        // Layers: opportunistic validation.
        var layers = new List<string>(1);
        if (options.EnableValidationIfAvailable && IsLayerPresent(ValidationLayerName))
        {
            layers.Add(ValidationLayerName);
            ValidationEnabled = true;
            RenderLog.Info(Tag, $"{ValidationLayerName} found: enabling validation.");
        }
        else if (options.EnableValidationIfAvailable)
        {
            RenderLog.Warn(Tag, $"{ValidationLayerName} not found (no Vulkan SDK installed?). Running without validation.");
        }

        // Extensions: whatever the windowing layer needs for a surface, plus debug utils if present.
        var extensions = new List<string>(4);
        byte** required = surfaceSource.GetRequiredExtensions(out uint requiredCount);
        for (uint i = 0; i < requiredCount; i++)
        {
            extensions.Add(SilkMarshal.PtrToString((nint)required[i])!);
        }

        bool debugUtils = Vk.IsInstanceExtensionPresent(ExtDebugUtils.ExtensionName);
        if (debugUtils)
        {
            extensions.Add(ExtDebugUtils.ExtensionName);
        }

        RenderLog.Info(Tag, $"Loader {LoaderVersion.Major}.{LoaderVersion.Minor}.{LoaderVersion.Patch}; instance extensions: {string.Join(", ", extensions)}");

        byte* appName = (byte*)SilkMarshal.StringToPtr(options.ApplicationName);
        byte* engineName = (byte*)SilkMarshal.StringToPtr("SakritCraft.Render");
        byte** extNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);
        byte** layerNames = layers.Count > 0 ? (byte**)SilkMarshal.StringArrayToPtr(layers) : null;
        try
        {
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApplicationVersion = new Version32(0, 1, 0),
                PEngineName = engineName,
                EngineVersion = new Version32(0, 1, 0),
                ApiVersion = Vk.Version13,
            };

            // Chaining the messenger create info onto the instance covers vkCreateInstance /
            // vkDestroyInstance themselves, which the messenger object cannot observe.
            var messengerInfo = BuildMessengerCreateInfo();

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = extNames,
                EnabledLayerCount = (uint)layers.Count,
                PpEnabledLayerNames = layerNames,
                PNext = debugUtils ? &messengerInfo : null,
            };

            Vk.CreateInstance(&createInfo, null, out Instance instance).Check("vkCreateInstance");
            Instance = instance;
        }
        finally
        {
            SilkMarshal.Free((nint)appName);
            SilkMarshal.Free((nint)engineName);
            SilkMarshal.Free((nint)extNames);
            if (layerNames != null)
            {
                SilkMarshal.Free((nint)layerNames);
            }
        }

        if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface surfaceExt))
        {
            throw new PlatformNotSupportedException("VK_KHR_surface is unavailable; cannot present to a window.");
        }

        SurfaceExt = surfaceExt;

        if (debugUtils && Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils du))
        {
            DebugUtils = du;
            var messengerInfo = BuildMessengerCreateInfo();
            du.CreateDebugUtilsMessenger(Instance, &messengerInfo, null, out _messenger).Check("vkCreateDebugUtilsMessengerEXT");
        }
    }

    /// <summary>Creates a presentable surface for the window. Ownership passes to the caller (destroy via <see cref="DestroySurface"/>).</summary>
    public SurfaceKHR CreateSurface(IVkSurface surfaceSource)
        => surfaceSource.Create<AllocationCallbacks>(Instance.ToHandle(), null).ToSurface();

    public void DestroySurface(SurfaceKHR surface) => SurfaceExt.DestroySurface(Instance, surface, null);

    private bool IsLayerPresent(string name)
    {
        uint count = 0;
        Vk.EnumerateInstanceLayerProperties(&count, null).Check("vkEnumerateInstanceLayerProperties");
        if (count == 0)
        {
            return false;
        }

        var props = new LayerProperties[count];
        fixed (LayerProperties* p = props)
        {
            Vk.EnumerateInstanceLayerProperties(&count, p).Check("vkEnumerateInstanceLayerProperties");
            for (uint i = 0; i < count; i++)
            {
                if (SilkMarshal.PtrToString((nint)p[i].LayerName) == name)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static DebugUtilsMessengerCreateInfoEXT BuildMessengerCreateInfo()
    {
        var severity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
        if (RenderLog.MinLevel <= RenderLogLevel.Trace)
        {
            severity |= DebugUtilsMessageSeverityFlagsEXT.InfoBitExt | DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt;
        }

        return new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = severity,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                        | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                        | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(&DebugCallback),
        };
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static Bool32 DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        try
        {
            string message = SilkMarshal.PtrToString((nint)data->PMessage) ?? string.Empty;
            string id = data->PMessageIdName != null ? SilkMarshal.PtrToString((nint)data->PMessageIdName) ?? "" : "";
            string text = id.Length > 0 ? $"[{id}] {message}" : message;

            if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
            {
                Interlocked.Increment(ref s_validationErrors);
                Interlocked.Increment(ref s_validationMessages);
                RenderLog.Error("vk.validation", text);
            }
            else if ((severity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0)
            {
                Interlocked.Increment(ref s_validationMessages);
                RenderLog.Warn("vk.validation", text);
            }
            else
            {
                RenderLog.Trace("vk.validation", text);
            }
        }
        catch
        {
            // Never let an exception cross the native boundary.
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (DebugUtils is not null && _messenger.Handle != 0)
        {
            DebugUtils.DestroyDebugUtilsMessenger(Instance, _messenger, null);
            DebugUtils.Dispose();
        }

        SurfaceExt.Dispose();
        Vk.DestroyInstance(Instance, null);
        Vk.Dispose();
    }
}
