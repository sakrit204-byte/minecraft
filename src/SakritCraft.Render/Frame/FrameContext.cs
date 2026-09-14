using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace SakritCraft.Render.Frame;

/// <summary>
/// Everything that belongs to one frame slot: a command pool that is reset wholesale each time the
/// slot comes around (cheaper than resetting individual command buffers and impossible to leak), the
/// primary command buffer, the binary semaphore the swapchain acquire signals, and the timeline value
/// this slot's last submission signals. Reusing the slot means first waiting for that value; that is
/// the only CPU-GPU synchronisation in the steady-state loop.
/// </summary>
public sealed unsafe class FrameContext : IDisposable
{
    private readonly VulkanDevice _device;
    private bool _disposed;

    public int Index { get; }
    public CommandPool Pool { get; }
    public CommandBuffer Cmd { get; }
    /// <summary>Signalled by vkAcquireNextImageKHR; waited on by the frame's submission at COLOR_ATTACHMENT_OUTPUT.</summary>
    public Semaphore ImageAcquired { get; }
    /// <summary>Timeline value this slot's most recent submission signals. 0 = never submitted.</summary>
    public ulong SubmittedTimelineValue { get; set; }
    /// <summary>Swapchain image index this slot rendered to most recently.</summary>
    public uint ImageIndex { get; set; }

    public FrameContext(VulkanDevice device, int index)
    {
        _device = device;
        Index = index;
        var vk = device.Vk;

        // TRANSIENT: the driver may use a cheaper allocation strategy for short-lived command buffers.
        // Deliberately no RESET_COMMAND_BUFFER_BIT: the whole pool is reset at Begin().
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = device.GraphicsFamily,
        };
        vk.CreateCommandPool(device.Device, &poolInfo, null, out CommandPool pool).Check("vkCreateCommandPool");
        Pool = pool;

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        vk.AllocateCommandBuffers(device.Device, &allocInfo, out CommandBuffer cmd).Check("vkAllocateCommandBuffers");
        Cmd = cmd;

        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        vk.CreateSemaphore(device.Device, &semInfo, null, out Semaphore acquired).Check("vkCreateSemaphore");
        ImageAcquired = acquired;

        device.SetObjectName(ObjectType.CommandPool, pool.Handle, $"Frame[{index}].CommandPool");
        device.SetObjectName(ObjectType.CommandBuffer, (ulong)cmd.Handle, $"Frame[{index}].Cmd");
        device.SetObjectName(ObjectType.Semaphore, acquired.Handle, $"Frame[{index}].ImageAcquired");
    }

    /// <summary>Resets the pool and opens the command buffer for one-time recording. Caller must have waited for <see cref="SubmittedTimelineValue"/>.</summary>
    public CommandBuffer Begin()
    {
        var vk = _device.Vk;
        vk.ResetCommandPool(_device.Device, Pool, CommandPoolResetFlags.None).Check("vkResetCommandPool");
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        vk.BeginCommandBuffer(Cmd, &beginInfo).Check("vkBeginCommandBuffer");
        return Cmd;
    }

    public void End() => _device.Vk.EndCommandBuffer(Cmd).Check("vkEndCommandBuffer");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var vk = _device.Vk;
        vk.DestroySemaphore(_device.Device, ImageAcquired, null);
        vk.DestroyCommandPool(_device.Device, Pool, null); // frees the command buffer
    }
}
