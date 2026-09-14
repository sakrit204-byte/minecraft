namespace SakritCraft.Content.Creatures;

/// <summary>
/// Stable item identifiers. These are written into save files and network messages, so a
/// number here is permanent: add new ones, never renumber existing ones.
/// </summary>
public static class ItemIds
{
    public const ushort None = 0;

    // Terrain materials, yielded by mining
    public const ushort Stone = 10;
    public const ushort Soil = 11;
    public const ushort Sand = 12;
    public const ushort Gravel = 13;
    public const ushort Clay = 14;

    // Ores and metals
    public const ushort CopperOre = 20;
    public const ushort TinOre = 21;
    public const ushort IronOre = 22;
    public const ushort CopperIngot = 25;
    public const ushort BronzeIngot = 26;
    public const ushort IronIngot = 27;
    public const ushort SteelIngot = 28;

    // Organic
    public const ushort Log = 40;
    public const ushort Plank = 41;
    public const ushort Fibre = 42;
    public const ushort Silk = 43;
    public const ushort Hide = 44;
    public const ushort Bone = 45;
    public const ushort Saltpetre = 46;

    // Food
    public const ushort RawMeat = 60;
    public const ushort CookedMeat = 61;
    public const ushort Grain = 62;
    public const ushort Bread = 63;
    public const ushort RottenFlesh = 64;

    // Tools
    public const ushort StonePickaxe = 80;
    public const ushort BronzePickaxe = 81;
    public const ushort IronPickaxe = 82;
    public const ushort StoneAxe = 85;
    public const ushort IronAxe = 86;
    public const ushort StoneShovel = 88;
    public const ushort IronSword = 90;
    public const ushort Bow = 91;

    // Placeables
    public const ushort Torch = 100;
    public const ushort WallSegment = 110;
    public const ushort FloorSegment = 111;
    public const ushort RoofSegment = 112;
    public const ushort DoorFrame = 113;
    public const ushort SupportBeam = 114;
}

/// <summary>Broad behaviour of an item, which decides what the interaction systems do with it.</summary>
public enum ItemKind : byte
{
    Material = 0,
    Tool = 1,
    Weapon = 2,
    Food = 3,
    /// <summary>Placed into the world as a light source.</summary>
    LightSource = 4,
    /// <summary>Placed into the world as a structural part.</summary>
    BuildingPart = 5,
}

/// <summary>Which kind of work a tool is for. Using the wrong one is slow rather than impossible.</summary>
public enum ToolKind : byte
{
    None = 0,
    Pickaxe = 1,
    Shovel = 2,
    Axe = 3,
    Chisel = 4,
    Blade = 5,
    Bow = 6,
}

public sealed record ItemDefinition
{
    public required ushort Id { get; init; }
    public required string Name { get; init; }
    public required ItemKind Kind { get; init; }

    /// <summary>Most items carried at once in one stack.</summary>
    public int StackLimit { get; init; } = 64;

    /// <summary>Kilograms each. Carry capacity is a weight, not a slot count, which is what
    /// makes the encumbrance rule in the movement system matter.</summary>
    public double Mass { get; init; } = 0.4;

    // ── Tools ────────────────────────────────────────────────────────────────────
    public ToolKind Tool { get; init; } = ToolKind.None;
    /// <summary>Material tier. A material can only be mined by a tool of at least its own tier.</summary>
    public int Tier { get; init; }
    /// <summary>Multiplies mining speed.</summary>
    public double Efficiency { get; init; } = 1.0;
    /// <summary>Radius of the volume a swing removes, in metres.</summary>
    public double CarveRadius { get; init; }
    public int Durability { get; init; }

    // ── Weapons ──────────────────────────────────────────────────────────────────
    public float AttackDamage { get; init; }
    public DamageKind AttackDamageType { get; init; } = DamageKind.Blunt;

    // ── Food ─────────────────────────────────────────────────────────────────────
    public float Nourishment { get; init; }
    public float Saturation { get; init; }
    /// <summary>Chance of illness. Raw meat and rotten flesh are a gamble, not a meal.</summary>
    public float IllnessChance { get; init; }

    // ── Placeables ───────────────────────────────────────────────────────────────
    public byte LightLevel { get; init; }
}

/// <summary>Every item in the game.</summary>
public static class ItemCatalog
{
    private static readonly Dictionary<ushort, ItemDefinition> Definitions = Build();

    public static IReadOnlyCollection<ItemDefinition> All => Definitions.Values;

    public static ItemDefinition Get(ushort id) => Definitions.TryGetValue(id, out var d)
        ? d
        : throw new KeyNotFoundException($"No item with id {id}.");

    public static bool TryGet(ushort id, out ItemDefinition definition)
        => Definitions.TryGetValue(id, out definition!);

