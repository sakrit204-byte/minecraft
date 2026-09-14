namespace SakritCraft.Render.Terrain;

/// <summary>
/// Material identifiers written into the density volume and carried on every terrain vertex. These
/// are placeholders for the material system in docs/MASTER-PLAN.html section 07; the shader maps them
/// to flat colours, choosing grass versus soil by slope so a hillside reads as a hillside. Zero is
/// reserved: the mesher treats it as "no vote".
/// </summary>
public static class TerrainMaterial
{
    public const byte None = 0;
    public const byte Rock = 1;
    /// <summary>Loose surface layer. Rendered as grass on gentle slopes, exposed soil on steeper ones.</summary>
    public const byte Soil = 2;
    public const byte Sand = 3;
    public const byte Snow = 4;
    public const byte Gravel = 5;

    /// <summary>Distance below the real surface, in metres, that still counts as the surface layer.</summary>
    public const double SoilDepth = 3.5;
    /// <summary>How far below the 2D height field the surface layer may reach; deeper solid rock (cave walls) is never soil.</summary>
    public const double SurfaceLayerReach = 12.0;
    /// <summary>Gravel band between soil and bedrock, so cliffs show a strata line rather than one flat tone.</summary>
    public const double GravelDepth = 5.5;
    /// <summary>Metres above sea level up to which the surface layer is sand.</summary>
    public const double BeachHeight = 2.5;
    /// <summary>Column temperature at or below which the surface layer is snow.</summary>
    public const double SnowTemperatureC = -2.0;
}
