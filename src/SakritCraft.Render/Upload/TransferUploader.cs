using SakritCraft.Render.Memory;
using SakritCraft.Render.Vulkan;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace SakritCraft.Render.Upload;

/// <summary>A reserved, mapped region of the staging ring. Write into <see cref="Ptr"/>, then hand it to a copy call.</summary>
public readonly unsafe struct StagingSlice
{
    public readonly byte* Ptr;
    public readonly ulong Offset;
    public readonly ulong Size;

    internal StagingSlice(byte* ptr, ulong offset, ulong size)
    {
        Ptr = ptr;
        Offset = offset;
        Size = size;
    }

    public Span<byte> Span => new(Ptr, checked((int)Size));
}

/// <summary>
/// Batched CPU-to-GPU uploads on the dedicated transfer queue with correct queue-family ownership
/// transfer. The design follows docs/MASTER-PLAN.html section 06 "Streaming": every stage of chunk
/// streaming runs on worker threads except the final GPU upload, which is batched once per frame on a
/// transfer queue so it never stalls the graphics queue.
///
/// <para><b>Lifecycle of one batch.</b> <see cref="BeginBatch"/> claims a transfer command buffer,
/// callers reserve staging with <see cref="TryStage"/>, write into it, and record copies with
/// <see cref="CopyToBuffer"/> / <see cref="CopyToImage"/>. <see cref="EndBatch"/> appends one
/// <i>release</i> barrier per destination range (ownership transfer from the transfer family to the
/// graphics family) and submits, signalling the transfer timeline. The matching <i>acquire</i>
/// barriers are recorded into a graphics command buffer by <see cref="RecordAcquires"/>, and that
/// graphics submission must wait on the timeline value it returns. Both halves are required by the
/// spec for EXCLUSIVE resources whose contents must survive the queue change.</para>
///
/// <para><b>Why acquires lag a frame.</b> A batch submitted in frame N is acquired in frame N+1. If the
/// graphics submission waited on a copy submitted microseconds earlier it would idle at the vertex
/// input stage until the DMA finished, which is exactly the stall the design forbids. One frame later
/// the copy is long done and the wait is free. Callers observe completion through
/// <see cref="AcquiredThrough"/>: a resource whose upload ticket is at or below it may be used by any
/// graphics command buffer recorded from that point on.</para>
///
/// <para><b>Re-uploading into a range the graphics queue already owns</b> needs no graphics-side release:
/// ownership transfer is only required when the contents must be preserved, and an upload overwrites
/// the range in full. The caller must still guarantee the graphics queue is no longer reading it
/// (deferred frees keyed to the frame timeline do that).</para>
///
/// <para>When the device has no transfer-only family the same code runs on the graphics queue in its
/// own submission; the ownership barriers degrade to plain memory barriers because the family indices
/// are equal. Nothing else changes.</para>
///
/// Steady state is allocation-free: barrier arrays and pending lists are structs in pooled storage.
/// </summary>
public sealed unsafe class TransferUploader : IDisposable
{
    private const string Tag = "vk.upload";

    private struct StagingRegion
    {
        public ulong Offset;
        public ulong Size;
        public ulong RetireAfter; // transfer timeline value
    }

    private struct PendingBuffer
    {
        public Buffer Buffer;
        public ulong Offset;
        public ulong Size;
        public PipelineStageFlags2 DstStages;
        public AccessFlags2 DstAccess;
    }

    private struct PendingImage
    {
        public Image Image;
        public ImageSubresourceRange Range;
        public ImageLayout FinalLayout;
        public PipelineStageFlags2 DstStages;
        public AccessFlags2 DstAccess;
    }

    private sealed class BatchSlot
    {
        public CommandBuffer Cmd;
        public ulong SubmittedValue;
    }

    private readonly VulkanDevice _device;
    private readonly Queue _queue;
    private readonly uint _srcFamily;
    private readonly uint _dstFamily;
    private readonly bool _ownershipTransfer;
    private readonly CommandPool _pool;
    private readonly BatchSlot[] _slots;
    private readonly GpuBuffer _staging;
    private readonly Queue<StagingRegion> _regions = new(64);

