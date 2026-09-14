using System.Runtime.CompilerServices;

namespace SakritCraft.Render.Memory;

/// <summary>
/// Sub-allocates byte ranges out of one fixed-size span: sorted free list, first fit, coalescing on
/// free. It knows nothing about Vulkan, which is why both <see cref="MemoryBlock"/> (carving
/// VkDeviceMemory) and the terrain geometry pools (carving one big VkBuffer into per-chunk ranges)
/// use the same code. Not thread-safe: owners lock around it.
///
/// First fit rather than best fit or buddy allocation: chunk meshes are freed and re-allocated in
/// roughly the order they stream, so first fit fragments little in practice, and the sorted list
/// makes coalescing O(log n) per free. A TLSF allocator would be the upgrade if fragmentation ever
/// shows up in the statistics.
/// </summary>
public sealed class RangeAllocator
{
    private struct FreeRange
    {
        public ulong Offset;
        public ulong Size;
    }

    private readonly List<FreeRange> _free = new(16);

    /// <summary>Total capacity in bytes.</summary>
    public ulong Capacity { get; }
    /// <summary>Bytes currently handed out (excludes alignment padding, which stays on the free list).</summary>
    public ulong Used { get; private set; }
    public int AllocationCount { get; private set; }
    public bool IsEmpty => AllocationCount == 0;

    public RangeAllocator(ulong capacity)
    {
        Capacity = capacity;
        _free.Add(new FreeRange { Offset = 0, Size = capacity });
    }

    /// <summary>Largest single free range; the honest answer to "will a request of this size fit".</summary>
    public ulong LargestFreeRange
    {
        get
        {
            ulong largest = 0;
            for (int i = 0; i < _free.Count; i++)
            {
                if (_free[i].Size > largest) largest = _free[i].Size;
            }

            return largest;
        }
    }

    /// <summary>Finds the first free range that can hold <paramref name="size"/> bytes at <paramref name="alignment"/> (a power of two).</summary>
    public bool TryAllocate(ulong size, ulong alignment, out ulong offset)
    {
        for (int i = 0; i < _free.Count; i++)
        {
            var range = _free[i];
            ulong aligned = AlignUp(range.Offset, alignment);
            ulong padding = aligned - range.Offset;
            if (range.Size < padding + size)
            {
                continue;
            }

            // Carve [aligned, aligned+size) out of the range. Leading padding stays free, trailing remainder stays free.
            ulong tail = range.Size - padding - size;
            if (padding == 0 && tail == 0)
            {
                _free.RemoveAt(i);
            }
            else if (padding == 0)
            {
                _free[i] = new FreeRange { Offset = aligned + size, Size = tail };
            }
            else if (tail == 0)
            {
                _free[i] = new FreeRange { Offset = range.Offset, Size = padding };
            }
            else
            {
                _free[i] = new FreeRange { Offset = range.Offset, Size = padding };
                _free.Insert(i + 1, new FreeRange { Offset = aligned + size, Size = tail });
            }

            Used += size;
            AllocationCount++;
            offset = aligned;
            return true;
        }

        offset = 0;
        return false;
    }

    /// <summary>Returns a range obtained from <see cref="TryAllocate"/>, merging with free neighbours.</summary>
    public void Free(ulong offset, ulong size)
    {
        // Binary search for insertion point (free list is sorted by offset).
        int lo = 0, hi = _free.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_free[mid].Offset < offset) lo = mid + 1; else hi = mid;
        }

        int index = lo;
        bool mergePrev = index > 0 && _free[index - 1].Offset + _free[index - 1].Size == offset;
        bool mergeNext = index < _free.Count && offset + size == _free[index].Offset;

        if (mergePrev && mergeNext)
        {
            var prev = _free[index - 1];
            prev.Size += size + _free[index].Size;
            _free[index - 1] = prev;
            _free.RemoveAt(index);
        }
        else if (mergePrev)
        {
            var prev = _free[index - 1];
            prev.Size += size;
            _free[index - 1] = prev;
        }
        else if (mergeNext)
        {
            var next = _free[index];
            next.Offset = offset;
            next.Size += size;
            _free[index] = next;
        }
        else
        {
            _free.Insert(index, new FreeRange { Offset = offset, Size = size });
        }

        Used -= size;
        AllocationCount--;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);
}
