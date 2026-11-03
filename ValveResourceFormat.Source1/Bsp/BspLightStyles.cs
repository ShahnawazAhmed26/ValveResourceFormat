namespace ValveResourceFormat.Source1.Bsp;

/// <summary>
/// The brightness of every light style of a map when it starts, as a fraction of the light baked into the lightmaps.
/// A face stores up to four lightmaps, one per light style, and the engine adds them scaled by the current
/// brightness of their style.
/// </summary>
/// <remarks>
/// A style is a pattern of letters from <c>a</c> (dark) to <c>z</c> (twice as bright as <c>m</c>, which is normal),
/// one letter per tenth of a second. An animated style is represented by the average of its pattern.
/// </remarks>
internal static class BspLightStyles
{
    /// <summary>The style stored in the unused slots of a face.</summary>
    public const byte None = 255;

    /// <summary>The first style that lights assign to themselves, so that they can be switched.</summary>
    public const int FirstSwitchableStyle = 32;

    private const int StyleCount = 64;
    private const int StartDisabledFlag = 1;
    private const char DarkLetter = 'a';
    private const char NormalLetter = 'm';

    private static readonly string[] LightClassNames = ["light", "light_spot", "light_environment"];

    // Styles 0 to 12 are the same in every map
    private static readonly string[] DefaultPatterns =
    [
        "m",
        "mmnmmommommnonmmonqnmmo",
        "abcdefghijklmnopqrstuvwxyzyxwvutsrqponmlkjihgfedcba",
        "mmmmmaaaaammmmmaaaaaabcdefgabcdefg",
        "mamamamamama",
        "jklmnopqrstuvwxyzyxwvutsrqponmlkj",
        "nmonqnmomnmomomno",
        "mmmaaaabcdefgmmmmaaaammmaamm",
        "mmmaaammmaaammmabcdefaaaammmmabcdefmmmaaaa",
        "aaaaaaaazzzzzzzz",
        "mmamammmmammamamaaamammma",
        "abcdefghijklmnopqrrqponmlkjihgfedcba",
        "mmnnmmnnnmmnn",
    ];

    /// <summary>Gets the brightness of every possible style value, indexed by the style stored in a face.</summary>
    /// <param name="entities">The entities of the map, whose lights set the styles they use.</param>
    public static float[] GetDefaultBrightness(IReadOnlyList<BspEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        // A style that nothing sets is a little darker than normal
        var brightness = new float[byte.MaxValue + 1];
        Array.Fill(brightness, 256f / 264f);

        for (var style = 0; style < DefaultPatterns.Length; style++)
        {
            brightness[style] = GetPatternBrightness(DefaultPatterns[style]);
        }

        brightness[StyleCount - 1] = 0f;

        // The last light to set a style wins, as lights that share a style set the same one
        foreach (var entity in entities)
        {
            if (!TryGetLightStyle(entity, out var light))
            {
                continue;
            }

            if (light.StartsDisabled)
            {
                brightness[light.Style] = 0f;
                continue;
            }

            var pattern = light.Pattern;

            if (pattern.Length == 0)
            {
                pattern = GetDefaultPattern(entity.GetInt("defaultstyle"));
            }

            brightness[light.Style] = GetPatternBrightness(pattern);
        }

        return brightness;
    }

    /// <summary>The style a light switches, and how it starts.</summary>
    /// <param name="Style">The style number, from <see cref="FirstSwitchableStyle"/>.</param>
    /// <param name="StartsDisabled">Whether the light starts off.</param>
    /// <param name="Pattern">The pattern the light sets, or empty when it sets none.</param>
    internal readonly record struct LightStyle(int Style, bool StartsDisabled, string Pattern);

    /// <summary>Reads the style a light entity switches.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="light">The style of the light.</param>
    /// <returns>Whether the entity is a light with a style of its own.</returns>
    public static bool TryGetLightStyle(BspEntity entity, out LightStyle light)
    {
        ArgumentNullException.ThrowIfNull(entity);

        light = default;

        if (!IsLight(entity.ClassName))
        {
            return false;
        }

        var style = entity.GetInt("style");

        if (style < FirstSwitchableStyle || style >= StyleCount - 1)
        {
            return false;
        }

        var startsDisabled = (entity.GetInt("spawnflags") & StartDisabledFlag) != 0;
        light = new LightStyle(style, startsDisabled, entity.GetString("pattern"));
        return true;
    }

    private static bool IsLight(string className)
        => LightClassNames.Contains(className, StringComparer.OrdinalIgnoreCase);

    /// <summary>The number of styles that every map defines the pattern of.</summary>
    public static int DefaultPatternCount => DefaultPatterns.Length;

    /// <summary>Gets the pattern of one of the styles every map has, or the normal pattern for any other number.</summary>
    /// <param name="style">The style number.</param>
    public static string GetDefaultPattern(int style)
    {
        if (style > 0 && style < DefaultPatterns.Length)
        {
            return DefaultPatterns[style];
        }

        return DefaultPatterns[0];
    }

    private static float GetPatternBrightness(string pattern)
    {
        var sum = 0f;

        foreach (var letter in pattern)
        {
            sum += letter - DarkLetter;
        }

        return sum / pattern.Length / (NormalLetter - DarkLetter);
    }
}