    // Batch under construction (between BeginBatch and EndBatch).
    private int _slotIndex;
    private bool _batchOpen;
    private bool _batchHasWork;
    private readonly List<PendingBuffer> _batchBuffers = new(256);
    private readonly List<PendingImage> _batchImages = new(8);

    // Submitted, awaiting acquire on the graphics queue.
    private readonly List<PendingBuffer> _awaitingBuffers = new(256);
    private readonly List<PendingImage> _awaitingImages = new(8);
    private ulong _awaitingValue;

    private BufferMemoryBarrier2[] _bufferBarriers = new BufferMemoryBarrier2[256];
    private ImageMemoryBarrier2[] _imageBarriers = new ImageMemoryBarrier2[8];

    // Staging ring: live bytes occupy [tail, head) or, when wrapped, [tail, capacity) + [0, head).
    private ulong _head;
    private ulong _tail;
    private ulong _live;

    private ulong _timelineValue;
    private bool _disposed;

    /// <summary>Signalled with the batch value by every submission. Graphics submissions wait on it before consuming uploads.</summary>
    public Semaphore Timeline { get; }
    /// <summary>Highest batch value whose acquire barriers have been recorded on the graphics queue. Resources with a ticket at or below this are drawable.</summary>
    public ulong AcquiredThrough { get; private set; }
    /// <summary>Value the most recent submission signals; 0 before the first.</summary>
    public ulong LastSubmittedValue => _timelineValue;
    public ulong StagingCapacity => _staging.Size;
    public ulong StagingInUse => _live;
    public bool UsesDedicatedQueue => _ownershipTransfer;
    /// <summary>Total bytes copied since start-up, for the statistics line.</summary>
    public ulong BytesUploaded { get; private set; }
    public int BatchesSubmitted { get; private set; }

    public TransferUploader(VulkanDevice device, GpuAllocator allocator, int framesInFlight, ulong stagingBytes)
    {
        _device = device;
        _queue = device.TransferQueue;
        _srcFamily = device.TransferFamily;
        _dstFamily = device.GraphicsFamily;
        _ownershipTransfer = _srcFamily != _dstFamily;
        var vk = device.Vk;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _srcFamily,
        };
        vk.CreateCommandPool(device.Device, &poolInfo, null, out _pool).Check("vkCreateCommandPool(transfer)");
        device.SetObjectName(ObjectType.CommandPool, _pool.Handle, "Upload.CommandPool");

