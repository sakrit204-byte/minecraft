using SakritCraft.World.Editing;

namespace SakritCraft.World.Generation;

/// <summary>
/// Material identifiers written into the density volume and carried on every terrain vertex.
/// Zero is reserved: the mesher treats it as "no vote".
/// </summary>
public static class TerrainMaterials
{
    public const byte None = 0;
    public const byte Rock = 1;
    /// <summary>Loose surface layer. Drawn as grass on gentle slopes and bare soil on steeper ones.</summary>
    public const byte Soil = 2;
    public const byte Sand = 3;
    public const byte Snow = 4;
    public const byte Gravel = 5;

    /// <summary>How far below the height field still counts as the outer surface rather than a cave wall.</summary>
    public const double SurfaceLayerReach = 12.0;

    /// <summary>Metres above sea level up to which the surface layer is sand.</summary>
    public const double BeachHeight = 2.5;

    /// <summary>Column temperature at or below which the surface layer is snow.</summary>
    public const double SnowTemperatureC = -2.0;

    /// <summary>
    /// The material at a point, given the column it sits in.
    /// <para>
    /// This lives in world generation rather than in the renderer because mining has to ask the
    /// same question the mesher does, and two implementations of one rule would eventually
    /// disagree about what a player is digging.
    /// </para>
    /// <para>
    /// There is deliberately no depth threshold. The mesher takes its material from the solid
    /// corner of a surface crossing, so every sample this is read from is by construction just
    /// below a surface; asking how deep it is only reintroduces a dependence on the sample
    /// spacing, and neighbouring chunks at different levels of detail then disagree about the
    /// same ground. What still has to be asked is <em>which</em> surface, so that grass does not
    /// grow on a cave wall forty metres down.
    /// </para>
    /// </summary>
    public static byte MaterialFor(in ColumnSample column, double y, double spacing)
    {
        double belowHeightField = column.BaseHeight - y;
        double surfaceReach = SurfaceLayerReach
                            + LandformFields.OverhangStrength(column.Erosion)
                            + spacing * 2.0;

        if (belowHeightField >= surfaceReach) return Rock;
        if (column.TemperatureC <= SnowTemperatureC) return Snow;
        if (y < LandformFields.SeaLevel + BeachHeight) return Sand;
        return Soil;
    }

    /// <summary>
    /// The material at a world point, including anything the player has filled in. Used by
    /// mining to decide hardness, tool requirements and what is dropped.
    /// </summary>
    public static byte MaterialAt(DensityField field, double x, double y, double z)
    {
        byte edited = field.Edits.MaterialAt(x, y, z);
        if (edited != None) return edited;

        ColumnSample column = field.Landform.Sample(x, z);
        return MaterialFor(in column, y, 0.5);
    }

    /// <summary>Hardness in the same shape as Minecraft's, so its pacing carries over.</summary>
    public static double Hardness(byte material) => material switch
    {
        Rock => 1.5,
        Gravel => 0.6,
        Soil => 0.5,
        Sand => 0.5,
        Snow => 0.2,
        _ => 1.0,
    };

    /// <summary>Tool tier required to get anything at all. Below it, the material yields nothing.</summary>
    public static int RequiredTier(byte material) => material switch
    {
        Rock => 1,
        Gravel => 0,
        Soil => 0,
        Sand => 0,
        Snow => 0,
        _ => 0,
    };

    /// <summary>Which kind of tool this material wants. The wrong one is slow, not impossible.</summary>
    public static bool WantsPickaxe(byte material) => material is Rock or Gravel;

    /// <summary>Items yielded per cubic metre removed.</summary>
    public static double YieldPerCubicMetre(byte material) => material switch
    {
        Rock => 5.0,
        Gravel => 6.0,
        Soil => 6.0,
        Sand => 6.0,
        Snow => 4.0,
        _ => 0.0,
    };

    public static string Name(byte material) => material switch
    {
        Rock => "rock",
        Soil => "soil",
        Sand => "sand",
        Snow => "snow",
        Gravel => "gravel",
        _ => "nothing",
    };
}
