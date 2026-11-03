using Microsoft.Extensions.Logging;
using ValveResourceFormat.GoldSrc;
using ValveResourceFormat.IO.Fgd;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Source1.FileSystem;

namespace ValveResourceFormat.Source1.Entities;

/// <summary>
/// Gives the entity classes a Source 1 or GoldSrc map is shown with, from the tables built into the library.
/// Source 1 games are not detected as a <see cref="ValveResourceFormat.IO.Game"/>, so their tables are found by the Steam app id of their
/// <c>gameinfo.txt</c>, and GoldSrc games by the folder of their mod.
/// </summary>
internal static class Source1EntityClassCatalog
{
    private const string ResourceName = "ValveResourceFormat.Source1.Entities.entity_classes.kv3";

    private static readonly Lazy<BuiltInTables> BuiltIn = new(LoadBuiltIn);

    /// <summary>
    /// Gets the entity classes of a Source 1 game.
    /// </summary>
    /// <param name="fileSystem">The file system of the game.</param>
    /// <param name="logger">Where it is said which classes are used.</param>
    /// <returns>The classes of the game, or the ones of every Source 1 game where it is not one whose classes are known.</returns>
    public static EntityClassTable Get(Source1FileSystem fileSystem, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logger);

        if (BuiltIn.Value.Source1Games.TryGetValue(fileSystem.SteamAppId, out var table))
        {
            logger.LogInformation("Entities are shown with the {Count} classes of Steam app {AppId}", table.Classes.Count, fileSystem.SteamAppId);
            return table;
        }

        logger.LogInformation("The entity classes of the game are not known, entities are shown with the classes of every Source 1 game");
        return BuiltIn.Value.Source1Merged;
    }

    /// <summary>
    /// Gets the entity classes of a GoldSrc game.
    /// </summary>
    /// <param name="modDirectory">The folder of the mod, such as <c>cstrike</c>.</param>
    /// <returns>The classes of the game, or <see langword="null"/> where they are not known.</returns>
    public static EntityClassTable? GetGoldSrc(string modDirectory)
    {
        ArgumentNullException.ThrowIfNull(modDirectory);

        var appId = GoldSrcGameApps.GetAppId(modDirectory);

        return appId != 0 && BuiltIn.Value.GoldSrcGames.TryGetValue(appId, out var table) ? table : null;
    }

    private sealed record BuiltInTables(
        Dictionary<int, EntityClassTable> Source1Games,
        EntityClassTable Source1Merged,
        Dictionary<int, EntityClassTable> GoldSrcGames);

    private static BuiltInTables LoadBuiltIn()
    {
        var root = EntityClassReader.ReadResource(typeof(Source1EntityClassCatalog).Assembly, ResourceName);
        var source1Games = new Dictionary<int, EntityClassTable>();
        var goldSrcGames = new Dictionary<int, EntityClassTable>();
        var source1Classes = new List<EntityClass>();

        foreach (var game in EntityClassReader.ReadTables(root))
        {
            if (game.IsGoldSrc)
            {
                goldSrcGames.Add(game.AppId, game.Table);
                continue;
            }

            source1Games.Add(game.AppId, game.Table);
            source1Classes.AddRange(game.Table.Classes);
        }

        var source1Merged = new EntityClassTable(EntityClassReader.MergeClasses(source1Classes), null);

        return new BuiltInTables(source1Games, source1Merged, goldSrcGames);
    }
}