        // One more slot than frames in flight: the transfer queue may lag the graphics queue by a frame.
        _slots = new BatchSlot[framesInFlight + 1];
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)_slots.Length,
        };
        var cmds = stackalloc CommandBuffer[_slots.Length];
        vk.AllocateCommandBuffers(device.Device, &allocInfo, cmds).Check("vkAllocateCommandBuffers(transfer)");
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new BatchSlot { Cmd = cmds[i] };
            device.SetObjectName(ObjectType.CommandBuffer, (ulong)cmds[i].Handle, $"Upload.Cmd[{i}]");
        }

        var timelineType = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };
        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &timelineType };
        vk.CreateSemaphore(device.Device, &semInfo, null, out Semaphore timeline).Check("vkCreateSemaphore(upload timeline)");
        Timeline = timeline;
        device.SetObjectName(ObjectType.Semaphore, timeline.Handle, "Upload.Timeline");

        _staging = new GpuBuffer(device, allocator, stagingBytes, BufferUsageFlags.TransferSrcBit, MemoryUsage.Staging, "Upload.StagingRing");

        RenderLog.Info(Tag, $"Transfer uploader: {stagingBytes >> 20} MiB staging ring, {_slots.Length} batch slots, " +
                            (_ownershipTransfer ? $"family {_srcFamily} -> {_dstFamily} with ownership transfer" : "graphics queue (no dedicated transfer family)"));
    }

    // ---- Batch construction ----------------------------------------------------------------------

    /// <summary>
    /// Opens this frame's batch. Blocks on the host only if the transfer queue is more than
    /// <c>framesInFlight</c> batches behind, which would indicate a genuine bandwidth problem.
    /// Retires staging regions whose copies have completed.
    /// </summary>
    public void BeginBatch()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_batchOpen)
        {
            throw new InvalidOperationException("BeginBatch called while a batch is already open.");
        }

        var vk = _device.Vk;
        _slotIndex = (_slotIndex + 1) % _slots.Length;
        var slot = _slots[_slotIndex];
        if (slot.SubmittedValue != 0)
        {
            WaitForValue(slot.SubmittedValue);
        }

        ulong completed = 0;
        vk.GetSemaphoreCounterValue(_device.Device, Timeline, &completed).Check("vkGetSemaphoreCounterValue(upload)");
        RetireStaging(completed);

        _batchOpen = true;
        _batchHasWork = false;
    }

    /// <summary>
    /// Reserves <paramref name="size"/> bytes of staging, contiguous and aligned. Returns false when the
    /// ring cannot fit it this frame; the caller keeps its data and retries next batch. Never blocks.
    /// </summary>
    public bool TryStage(ulong size, ulong alignment, out StagingSlice slice)
    {
        EnsureBatchOpen();
        ulong capacity = _staging.Size;
        if (size == 0 || size > capacity)
        {
            slice = default;
            return false;
        }

        if (_live == 0)
        {
            _head = 0;
            _tail = 0;
        }

        // Live bytes occupy [tail, head) normally, or [tail, capacity) + [0, head) once wrapped. A ring
        // with head == tail is empty when live == 0 and completely full otherwise.
        bool wrapped = _head < _tail || (_head == _tail && _live > 0);
        ulong offset;
        ulong regionStart;
        if (!wrapped)
        {
            ulong aligned = RangeAllocator.AlignUp(_head, alignment);
            if (aligned + size <= capacity)
            {
                offset = aligned;
                regionStart = _head;
            }
            else if (size <= _tail)
            {
                // Wrap: the bytes from head to the end are wasted until the wrap retires. Account for
                // them as a region so tail/live stay consistent.
                ulong waste = capacity - _head;
                if (waste > 0)
                {
                    _regions.Enqueue(new StagingRegion { Offset = _head, Size = waste, RetireAfter = _timelineValue + 1 });
                    _live += waste;
                }

                offset = 0;
                regionStart = 0;
            }
            else
            {
                slice = default;
                return false;
            }
        }
        else
        {
            ulong aligned = RangeAllocator.AlignUp(_head, alignment);
            if (aligned + size <= _tail)
            {
                offset = aligned;
                regionStart = _head;
            }
            else
            {
                slice = default;
                return false;
            }
        }

        // Alignment padding is folded into the region so the ring accounting stays exact.
        ulong regionSize = offset + size - regionStart;
        _regions.Enqueue(new StagingRegion { Offset = regionStart, Size = regionSize, RetireAfter = _timelineValue + 1 });
        _live += regionSize;
        _head = offset + size;

        slice = new StagingSlice(_staging.MappedPtr + offset, offset, size);
        return true;
    }

    /// <summary>
    /// Records a copy of <paramref name="src"/> into <paramref name="dst"/> and schedules the range's
    /// release to the graphics family. <paramref name="dstStages"/>/<paramref name="dstAccess"/> describe
    /// how graphics will read it (they land in the acquire barrier). Returns the ticket the upload
    /// completes under; compare it with <see cref="AcquiredThrough"/>.
    /// </summary>
    public ulong CopyToBuffer(in StagingSlice src, Buffer dst, ulong dstOffset, PipelineStageFlags2 dstStages, AccessFlags2 dstAccess)
    {
        EnsureBatchOpen();
        var cmd = EnsureRecording();
        var region = new BufferCopy(src.Offset, dstOffset, src.Size);
        _device.Vk.CmdCopyBuffer(cmd, _staging.Handle, dst, 1, &region);
        _batchBuffers.Add(new PendingBuffer { Buffer = dst, Offset = dstOffset, Size = src.Size, DstStages = dstStages, DstAccess = dstAccess });
        BytesUploaded += src.Size;
        return _timelineValue + 1;
    }

    /// <summary>
    /// Uploads tightly packed texels for one mip level of <paramref name="dst"/>, transitioning the
    /// whole image from UNDEFINED to TRANSFER_DST first and to <paramref name="finalLayout"/> as part of
    /// the ownership release. The image must not have been used before (UNDEFINED discards content).
    /// </summary>
    /// <summary>
    /// Fills an entire image, every mip level and every array layer, from one staged
    /// allocation.
    /// <para>
    /// This exists because <see cref="CopyToImage"/> transitions the image's full range
    /// out of <see cref="ImageLayout.Undefined"/> on every call, and that layout
    /// discards contents. Calling it once per mip level therefore throws away each
    /// level as the next one starts, leaving only the last. A mipped image has to be
    /// uploaded under a single barrier, so the regions are batched here instead.
    /// </para>
    /// <para>
    /// Each region's <c>BufferOffset</c> is relative to the start of the staging slice;
    /// the slice's own offset is added here so callers can lay their data out from zero.
    /// </para>
    /// </summary>
    public ulong CopyToImageRegions(in StagingSlice src, GpuImage dst, ReadOnlySpan<BufferImageCopy> regions,
                                    ImageLayout finalLayout, PipelineStageFlags2 dstStages, AccessFlags2 dstAccess)
    {
        EnsureBatchOpen();
        if (regions.Length == 0)
        {
            throw new ArgumentException("At least one copy region is required.", nameof(regions));
        }

        var cmd = EnsureRecording();
        var vk = _device.Vk;

        var toTransfer = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.None,
            SrcAccessMask = AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.TransferWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = dst.Handle,
            SubresourceRange = dst.FullRange,
        };
        var dep = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &toTransfer,
        };
        vk.CmdPipelineBarrier2(cmd, &dep);

        var shifted = new BufferImageCopy[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            shifted[i] = regions[i];
            shifted[i].BufferOffset += src.Offset;
        }

        fixed (BufferImageCopy* p = shifted)
        {
            vk.CmdCopyBufferToImage(cmd, _staging.Handle, dst.Handle,
                ImageLayout.TransferDstOptimal, (uint)shifted.Length, p);
        }

        _batchImages.Add(new PendingImage
        {
            Image = dst.Handle,
            Range = dst.FullRange,
            FinalLayout = finalLayout,
            DstStages = dstStages,
            DstAccess = dstAccess,
        });
        BytesUploaded += src.Size;
        return _timelineValue + 1;
    }

    public ulong CopyToImage(in StagingSlice src, GpuImage dst, uint mipLevel, ImageLayout finalLayout, PipelineStageFlags2 dstStages, AccessFlags2 dstAccess)
    {
        EnsureBatchOpen();
        var cmd = EnsureRecording();
        var vk = _device.Vk;

        // UNDEFINED -> TRANSFER_DST on the transfer queue. No prior work touches the image, so the source
        // scope is empty.
        var toTransfer = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.None,
            SrcAccessMask = AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.TransferWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = dst.Handle,
            SubresourceRange = dst.FullRange,
        };
        var dep = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &toTransfer };
        vk.CmdPipelineBarrier2(cmd, &dep);

        uint w = Math.Max(1u, dst.Extent.Width >> (int)mipLevel);
        uint h = Math.Max(1u, dst.Extent.Height >> (int)mipLevel);
        uint d = Math.Max(1u, dst.Extent.Depth >> (int)mipLevel);
        var region = new BufferImageCopy
        {
            BufferOffset = src.Offset,
            BufferRowLength = 0,   // tightly packed
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(dst.Aspect, mipLevel, 0, dst.ArrayLayers),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(w, h, d),
        };
        vk.CmdCopyBufferToImage(cmd, _staging.Handle, dst.Handle, ImageLayout.TransferDstOptimal, 1, &region);

        _batchImages.Add(new PendingImage { Image = dst.Handle, Range = dst.FullRange, FinalLayout = finalLayout, DstStages = dstStages, DstAccess = dstAccess });
        BytesUploaded += src.Size;
        return _timelineValue + 1;
    }

    /// <summary>
    /// Records the release barriers, submits the batch and returns the timeline value it signals
    /// (0 when nothing was recorded, in which case nothing is submitted).
    /// </summary>
    public ulong EndBatch()
    {
        EnsureBatchOpen();
        _batchOpen = false;
        if (!_batchHasWork)
        {
            return 0;
        }

        var vk = _device.Vk;
        var slot = _slots[_slotIndex];
        var cmd = slot.Cmd;
        ulong value = ++_timelineValue;

        RecordReleaseBarriers(cmd);
        vk.EndCommandBuffer(cmd).Check("vkEndCommandBuffer(transfer)");

        var signal = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = Timeline,
            Value = value,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };
        var cmdInfo = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = cmd };
        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &cmdInfo,
            SignalSemaphoreInfoCount = 1,
            PSignalSemaphoreInfos = &signal,
        };
        vk.QueueSubmit2(_queue, 1, &submit, default).Check("vkQueueSubmit2(transfer)");
        slot.SubmittedValue = value;
        BatchesSubmitted++;

        // Everything in this batch now waits for its acquire on the graphics queue.
        _awaitingBuffers.AddRange(_batchBuffers);
        _awaitingImages.AddRange(_batchImages);
        _awaitingValue = value;
        _batchBuffers.Clear();
        _batchImages.Clear();
        return value;
    }

    // ---- Graphics side ----------------------------------------------------------------------------

    /// <summary>
    /// Records acquire barriers for every submitted-but-unacquired upload into <paramref name="graphicsCmd"/>
    /// and returns the transfer timeline value the enclosing submission must wait on (0 = nothing to
    /// wait for). Call at the top of the graphics command buffer, before any consumer of the data.
    /// Advances <see cref="AcquiredThrough"/>.
    /// </summary>
    public ulong RecordAcquires(CommandBuffer graphicsCmd)
    {
        if (_awaitingBuffers.Count == 0 && _awaitingImages.Count == 0)
        {
            return 0;
        }

        EnsureCapacity(ref _bufferBarriers, _awaitingBuffers.Count);
        EnsureCapacity(ref _imageBarriers, _awaitingImages.Count);

        for (int i = 0; i < _awaitingBuffers.Count; i++)
        {
            var p = _awaitingBuffers[i];
            // Acquire half of the ownership transfer. The source scope is empty by definition (the
            // release supplied it); ALL_COMMANDS with no access is the sync2 spelling of TOP_OF_PIPE.
            _bufferBarriers[i] = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllCommandsBit,
                SrcAccessMask = AccessFlags2.None,
                DstStageMask = p.DstStages,
                DstAccessMask = p.DstAccess,
                SrcQueueFamilyIndex = _ownershipTransfer ? _srcFamily : Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = _ownershipTransfer ? _dstFamily : Vk.QueueFamilyIgnored,
                Buffer = p.Buffer,
                Offset = p.Offset,
                Size = p.Size,
            };
        }

        for (int i = 0; i < _awaitingImages.Count; i++)
        {
            var p = _awaitingImages[i];
            _imageBarriers[i] = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllCommandsBit,
                SrcAccessMask = AccessFlags2.None,
                DstStageMask = p.DstStages,
                DstAccessMask = p.DstAccess,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = p.FinalLayout,
                SrcQueueFamilyIndex = _ownershipTransfer ? _srcFamily : Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = _ownershipTransfer ? _dstFamily : Vk.QueueFamilyIgnored,
                Image = p.Image,
                SubresourceRange = p.Range,
            };
        }

        if (!_ownershipTransfer && _awaitingBuffers.Count > 0 && _awaitingImages.Count == 0)
        {
            // Same family: the timeline semaphore wait already makes the copy's writes visible to the
            // waiting stages, so buffer barriers would be redundant. Images still need their layout change.
        }
        else
        {
            fixed (BufferMemoryBarrier2* bb = _bufferBarriers)
            fixed (ImageMemoryBarrier2* ib = _imageBarriers)
            {
                var dep = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = _ownershipTransfer ? (uint)_awaitingBuffers.Count : 0u,
                    PBufferMemoryBarriers = bb,
                    ImageMemoryBarrierCount = (uint)_awaitingImages.Count,
                    PImageMemoryBarriers = ib,
                };
                _device.Vk.CmdPipelineBarrier2(graphicsCmd, &dep);
            }
        }

        ulong wait = _awaitingValue;
        AcquiredThrough = wait;
        _awaitingBuffers.Clear();
        _awaitingImages.Clear();
        return wait;
    }

    // ---- Internals --------------------------------------------------------------------------------

    private void RecordReleaseBarriers(CommandBuffer cmd)
    {
        EnsureCapacity(ref _bufferBarriers, _batchBuffers.Count);
        EnsureCapacity(ref _imageBarriers, _batchImages.Count);

        int bufferCount = 0;
        if (_ownershipTransfer)
        {
            for (int i = 0; i < _batchBuffers.Count; i++)
            {
                var p = _batchBuffers[i];
                // Release half: make the copy's writes available, hand the range to graphics. The
                // destination scope is ignored for a release but must still be valid on this queue, so
                // ALL_COMMANDS with no access (the sync2 spelling of BOTTOM_OF_PIPE).
                _bufferBarriers[bufferCount++] = new BufferMemoryBarrier2
                {
                    SType = StructureType.BufferMemoryBarrier2,
                    SrcStageMask = PipelineStageFlags2.CopyBit,
                    SrcAccessMask = AccessFlags2.TransferWriteBit,
                    DstStageMask = PipelineStageFlags2.AllCommandsBit,
                    DstAccessMask = AccessFlags2.None,
                    SrcQueueFamilyIndex = _srcFamily,
                    DstQueueFamilyIndex = _dstFamily,
                    Buffer = p.Buffer,
                    Offset = p.Offset,
                    Size = p.Size,
                };
            }
        }

        for (int i = 0; i < _batchImages.Count; i++)
        {
            var p = _batchImages[i];
            _imageBarriers[i] = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.CopyBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.AllCommandsBit,
                DstAccessMask = AccessFlags2.None,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = p.FinalLayout,
                SrcQueueFamilyIndex = _ownershipTransfer ? _srcFamily : Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = _ownershipTransfer ? _dstFamily : Vk.QueueFamilyIgnored,
                Image = p.Image,
                SubresourceRange = p.Range,
            };
        }

        if (bufferCount == 0 && _batchImages.Count == 0)
        {
            return;
        }

        fixed (BufferMemoryBarrier2* bb = _bufferBarriers)
        fixed (ImageMemoryBarrier2* ib = _imageBarriers)
        {
            var dep = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = (uint)bufferCount,
                PBufferMemoryBarriers = bb,
                ImageMemoryBarrierCount = (uint)_batchImages.Count,
                PImageMemoryBarriers = ib,
            };
            _device.Vk.CmdPipelineBarrier2(cmd, &dep);
        }
    }

    private CommandBuffer EnsureRecording()
    {
        var cmd = _slots[_slotIndex].Cmd;
        if (!_batchHasWork)
        {
            var vk = _device.Vk;
            vk.ResetCommandBuffer(cmd, CommandBufferResetFlags.None).Check("vkResetCommandBuffer(transfer)");
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            vk.BeginCommandBuffer(cmd, &begin).Check("vkBeginCommandBuffer(transfer)");
            _batchHasWork = true;
        }

        return cmd;
    }

    private void EnsureBatchOpen()
    {
        if (!_batchOpen)
        {
            throw new InvalidOperationException("No upload batch is open; call BeginBatch first.");
        }
    }

    private void RetireStaging(ulong completed)
    {
        while (_regions.TryPeek(out var region) && region.RetireAfter <= completed)
        {
            _regions.Dequeue();
            _live -= region.Size;
            _tail = (region.Offset + region.Size) % _staging.Size;
        }
    }

    private void WaitForValue(ulong value)
    {
        var semaphore = Timeline;
        var waitInfo = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &semaphore,
            PValues = &value,
        };
        _device.Vk.WaitSemaphores(_device.Device, &waitInfo, ulong.MaxValue).Check("vkWaitSemaphores(upload)");
    }

    private static void EnsureCapacity<T>(ref T[] array, int needed)
    {
        if (array.Length < needed)
        {
            Array.Resize(ref array, Math.Max(needed, array.Length * 2));
        }
    }

    /// <summary>Caller must have made the device idle.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var vk = _device.Vk;
        _staging.Dispose();
        vk.DestroySemaphore(_device.Device, Timeline, null);
        vk.DestroyCommandPool(_device.Device, _pool, null);
        RenderLog.Info(Tag, $"Uploader closed: {BatchesSubmitted} batches, {BytesUploaded >> 20} MiB uploaded.");
    }
}