    private static Dictionary<ushort, ItemDefinition> Build()
    {
        var list = new List<ItemDefinition>
        {
            Material(ItemIds.Stone, "Stone", 1.4),
            Material(ItemIds.Soil, "Soil", 1.1),
            Material(ItemIds.Sand, "Sand", 1.2),
            Material(ItemIds.Gravel, "Gravel", 1.3),
            Material(ItemIds.Clay, "Clay", 1.2),

            Material(ItemIds.CopperOre, "Copper Ore", 2.0),
            Material(ItemIds.TinOre, "Tin Ore", 1.9),
            Material(ItemIds.IronOre, "Iron Ore", 2.4),
            Material(ItemIds.CopperIngot, "Copper Ingot", 1.6),
            Material(ItemIds.BronzeIngot, "Bronze Ingot", 1.7),
            Material(ItemIds.IronIngot, "Iron Ingot", 2.0),
            Material(ItemIds.SteelIngot, "Steel Ingot", 2.0),

            Material(ItemIds.Log, "Log", 2.2),
            Material(ItemIds.Plank, "Plank", 0.9),
            Material(ItemIds.Fibre, "Fibre", 0.1),
            Material(ItemIds.Silk, "Silk", 0.1),
            Material(ItemIds.Hide, "Hide", 0.7),
            Material(ItemIds.Bone, "Bone", 0.3),
            Material(ItemIds.Saltpetre, "Saltpetre", 0.3),

            Food(ItemIds.RawMeat, "Raw Meat", nourishment: 3, saturation: 1.8f, illness: 0.30f, mass: 0.6),
            Food(ItemIds.CookedMeat, "Cooked Meat", 8, 12.8f, 0.0f, 0.5),
            Food(ItemIds.Grain, "Grain", 1, 0.6f, 0.0f, 0.2),
            Food(ItemIds.Bread, "Bread", 5, 6.0f, 0.0f, 0.3),
            Food(ItemIds.RottenFlesh, "Rotten Flesh", 4, 0.8f, 0.80f, 0.4),

            Tool(ItemIds.StonePickaxe, "Stone Pickaxe", ToolKind.Pickaxe, tier: 1, efficiency: 1.0, carve: 0.35, durability: 130, mass: 2.4),
            Tool(ItemIds.BronzePickaxe, "Bronze Pickaxe", ToolKind.Pickaxe, 2, 1.6, 0.35, 250, 2.6),
            Tool(ItemIds.IronPickaxe, "Iron Pickaxe", ToolKind.Pickaxe, 3, 2.4, 0.38, 500, 2.8),
            Tool(ItemIds.StoneAxe, "Stone Axe", ToolKind.Axe, 1, 1.0, 0.0, 130, 2.4),
            Tool(ItemIds.IronAxe, "Iron Axe", ToolKind.Axe, 3, 2.4, 0.0, 500, 2.8),
            Tool(ItemIds.StoneShovel, "Stone Shovel", ToolKind.Shovel, 1, 1.0, 0.55, 130, 2.0),

            Weapon(ItemIds.IronSword, "Iron Sword", damage: 6.5f, type: DamageKind.Slashing, durability: 420, mass: 1.8),
            Weapon(ItemIds.Bow, "Bow", 4.0f, DamageKind.Piercing, 300, 1.0),

            new()
            {
                Id = ItemIds.Torch, Name = "Torch", Kind = ItemKind.LightSource,
                StackLimit = 64, Mass = 0.15, LightLevel = 14,
            },

            BuildingPart(ItemIds.WallSegment, "Wall Segment", 12.0),
            BuildingPart(ItemIds.FloorSegment, "Floor Segment", 11.0),
            BuildingPart(ItemIds.RoofSegment, "Roof Segment", 10.0),
            BuildingPart(ItemIds.DoorFrame, "Door Frame", 14.0),
            BuildingPart(ItemIds.SupportBeam, "Support Beam", 8.0),
        };

        return list.ToDictionary(d => d.Id);
    }

    private static ItemDefinition Material(ushort id, string name, double mass) => new()
    {
        Id = id, Name = name, Kind = ItemKind.Material, Mass = mass, StackLimit = 64,
    };

    private static ItemDefinition Food(ushort id, string name, float nourishment, float saturation,
                                       float illness, double mass) => new()
    {
        Id = id, Name = name, Kind = ItemKind.Food, Mass = mass, StackLimit = 16,
        Nourishment = nourishment, Saturation = saturation, IllnessChance = illness,
    };

    private static ItemDefinition Tool(ushort id, string name, ToolKind tool, int tier, double efficiency,
                                       double carve, int durability, double mass) => new()
    {
        Id = id, Name = name, Kind = ItemKind.Tool, Mass = mass, StackLimit = 1,
        Tool = tool, Tier = tier, Efficiency = efficiency, CarveRadius = carve, Durability = durability,
    };

    private static ItemDefinition Weapon(ushort id, string name, float damage, DamageKind type,
                                         int durability, double mass) => new()
    {
        Id = id, Name = name, Kind = ItemKind.Weapon, Mass = mass, StackLimit = 1,
        Tool = type == DamageKind.Piercing ? ToolKind.Bow : ToolKind.Blade,
        AttackDamage = damage, AttackDamageType = type, Durability = durability, Tier = 2,
    };

    private static ItemDefinition BuildingPart(ushort id, string name, double mass) => new()
    {
        Id = id, Name = name, Kind = ItemKind.BuildingPart, Mass = mass, StackLimit = 16,
    };
}
