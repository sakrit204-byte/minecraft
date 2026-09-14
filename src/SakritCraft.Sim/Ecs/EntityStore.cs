using System.Runtime.CompilerServices;

namespace SakritCraft.Sim.Ecs;

/// <summary>
/// A handle to a simulated thing. The generation makes stale handles detectable: an
/// index is reused as soon as an entity dies, so without it a reference kept across a
/// death would silently start pointing at whatever was created next.
/// </summary>
public readonly record struct Entity(int Index, int Generation)
{
    public static Entity None => new(-1, 0);
    public bool IsNone => Index < 0;
    public override string ToString() => IsNone ? "Entity.None" : $"Entity({Index}v{Generation})";
}

/// <summary>
/// Dense storage for one component type, indexed by entity.
/// <para>
/// A sparse set: components live packed in <see cref="Data"/> so a system iterates them
/// contiguously, while a lookup table maps an entity index to its slot. Removal swaps
/// the last element into the hole, which keeps the array packed at the cost of not
/// preserving order. No system here depends on component order, and any that did would
/// be depending on something the simulation never promises.
/// </para>
/// </summary>
public sealed class ComponentStore<T> where T : struct
{
    private int[] _slotOf;        // entity index -> dense slot, or -1
    private int[] _ownerOf;       // dense slot -> entity index
    private T[] _data;

    public int Count { get; private set; }

    public ComponentStore(int capacity = 256)
    {
        _slotOf = new int[capacity];
        Array.Fill(_slotOf, -1);
        _ownerOf = new int[capacity];
        _data = new T[capacity];
    }

    /// <summary>The packed component data. Valid up to <see cref="Count"/>.</summary>
    public Span<T> Data => _data.AsSpan(0, Count);

    /// <summary>Entity index owning each packed slot. Parallel to <see cref="Data"/>.</summary>
    public ReadOnlySpan<int> Owners => _ownerOf.AsSpan(0, Count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has(Entity entity)
        => entity.Index >= 0 && entity.Index < _slotOf.Length && _slotOf[entity.Index] >= 0;

    public ref T Add(Entity entity, in T value)
    {
        EnsureEntityCapacity(entity.Index);

        int slot = _slotOf[entity.Index];
        if (slot >= 0)
        {
            _data[slot] = value;
            return ref _data[slot];
        }

        if (Count == _data.Length)
        {
            Array.Resize(ref _data, _data.Length * 2);
            Array.Resize(ref _ownerOf, _ownerOf.Length * 2);
        }

        slot = Count++;
        _data[slot] = value;
        _ownerOf[slot] = entity.Index;
        _slotOf[entity.Index] = slot;
        return ref _data[slot];
    }

    public ref T Get(Entity entity)
    {
        int slot = _slotOf[entity.Index];
        if (slot < 0) throw new KeyNotFoundException($"{entity} has no {typeof(T).Name}.");
        return ref _data[slot];
    }

    public bool TryGet(Entity entity, out T value)
    {
        if (!Has(entity)) { value = default; return false; }
        value = _data[_slotOf[entity.Index]];
        return true;
    }

    public void Remove(Entity entity)
    {
        if (!Has(entity)) return;

        int slot = _slotOf[entity.Index];
        int last = --Count;

        // Swap the tail into the hole so the array stays packed.
        _data[slot] = _data[last];
        _ownerOf[slot] = _ownerOf[last];
        _slotOf[_ownerOf[slot]] = slot;
        _slotOf[entity.Index] = -1;
    }

    private void EnsureEntityCapacity(int index)
    {
        if (index < _slotOf.Length) return;

        int size = _slotOf.Length;
        while (size <= index) size *= 2;
        int old = _slotOf.Length;
        Array.Resize(ref _slotOf, size);
        Array.Fill(_slotOf, -1, old, size - old);
    }
}

/// <summary>
/// Every entity in the simulation, and the components attached to them.
/// <para>
/// Deliberately small. The alternative was an archetype engine, which pays off when
/// hundreds of systems iterate dozens of component combinations; this simulation has
/// perhaps a dozen of each, and a sparse set is far easier to reason about when a bug
/// appears in entity lifetime rather than in iteration speed.
/// </para>
/// </summary>
public sealed class EntityStore
{
    private readonly List<int> _generations = new();
    private readonly Stack<int> _free = new();
    private readonly Dictionary<Type, object> _stores = new();
    private readonly List<IComponentRemover> _removers = new();
    private readonly List<Entity> _pendingDestroy = new();

    /// <summary>Entities currently alive.</summary>
    public int AliveCount { get; private set; }

    public Entity Create()
    {
        if (_free.Count > 0)
        {
            int index = _free.Pop();
            AliveCount++;
            return new Entity(index, _generations[index]);
        }

        _generations.Add(1);
        AliveCount++;
        return new Entity(_generations.Count - 1, 1);
    }

    /// <summary>
    /// The live handle for a raw entity index, or None if that slot is free. Systems iterate
    /// packed component arrays, which carry owner indices rather than full handles, so this is
    /// how they get back to an entity.
    /// </summary>
    public Entity HandleOf(int index)
        => index >= 0 && index < _generations.Count ? new Entity(index, _generations[index]) : Entity.None;

    public bool IsAlive(Entity entity)
        => entity.Index >= 0
        && entity.Index < _generations.Count
        && _generations[entity.Index] == entity.Generation
        && _generations[entity.Index] > 0;

    /// <summary>
    /// Marks an entity for removal. Destruction is deferred to <see cref="FlushDestroyed"/>
    /// so that a system iterating a component array cannot have it mutated underneath it,
    /// which is the classic way an entity-component simulation corrupts itself.
    /// </summary>
    public void Destroy(Entity entity)
    {
        if (!IsAlive(entity)) return;
        _pendingDestroy.Add(entity);
    }

    /// <summary>Applies every deferred destruction. Called once at the end of a tick.</summary>
    public int FlushDestroyed()
    {
        if (_pendingDestroy.Count == 0) return 0;

        foreach (Entity entity in _pendingDestroy)
        {
            if (!IsAlive(entity)) continue;

            foreach (IComponentRemover remover in _removers)
            {
                remover.RemoveFrom(entity);
            }

            // Bump the generation so any handle still pointing here stops resolving.
            _generations[entity.Index] = _generations[entity.Index] + 1;
            _free.Push(entity.Index);
            AliveCount--;
        }

        int count = _pendingDestroy.Count;
        _pendingDestroy.Clear();
        return count;
    }

    public ComponentStore<T> Store<T>() where T : struct
    {
        if (_stores.TryGetValue(typeof(T), out object? existing))
        {
            return (ComponentStore<T>)existing;
        }

        var created = new ComponentStore<T>();
        _stores[typeof(T)] = created;
        _removers.Add(new Remover<T>(created));
        return created;
    }

    // The dictionary holds a wrapper so destruction can clear every store without knowing
    // the component types at compile time.
    private interface IComponentRemover
    {
        void RemoveFrom(Entity entity);
    }

    private sealed class Remover<T> : IComponentRemover where T : struct
    {
        private readonly ComponentStore<T> _store;
        public Remover(ComponentStore<T> store) => _store = store;
        public void RemoveFrom(Entity entity) => _store.Remove(entity);
    }
}
