using SakritCraft.Content.Creatures;

namespace SakritCraft.Sim;

/// <summary>One stack of one item.</summary>
public struct ItemStack
{
    public ushort ItemId;
    public int Count;
    /// <summary>Remaining durability for tools and weapons; ignored for everything else.</summary>
    public int Durability;

    public readonly bool IsEmpty => ItemId == ItemIds.None || Count <= 0;
    public static ItemStack Empty => default;

    public static ItemStack Of(ushort itemId, int count = 1)
    {
        ItemDefinition definition = ItemCatalog.Get(itemId);
        return new ItemStack { ItemId = itemId, Count = count, Durability = definition.Durability };
    }
}

/// <summary>
/// What a character is carrying.
/// <para>
/// Capacity is a mass, not a number of slots. That is the whole point: it makes a mining
/// trip a decision about what to leave behind rather than a walk home with everything,
/// and it gives the encumbrance penalty in the movement code something real to read.
/// </para>
/// </summary>
public sealed class Inventory
{
    private readonly List<ItemStack> _stacks = new();

    /// <summary>Kilograms that can be carried without penalty.</summary>
    public double CarryLimit { get; set; } = 60.0;

    public IReadOnlyList<ItemStack> Stacks => _stacks;

    /// <summary>Total mass carried, in kilograms.</summary>
    public double Mass
    {
        get
        {
            double total = 0.0;
            foreach (ItemStack stack in _stacks)
            {
                if (stack.IsEmpty) continue;
                total += ItemCatalog.Get(stack.ItemId).Mass * stack.Count;
            }
            return total;
        }
    }

    public bool IsOverloaded => Mass > CarryLimit;

    /// <summary>
    /// Adds items, filling partial stacks first. Returns how many could not be taken, which
    /// is non-zero only when the carry limit is reached.
    /// </summary>
    public int Add(ushort itemId, int count)
    {
        if (count <= 0) return 0;
        ItemDefinition definition = ItemCatalog.Get(itemId);

        // Top up existing stacks before starting new ones.
        for (int i = 0; i < _stacks.Count && count > 0; i++)
        {
            ItemStack stack = _stacks[i];
            if (stack.ItemId != itemId || stack.Count >= definition.StackLimit) continue;

            int room = definition.StackLimit - stack.Count;
            int take = Math.Min(room, count);
            if (Mass + definition.Mass * take > CarryLimit) take = AffordableCount(definition);
            if (take <= 0) break;

            stack.Count += take;
            _stacks[i] = stack;
            count -= take;
        }

        while (count > 0)
        {
            int affordable = AffordableCount(definition);
            if (affordable <= 0) break;

            int take = Math.Min(Math.Min(definition.StackLimit, count), affordable);
            var stack = ItemStack.Of(itemId, take);
            _stacks.Add(stack);
            count -= take;
        }

        return count;
    }

    private int AffordableCount(ItemDefinition definition)
    {
        if (definition.Mass <= 0.0) return int.MaxValue;
        double room = CarryLimit - Mass;
        return room <= 0.0 ? 0 : (int)Math.Floor(room / definition.Mass);
    }

    /// <summary>Removes up to <paramref name="count"/> of an item. Returns how many were removed.</summary>
    public int Remove(ushort itemId, int count)
    {
        int removed = 0;
        for (int i = _stacks.Count - 1; i >= 0 && removed < count; i--)
        {
            ItemStack stack = _stacks[i];
            if (stack.ItemId != itemId) continue;

            int take = Math.Min(stack.Count, count - removed);
            stack.Count -= take;
            removed += take;

            if (stack.Count <= 0) _stacks.RemoveAt(i);
            else _stacks[i] = stack;
        }
        return removed;
    }

    public int CountOf(ushort itemId)
    {
        int total = 0;
        foreach (ItemStack stack in _stacks)
        {
            if (stack.ItemId == itemId) total += stack.Count;
        }
        return total;
    }

    public bool Has(ushort itemId, int count = 1) => CountOf(itemId) >= count;

    /// <summary>Best tool of a kind currently carried, or null. Used to resolve mining speed.</summary>
    public ItemDefinition? BestTool(ToolKind kind)
    {
        ItemDefinition? best = null;
        foreach (ItemStack stack in _stacks)
        {
            if (stack.IsEmpty) continue;
            ItemDefinition definition = ItemCatalog.Get(stack.ItemId);
            if (definition.Tool != kind) continue;
            if (best is null || definition.Tier > best.Tier
                || (definition.Tier == best.Tier && definition.Efficiency > best.Efficiency))
            {
                best = definition;
            }
        }
        return best;
    }

    /// <summary>Highest attack damage among carried weapons, or bare hands.</summary>
    public (float Damage, DamageKind Type) BestWeapon()
    {
        float damage = 1.0f;
        DamageKind type = DamageKind.Blunt;
        foreach (ItemStack stack in _stacks)
        {
            if (stack.IsEmpty) continue;
            ItemDefinition definition = ItemCatalog.Get(stack.ItemId);
            if (definition.Kind != ItemKind.Weapon || definition.AttackDamage <= damage) continue;
            damage = definition.AttackDamage;
            type = definition.AttackDamageType;
        }
        return (damage, type);
    }

    /// <summary>Empties the inventory and returns what was in it, for death drops.</summary>
    public List<ItemStack> TakeAll()
    {
        var all = new List<ItemStack>(_stacks);
        _stacks.Clear();
        return all;
    }

    public void Clear() => _stacks.Clear();
}
