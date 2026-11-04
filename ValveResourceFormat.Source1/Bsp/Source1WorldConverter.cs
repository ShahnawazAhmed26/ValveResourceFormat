using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelData;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Source1.Entities;
using ValveResourceFormat.Source1.Material;
using ValveResourceFormat.Source1.Model;
using S2Model = ValveResourceFormat.ResourceTypes.Model;
using VrfTexture = ValveResourceFormat.ResourceTypes.Texture;

namespace ValveResourceFormat.Source1.Bsp;

/// <summary>
/// The Source 2 resources of a converted Source 1 map, by the path they are served at.
/// </summary>
internal sealed class Source1ConvertedMap
{
    /// <summary>The resources by normalized path, with the <c>_c</c> suffix of compiled files.</summary>
    public Dictionary<string, Resource> Files { get; } = [];

    /// <summary>The path of the map resource, which names the world beside it.</summary>
    public required string MapPath { get; init; }

    /// <summary>The entities as they were converted, in the order of the entity lump, followed by the cubemaps of the map.</summary>
    public required List<EntityLump.Entity> Entities { get; init; }

    /// <summary>The Source 1 entities, by the <c>source1_entity</c> key of the converted entities.</summary>
    public required List<BspEntity> SourceEntities { get; init; }

    /// <summary>The Source 1 entities whose ropes and sprites the world draws, which need no stand-in.</summary>
    public HashSet<BspEntity> DrawnEntities { get; } = [];

    /// <summary>The squared distances where the detail sprites start to fade and where they are gone, or null when the map has no detail props.</summary>
    public Vector2? DetailFadeSquared { get; set; }

    /// <summary>The paths of the materials of the surfaces that have a bumped lightmap.</summary>
    public required HashSet<string> BumpedMaterials { get; init; }

    /// <summary>The path of the brush model of a model of the map, <c>*N</c> in the entities.</summary>
    /// <param name="modelIndex">The index of the model in the map.</param>
    public string GetBrushModelPath(int modelIndex) => Source1WorldConverter.GetBrushModelPath(MapPath, modelIndex);
}

/// <summary>
/// Converts a Source 1 map to the resources a Source 2 world is made of: a map, a world with one world node, an entity lump,
/// and one model per brush model of the map.
/// </summary>
internal static class Source1WorldConverter
{
    private const string CompiledSuffix = "_c";
    private const string DefaultCubemapPath = "materials/engine/defaultcubemap.vtf";
    private const string SkyWorldGroup = "skybox";

    // The scale of a sky_camera that sets none, which the Source 2 sky camera would read as 1
    private const float DefaultSkyScale = 16f;

    // The lightmap version whose light store the Source 1 lights use
    private const long LightmapVersion = 8;
    private const long LightmapGameVersion = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct BrushVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 TexCoord;
        public Vector2 LightmapCoord;
        public uint Color;
    }

    private readonly record struct MaterialPiece(string Material, BspVertex[] Vertices, int[] Indices);

    /// <summary>Converts a map.</summary>
    /// <param name="bsp">The map.</param>
    /// <param name="getDecalSize">Gets the world size of an <c>infodecal</c> texture, or null when the material is unknown.</param>
    /// <param name="getMaterialInfo">Reads the texture and the sprite settings of a material, or returns null when it has no texture. Sprites and ropes of such materials are left out.</param>
    /// <param name="locator">Finds the 3D sky area of the faces of the world, or null to keep the world as one model.</param>
    /// <param name="loadModel">Loads a model of the game by path, or returns null when it has none. Props whose model is missing are left out.</param>
    /// <param name="openFile">Opens a file of the game or of the pakfile of the map, such as the baked lighting of a static prop.</param>
    /// <param name="sky">The 2D sky, which the map draws with an <c>env_sky</c>, or null when it has none.</param>
    public static Source1ConvertedMap Convert(
        BspFile bsp,
        Func<string, Source1Model?> loadModel,
        Func<string, Stream?> openFile,
        Func<string, Source1MaterialInfo?> getMaterialInfo,
        Func<string, Vector2?>? getDecalSize = null,
        IBrushLightingLocator? locator = null,
        Source1Sky? sky = null)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(loadModel);
        ArgumentNullException.ThrowIfNull(openFile);

        var mapPath = $"maps/{Source1Paths.Normalize(bsp.Name)}";
        var worldFolder = $"{mapPath}/";
        var sourceEntities = new List<BspEntity>();
        var entityLump = Source1EntityConverter.CreateLump(bsp.Name);
        var skyEntities = new List<EntityLump.Entity>();
        var entities = ConvertEntities(bsp, mapPath, entityLump, sourceEntities, locator, skyEntities, loadModel, openFile);
        var modelMeshes = BspMeshBuilder.BuildAll(bsp, getDecalSize);

        if (sky != null)
        {
            entities.Add(CreateSkyEntity(entityLump, sky.MaterialPath));
        }

        var postProcessPath = $"{mapPath}/postprocess.vpost";
        entities.Add(CreatePostProcessVolume(entityLump, postProcessPath));

        var map = new Source1ConvertedMap
        {
            MapPath = mapPath,
            Entities = entities,
            SourceEntities = sourceEntities,
            BumpedMaterials = GetBumpedMaterials(modelMeshes),
        };

        AddFile(map, $"{mapPath}.vmap", new Resource(ResourceType.Map));
        AddFile(map, postProcessPath, CreatePostProcessing());

        if (sky != null)
        {
            AddFile(map, sky.MaterialPath, sky.Material);
            AddFile(map, sky.TexturePath, sky.Texture);
        }

        var lightmapPaths = AddLightmaps(map, bsp);
        var skyProbeVolumes = new List<EntityLump.Entity>();
        Source1LightProbeBaker.Add(map, bsp, entityLump, locator, skyProbeVolumes);

        var worldParts = new List<string>();
        var skyParts = new List<string>();

        for (var i = 0; i < modelMeshes.Length; i++)
        {
            if (i == 0)
            {
                AddWorldModels(map, modelMeshes[0], locator, worldParts, skyParts);
                continue;
            }

            var path = GetBrushModelPath(mapPath, i);
            var pieces = GetPieces(modelMeshes[i]);

            AddFile(map, path, CreateBrushModel(pieces, path));
        }

        var detail = Source1DetailPropConverter.Convert(map, bsp, locator);
        worldParts.AddRange(detail.WorldParts);
        skyParts.AddRange(detail.SkyParts);

        Source1RopeConverter.Convert(map, bsp, locator, getMaterialInfo, worldParts, skyParts);
        Source1SpriteConverter.Convert(map, bsp, locator, getMaterialInfo, worldParts, skyParts);
        MarkDrawnByWorld(map, entities);

        var nodePath = $"{worldFolder}world_nodes/node001";
        var lumpPath = $"{worldFolder}entities/default_ents";

        AddFile(map, $"{worldFolder}world.vwrld", CreateWorld($"{worldFolder}world.vwrld", lumpPath, nodePath, lightmapPaths));
        var worldProps = new List<int>();
        var skyProps = new List<int>();

        for (var i = 0; i < bsp.StaticProps.Props.Count; i++)
        {
            var prop = bsp.StaticProps.Props[i];

            if (prop.ModelName.Length == 0)
            {
                continue;
            }

            if (locator?.IsInSky(prop.Origin) ?? false)
            {
                skyProps.Add(i);
            }
            else
            {
                worldProps.Add(i);
            }
        }

        var skyCamera = FindSkyCamera(entities, locator);

        if (skyCamera != null)
        {
            skyCamera["scale"] = MathF.Max(skyCamera.GetFloatProperty("scale", DefaultSkyScale), 1f);
            skyEntities.Add(skyCamera);
        }

        var worldEntities = new List<EntityLump.Entity>(entities.Count - skyEntities.Count);

        foreach (var entity in entities)
        {
            if (!skyEntities.Contains(entity))
            {
                worldEntities.Add(entity);
            }
        }

        AddFile(map, $"{nodePath}.vwnod", CreateWorldNode($"{nodePath}.vwnod", worldParts, bsp, worldProps, detail.WorldModels, loadModel, openFile));

        if (skyParts.Count > 0 || skyProps.Count > 0 || detail.SkyModels.Count > 0 || skyEntities.Count > 0)
        {
            var skyLumpEntities = GetSkyLumpEntities(entityLump, skyProbeVolumes, skyEntities, skyCamera);

            AddSkyWorld(map, bsp, skyParts, skyProps, detail.SkyModels, skyLumpEntities, lightmapPaths, loadModel, openFile);
            worldEntities.Add(CreateSkyboxReference(entityLump, map.MapPath));
        }

        AddFile(map, $"{lumpPath}.vents", CreateEntityLump($"{lumpPath}.vents", bsp.Name, worldEntities));

        return map;
    }

    // The first sky_camera is the one the engine draws the 3D sky from, when it is in an area of its own
    private static EntityLump.Entity? FindSkyCamera(List<EntityLump.Entity> entities, IBrushLightingLocator? locator)
    {
        if (locator == null)
        {
            return null;
        }

        foreach (var entity in entities)
        {
            if (entity.GetStringProperty("classname") != "sky_camera")
            {
                continue;
            }

            return locator.IsInSky(entity.GetVector3Property("origin")) ? entity : null;
        }

        return null;
    }

    // The sky world is lit by the probes of its own area and fogged by the fog of the sky_camera, which the lump of the sky
    // holds as the entities of a Source 2 sky map do
    private static List<EntityLump.Entity> GetSkyLumpEntities(
        EntityLump lump,
        List<EntityLump.Entity> skyProbeVolumes,
        List<EntityLump.Entity> skyEntities,
        EntityLump.Entity? skyCamera)
    {
        var skyLumpEntities = new List<EntityLump.Entity>(skyEntities);
        skyLumpEntities.AddRange(skyProbeVolumes);

        if (skyCamera != null && skyCamera.GetBooleanProperty("fogenable"))
        {
            skyLumpEntities.Add(CreateSkyFog(lump, skyCamera));
        }

        return skyLumpEntities;
    }

    private static EntityLump.Entity CreateSkyFog(EntityLump lump, EntityLump.Entity skyCamera)
    {
        var fog = new EntityLump.Entity { ParentLump = lump };
        fog["classname"] = "env_fog_controller";
        fog["fogcolor"] = "255 255 255";
        fog["fogcolor2"] = "255 255 255";
        fog["fogstart"] = 500f;
        fog["fogend"] = 2000f;

        string[] keys = ["fogenable", "fogcolor", "fogcolor2", "fogstart", "fogend", "fogmaxdensity", "fogblend", "fogdir", "use_angles", "angles"];

        foreach (var key in keys)
        {
            if (skyCamera.TryGetValue(key, out var value))
            {
                fog[key] = value;
            }
        }

        SetFogsObjects(fog);

        return fog;
    }

    // The map the reference places is the sky world, in a world group of its own that is drawn as the 3D sky. The sky world
    // keeps the coordinates of the map, so the reference sits at the origin and the sky_camera is seen from its own position.
    private static EntityLump.Entity CreateSkyboxReference(EntityLump lump, string mapPath)
    {
        var reference = new EntityLump.Entity { ParentLump = lump };
        reference["classname"] = "skybox_reference";
        reference["origin"] = "0 0 0";
        reference["targetmapname"] = $"{mapPath}/sky";
        reference["worldgroupid"] = SkyWorldGroup;

        return reference;
    }

    // The 3D sky box area is a world of its own, which the map does not list, that the sky reference places
    private static void AddSkyWorld(
        Source1ConvertedMap map,
        BspFile bsp,
        List<string> parts,
        List<int> propIndices,
        List<Source1DetailPropConverter.DetailModel> detailModels,
        List<EntityLump.Entity> entities,
        List<string> lightmapPaths,
        Func<string, Source1Model?> loadModel,
        Func<string, Stream?> openFile)
    {
        var skyFolder = $"{map.MapPath}/sky/";
        var worldPath = $"{skyFolder}world.vwrld";
        var nodePath = $"{skyFolder}world_nodes/node001";
        var lumpPath = $"{skyFolder}entities/default_ents";

        AddFile(map, $"{map.MapPath}/sky.vmap", new Resource(ResourceType.Map));
        AddFile(map, worldPath, CreateWorld(worldPath, lumpPath, nodePath, lightmapPaths));
        AddFile(map, $"{nodePath}.vwnod", CreateWorldNode($"{nodePath}.vwnod", parts, bsp, propIndices, detailModels, loadModel, openFile));
        AddFile(map, $"{lumpPath}.vents", CreateEntityLump($"{lumpPath}.vents", $"{bsp.Name}_sky", entities));
    }

    private static HashSet<string> GetBumpedMaterials(BspModelMesh[] modelMeshes)
    {
        var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in modelMeshes)
        {
            foreach (var (name, mesh) in model.Materials)
            {
                if ((mesh.Flags & BspMeshFlags.Bumped) != 0)
                {
                    materials.Add(Source1Paths.ToMaterialPath(name));
                }
            }
        }

        return materials;
    }

    // The flat lightmap and the three bump basis lightmaps are textures of their own, named for the samplers that read them
    private static List<string> AddLightmaps(Source1ConvertedMap map, BspFile bsp)
    {
        var atlas = bsp.LightmapLayout.BuildAtlas();
        var paths = new List<string>(LightmapAtlas.PageCount);

        for (var page = 0; page < atlas.Pages.Length; page++)
        {
            var name = page == 0
                ? "lightmap"
                : string.Create(CultureInfo.InvariantCulture, $"lightmap_bump{page}");
            var path = $"{map.MapPath}/lightmaps/{name}.vtex";
            var pixels = MemoryMarshal.AsBytes(atlas.Pages[page].AsSpan()).ToArray();

            var texture = VrfTexture.Create((ushort)atlas.Width, (ushort)atlas.Height, VTexFormat.RGBA16161616F, 0, Vector4.One, [pixels]);

            AddFile(map, path, texture);
            paths.Add(path);
        }

        return paths;
    }

    internal static string GetBrushModelPath(string mapPath, int modelIndex, int part = 0)
    {
        var name = mapPath[(mapPath.LastIndexOf('/') + 1)..];

        if (part == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{mapPath}/models/{name}_c0_{modelIndex}.vmdl");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{mapPath}/models/{name}_c0_{modelIndex}_{part}.vmdl");
    }

    internal static void AddFile(Source1ConvertedMap map, string path, Resource resource)
    {
        resource.FileName = path + CompiledSuffix;
        map.Files.Add(resource.FileName, resource);
    }

    private static List<MaterialPiece> GetPieces(BspModelMesh model)
    {
        var pieces = new List<MaterialPiece>();

        foreach (var (materialName, materialMesh) in model.Materials)
        {
            if (materialMesh.Indices.Length == 0 || materialName.StartsWith("tools/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pieces.Add(new MaterialPiece(materialName, materialMesh.Vertices, materialMesh.Indices));
        }

        return pieces;
    }

    // The faces of the world model inside the area of the 3D sky box become a model of their own, so that
    // they can be drawn apart from the rest
    private static void AddWorldModels(
        Source1ConvertedMap map,
        BspModelMesh world,
        IBrushLightingLocator? locator,
        List<string> worldParts,
        List<string> skyParts)
    {
        var groups = new Dictionary<bool, List<MaterialPiece>>();

        foreach (var piece in GetPieces(world))
        {
            var triangles = new Dictionary<bool, List<int>>();

            for (var i = 0; i + 2 < piece.Indices.Length; i += 3)
            {
                var a = piece.Vertices[piece.Indices[i]];
                var b = piece.Vertices[piece.Indices[i + 1]];
                var c = piece.Vertices[piece.Indices[i + 2]];
                var center = (a.Position + b.Position + c.Position) / 3f;
                var inSky = locator?.IsInSky(center) ?? false;

                if (!triangles.TryGetValue(inSky, out var starts))
                {
                    starts = [];
                    triangles.Add(inSky, starts);
                }

                starts.Add(i);
            }

            foreach (var (inSky, starts) in triangles)
            {
                if (!groups.TryGetValue(inSky, out var pieces))
                {
                    pieces = [];
                    groups.Add(inSky, pieces);
                }

                pieces.Add(CreateSubPiece(piece, starts));
            }
        }

        foreach (var overlay in world.Overlays)
        {
            if (overlay.Vertices.Length == 0)
            {
                continue;
            }

            var inSky = locator?.IsInSky(overlay.Vertices[0].Position) ?? false;
            var materialPath = Source1Paths.ToMaterialPath(overlay.Material);
            var overlayMaterial = Source1Paths.AddVariant(materialPath, Source1ResourceProvider.OverlayVariant);

            if (!groups.TryGetValue(inSky, out var pieces))
            {
                pieces = [];
                groups.Add(inSky, pieces);
            }

            pieces.Add(new MaterialPiece(overlayMaterial, overlay.Vertices, overlay.Indices));
        }

        var part = 0;

        foreach (var (inSky, pieces) in groups)
        {
            var path = GetBrushModelPath(map.MapPath, 0, part);

            AddFile(map, path, CreateBrushModel(pieces, path));

            if (inSky)
            {
                skyParts.Add(path);
            }
            else
            {
                worldParts.Add(path);
            }

            part++;
        }
    }

    private static MaterialPiece CreateSubPiece(MaterialPiece source, List<int> triangleStarts)
    {
        var vertices = new List<BspVertex>();
        var indices = new List<int>(triangleStarts.Count * 3);
        var remap = new Dictionary<int, int>();

        foreach (var start in triangleStarts)
        {
            for (var corner = 0; corner < 3; corner++)
            {
                var original = source.Indices[start + corner];

                if (!remap.TryGetValue(original, out var mapped))
                {
                    mapped = vertices.Count;
                    remap.Add(original, mapped);
                    vertices.Add(source.Vertices[original]);
                }

                indices.Add(mapped);
            }
        }

        return new MaterialPiece(source.Material, [.. vertices], [.. indices]);
    }

    private static List<EntityLump.Entity> ConvertEntities(
        BspFile bsp,
        string mapPath,
        EntityLump lump,
        List<BspEntity> sources,
        IBrushLightingLocator? locator,
        List<EntityLump.Entity> skyEntities,
        Func<string, Source1Model?> loadModel,
        Func<string, Stream?> openFile)
    {
        var options = new Source1EntityConverterOptions { HdrLighting = bsp.HasHdrLighting };
        var entities = new List<EntityLump.Entity>(bsp.Entities.Count);
        var entityModels = new Source1EntityModels(openFile, bsp.Name, bsp.Entities);

        foreach (var source in bsp.Entities)
        {
            if (Source1EntityConverter.Convert(source, lump, options) is not { } entity)
            {
                continue;
            }

            var brushModelIndex = ConvertModel(bsp, mapPath, source, entity);
            var isInSky = false;

            if (brushModelIndex > 0)
            {
                isInSky = IsBrushEntityInSky(bsp, source, brushModelIndex, locator);
            }
            else if (ConvertEntityModel(entityModels, source, entity, loadModel))
            {
                isInSky = locator?.IsInSky(source.Origin) ?? false;
            }

            if (isInSky)
            {
                skyEntities.Add(entity);
            }

            SetTonemapDefaults(entity);
            SetFogsObjects(entity);
            SetParticlePath(source, entity);

            entity["source1_entity"] = (long)sources.Count;
            sources.Add(source);
            entities.Add(entity);
        }

        AddCubemaps(bsp, lump, entities);

        return entities;
    }

    // The map compiler keeps no env_cubemap entities, only the samples it baked. A model takes the nearest sample, so the
    // influence of each reaches every point of the world, and the nearest one wins among the overlapping probes. A map
    // without samples reflects the default cube map of the engine everywhere.
    private static void AddCubemaps(BspFile bsp, EntityLump lump, List<EntityLump.Entity> entities)
    {
        if (bsp.Models.Length == 0)
        {
            return;
        }

        var world = bsp.Models[0];
        var radius = (world.Max - world.Min).Length();

        if (bsp.Cubemaps.Length == 0)
        {
            var center = (world.Min + world.Max) * 0.5f;
            entities.Add(CreateCubemap(lump, center, DefaultCubemapPath, radius));
            return;
        }

        var extension = bsp.HasHdrLighting ? ".hdr.vtf" : ".vtf";

        foreach (var sample in bsp.Cubemaps)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"c{sample.OriginX}_{sample.OriginY}_{sample.OriginZ}");
            var texture = $"materials/maps/{bsp.Name}/{name}{extension}";

            entities.Add(CreateCubemap(lump, sample.Origin, texture, radius));
        }
    }

    private static EntityLump.Entity CreateCubemap(EntityLump lump, Vector3 origin, string texture, float radius)
    {
        var entity = new EntityLump.Entity { ParentLump = lump };
        entity["classname"] = "env_cubemap";
        entity["origin"] = string.Create(CultureInfo.InvariantCulture, $"{origin.X} {origin.Y} {origin.Z}");
        entity["cubemaptexture"] = texture;
        entity["influenceradius"] = radius;

        return entity;
    }

    private static EntityLump.Entity CreateSkyEntity(EntityLump lump, string material)
    {
        var entity = new EntityLump.Entity { ParentLump = lump };
        entity["classname"] = "env_sky";
        entity["skyname"] = material;
        entity["fog_type"] = 0L;

        return entity;
    }

    // Brush entities draw the brush model of the map, which is hidden when the entity starts hidden. The sprite and
    // material of other entities are drawn by their own builders, and their model is set by ConvertEntityModel.
    // Returns the index of the brush model the entity draws, or 0 when it draws none.
    private static int ConvertModel(BspFile bsp, string mapPath, BspEntity source, EntityLump.Entity entity)
    {
        var model = source.GetString("model");

        if (model.Length == 0)
        {
            return 0;
        }

        if (model[0] == '*')
        {
            var isBrushModel = int.TryParse(model.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index > 0
                && index < bsp.Models.Length;

            if (!isBrushModel || Source1EntityConverter.IsHiddenAtSpawn(source))
            {
                entity["model"] = string.Empty;
                return 0;
            }

            entity["model"] = GetBrushModelPath(mapPath, index);
            return index;
        }

        var isSource1Model = model.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
            || model.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase)
            || model.EndsWith(".spr", StringComparison.OrdinalIgnoreCase);

        if (isSource1Model)
        {
            entity["model"] = string.Empty;
        }

        return 0;
    }

    // A brush entity is placed by its entity transform, so it is in the 3D sky box area when the center of its model is
    private static bool IsBrushEntityInSky(BspFile bsp, BspEntity source, int modelIndex, IBrushLightingLocator? locator)
    {
        if (locator == null)
        {
            return false;
        }

        var bounds = bsp.Models[modelIndex];
        var transform = EntityTransformHelper.ToRigidTransformationMatrix(source.Angles, source.Origin);
        var center = Vector3.Transform((bounds.Min + bounds.Max) * 0.5f, transform);

        return locator.IsInSky(center);
    }

    // The entity classes load an effect by the path in its key, and the provider serves a particle system by that path
    private static void SetParticlePath(BspEntity source, EntityLump.Entity entity)
    {
        if (!source.ClassName.Equals("info_particle_system", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var name = source.GetString("effect_name");

        if (!string.IsNullOrEmpty(name))
        {
            entity["effect_name"] = Source1ResourceProvider.GetParticlePath(name);
        }
    }

    // A Source 1 tonemap controller sets its exposure range through inputs, so without keys it would clamp the exposure to zero
    private static void SetTonemapDefaults(EntityLump.Entity entity)
    {
        if (entity.GetStringProperty("classname") != "env_tonemap_controller" || entity.ContainsKey("maxexposure"))
        {
            return;
        }

        entity["minexposure"] = 0.5f;
        entity["maxexposure"] = 2f;
        entity["rate"] = 1f;
    }

    // The fog of a fog controller covers everything the game draws, not only the 2D sky
    private static void SetFogsObjects(EntityLump.Entity entity)
    {
        if (entity.GetStringProperty("classname") != "env_fog_controller")
        {
            return;
        }

        var start = entity.GetFloatProperty("fogstart");
        var end = entity.GetFloatProperty("fogend");

        if (end > start)
        {
            entity["fogobjects"] = true;
        }

        SetFogCurve(entity);
    }

    // Source 1 fogs a pixel by the square of min(maxdensity, saturate((distance - start) / (end - start))). That is
    // the Source 2 fog curve raised to the power 2, once the density cap moves into the end distance and the maximum
    // density is squared.
    private static void SetFogCurve(EntityLump.Entity entity)
    {
        var start = entity.GetFloatProperty("fogstart");
        var end = entity.GetFloatProperty("fogend");
        var maxDensity = Math.Clamp(entity.GetFloatProperty("fogmaxdensity", 1f), 0f, 1f);

        if (end > start && maxDensity > 0f)
        {
            entity["fogend"] = start + (end - start) * maxDensity;
        }

        entity["fogmaxdensity"] = maxDensity * maxDensity;
        entity["fogexponent"] = 2f;
    }

    // The world draws the ropes and sprites of these entities, so a tool draws no stand-in for them
    private static void MarkDrawnByWorld(Source1ConvertedMap map, List<EntityLump.Entity> entities)
    {
        foreach (var entity in entities)
        {
            var index = entity.GetIntegerProperty("source1_entity", -1L);

            if (index < 0 || index >= map.SourceEntities.Count)
            {
                continue;
            }

            if (map.DrawnEntities.Contains(map.SourceEntities[(int)index]))
            {
                entity["drawnbyworld"] = true;
            }
        }
    }

    // The game code sets the model of characters, pickups and weapons, and props take it from their model key. The class of
    // the entity draws it, in the pose of the sequence the entity starts with. Returns whether the entity has a model.
    private static bool ConvertEntityModel(Source1EntityModels entityModels, BspEntity source, EntityLump.Entity entity, Func<string, Source1Model?> loadModel)
    {
        if (Source1EntityConverter.IsHiddenAtSpawn(source) || entityModels.IsSpawnedLater(source))
        {
            return false;
        }

        if (entityModels.GetModel(source) is not { } entityModel)
        {
            return false;
        }

        var model = loadModel(entityModel.Path);

        if (model == null)
        {
            return false;
        }

        entity["model"] = entityModel.Path;
        entity["skin"] = GetSkinName(model, entityModel.Skin);
        entity["model_state"] = EntityLump.StringifyValue(CreateModelState(model, entityModel));

        // The body value picks a sub-model of every body group, which the model state holds by name
        entity.Remove("body");

        var sequence = FindStartSequence(model, source, entityModel);

        if (sequence != null)
        {
            entity["defaultanim"] = sequence;
        }

        return true;
    }

    // The converted model names its skin families by index, and an index the model does not have shows the first family
    private static string GetSkinName(Source1Model model, int skin)
    {
        var skinCount = Math.Max(1, model.SkinFamilies.Length);
        var index = (uint)skin < (uint)skinCount ? skin : 0;

        return string.Create(CultureInfo.InvariantCulture, $"skin{index}");
    }

    private static KVObject CreateModelState(Source1Model model, Source1EntityModel entityModel)
    {
        var choices = new int[model.BodyParts.Length];

        for (var i = 0; i < model.BodyParts.Length; i++)
        {
            var part = model.BodyParts[i];

            if (part.Base > 0 && part.Models.Length > 0)
            {
                choices[i] = entityModel.Body / part.Base % part.Models.Length;
            }
        }

        foreach (var (group, subModel) in entityModel.BodyGroups)
        {
            if ((uint)group < (uint)choices.Length && (uint)subModel < (uint)model.BodyParts[group].Models.Length)
            {
                choices[group] = subModel;
            }
        }

        var bodyGroups = KVObject.Collection();
        var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The game finds a body group by name and takes the first one that matches
        for (var i = 0; i < choices.Length; i++)
        {
            var name = model.BodyParts[i].Name;

            if (addedNames.Add(name))
            {
                bodyGroups.Add(name, (long)choices[i]);
            }
        }

        var state = KVObject.Collection();
        state.Add("body_groups", bodyGroups);

        return state;
    }

    // The sequence the entity names, then the one its game code starts with, then the sequence of its activity, and an
    // entity that names no animation keeps the sequence index of its sequence key, which is 0 when it has none
    private static string? FindStartSequence(Source1Model model, BspEntity source, Source1EntityModel entityModel)
    {
        var named = FindSequence(model, source.GetString("DefaultAnim")) ?? FindSequence(model, entityModel.Sequence);

        if (named != null)
        {
            return named;
        }

        if (entityModel.Activity != null && FindSequenceByActivity(model, entityModel.Activity) is { } activitySequence)
        {
            return activitySequence;
        }

        var index = source.GetInt("sequence");

        if ((uint)index >= (uint)model.Sequences.Length)
        {
            return null;
        }

        return model.Sequences[index].Name;
    }

    private static string? FindSequence(Source1Model model, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (var sequence in model.Sequences)
        {
            if (sequence.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return sequence.Name;
            }
        }

        return null;
    }

    // The game picks the sequence of an activity with the highest weight most often
    private static string? FindSequenceByActivity(Source1Model model, string activityName)
    {
        Source1Sequence? best = null;

        foreach (var sequence in model.Sequences)
        {
            var isActivity = sequence.ActivityName.Equals(activityName, StringComparison.OrdinalIgnoreCase);

            if (sequence.IsDelta || !isActivity || !sequence.IsPlayable)
            {
                continue;
            }

            if (best == null || sequence.ActivityWeight > best.ActivityWeight)
            {
                best = sequence;
            }
        }

        return best?.Name;
    }

    // The game draws a map with bloom and its own tonemap, which a master volume of the map sets
    private static EntityLump.Entity CreatePostProcessVolume(EntityLump lump, string postProcessPath)
    {
        var entity = new EntityLump.Entity { ParentLump = lump };
        entity["classname"] = "post_processing_volume";
        entity["origin"] = "0 0 0";
        entity["master"] = true;
        entity["postprocessing"] = postProcessPath;

        return entity;
    }

    // The game scales the scene by its exposure and clamps it, with no curve, which the nearly straight curve of these
    // tonemap parameters stands in for. The bloom parameters are left out, so the renderer applies its defaults.
    private static Resource CreatePostProcessing()
    {
        var tonemap = KVObject.Collection();
        tonemap.Add("m_flExposureBias", 0f);
        tonemap.Add("m_flShoulderStrength", 0f);
        tonemap.Add("m_flLinearStrength", 0.001f);
        tonemap.Add("m_flLinearAngle", 0.001f);
        tonemap.Add("m_flToeStrength", 1f);
        tonemap.Add("m_flToeNum", 1f);
        tonemap.Add("m_flToeDenom", 1f);
        tonemap.Add("m_flWhitePoint", 1.5f);

        var data = KVObject.Collection();
        data.Add("m_bHasTonemapParams", true);
        data.Add("m_toneMapParams", tonemap);
        data.Add("m_bHasColorCorrection", false);
        data.Add("m_bHasBloomParams", true);
        data.Add("m_bloomParams", KVObject.Collection());

        var resource = new Resource(ResourceType.PostProcessing);
        resource.UnparsedBlocks.Add(new PostProcessing { Resource = resource, Data = data });

        return resource;
    }

    private static Resource CreateWorld(string path, string lumpPath, string nodePath, List<string> lightmapPaths)
    {
        var resource = new Resource(ResourceType.World) { FileName = path };
        var lumps = KVObject.Array();
        lumps.Add($"{lumpPath}.vents");

        var node = KVObject.Collection();
        node.Add("m_worldNodePrefix", nodePath);

        var nodes = KVObject.Array();
        nodes.Add(node);

        var lightmaps = KVObject.Array();

        foreach (var lightmapPath in lightmapPaths)
        {
            lightmaps.Add(lightmapPath);
        }

        var lighting = KVObject.Collection();
        lighting.Add("m_nLightmapVersionNumber", LightmapVersion);
        lighting.Add("m_nLightmapGameVersionNumber", LightmapGameVersion);
        lighting.Add("m_lightMaps", lightmaps);

        var data = KVObject.Collection();
        data.Add("m_entityLumps", lumps);
        data.Add("m_worldNodes", nodes);
        data.Add("m_worldLightingInfo", lighting);

        resource.UnparsedBlocks.Add(new World { Resource = resource, Data = data });

        return resource;
    }

    private static Resource CreateWorldNode(
        string path,
        List<string> parts,
        BspFile bsp,
        List<int> propIndices,
        List<Source1DetailPropConverter.DetailModel> detailModels,
        Func<string, Source1Model?> loadModel,
        Func<string, Stream?> openFile)
    {
        var resource = new Resource(ResourceType.WorldNode) { FileName = path };
        var sceneObjects = KVObject.Array();

        foreach (var part in parts)
        {
            var tint = KVObject.Array();

            for (var i = 0; i < 4; i++)
            {
                tint.Add(1f);
            }

            var sceneObject = KVObject.Collection();
            sceneObject.Add("m_renderableModel", part);
            sceneObject.Add("m_vTransform", ToColumns(Matrix4x4.Identity));
            sceneObject.Add("m_nObjectTypeFlags", 0L);
            sceneObject.Add("m_vTintColor", tint);
            sceneObjects.Add(sceneObject);
        }

        var props = new Source1StaticPropConverter(bsp, loadModel, openFile, sceneObjects);

        foreach (var propIndex in propIndices)
        {
            props.Add(propIndex);
        }

        foreach (var detailModel in detailModels)
        {
            props.AddDetailModel(detailModel.ModelName, detailModel.Transform);
        }

        var data = KVObject.Collection();
        data.Add("m_sceneObjects", sceneObjects);
        data.Add("m_layerNames", KVObject.Array());

        if (props.ExtraVertexStreams.Count > 0)
        {
            data.Add("m_extraVertexStreams", props.ExtraVertexStreams);
            data.Add("m_extraVertexStreamOverrides", props.ExtraVertexStreamOverrides);
        }

        resource.UnparsedBlocks.Add(new WorldNode { Resource = resource, Data = data });

        return resource;
    }

    // The transform of a scene object is four columns of four floats each
    internal static KVObject ToColumns(Matrix4x4 value)
    {
        var columns = KVObject.Array();
        var flat = Source1ModelConverter.ToArray(value);

        for (var column = 0; column < 4; column++)
        {
            var values = KVObject.Array();

            for (var row = 0; row < 4; row++)
            {
                values.Add(flat.GetElement<float>((column * 4) + row));
            }

            columns.Add(values);
        }

        return columns;
    }

    private static Resource CreateEntityLump(string path, string name, List<EntityLump.Entity> entities)
    {
        var lump = Source1EntityConverter.CreateLump(name);
        var resource = lump.Resource!;
        var keyValues = KVObject.Array();

        foreach (var entity in entities)
        {
            var values = KVObject.Collection();
            values.Add("version", 1L);
            values.Add("values", entity);

            var connections = KVObject.Array();

            foreach (var connection in entity.Connections ?? [])
            {
                var converted = KVObject.Collection();
                converted.Add("m_targetName", connection.TargetName);
                converted.Add("m_outputName", connection.OutputName);
                converted.Add("m_inputName", connection.InputName);
                converted.Add("m_overrideParam", connection.OverrideParam);
                converted.Add("m_flDelay", connection.Delay);
                converted.Add("m_nTimesToFire", (long)connection.TimesToFire);
                converted.Add("m_targetType", (long)connection.TargetType);
                connections.Add(converted);
            }

            var item = KVObject.Collection();
            item.Add("keyValues3Data", values);
            item.Add("m_connections", connections);
            keyValues.Add(item);
        }

        lump.Data.Remove("m_entityKeyValues");
        lump.Data.Add("m_entityKeyValues", keyValues);
        resource.FileName = path;
        resource.UnparsedBlocks.Add(lump);

        return resource;
    }

    private static Resource CreateBrushModel(List<MaterialPiece> pieces, string path)
    {
        var vertices = new List<BrushVertex>();
        var indices = new List<int>();
        var drawCalls = new List<MeshDrawCall>();
        var materials = new List<string>();

        foreach (var piece in pieces)
        {
            var material = Source1Paths.ToMaterialPath(piece.Material);
            var drawCall = new MeshDrawCall(material, vertices.Count, piece.Vertices.Length, indices.Count, piece.Indices.Length);

            if (!materials.Contains(material, StringComparer.OrdinalIgnoreCase))
            {
                materials.Add(material);
            }

            foreach (var vertex in piece.Vertices)
            {
                vertices.Add(CreateVertex(vertex));
            }

            indices.AddRange(piece.Indices);
            drawCalls.Add(drawCall);
        }

        var vertexArray = vertices.ToArray();
        var (min, max) = GetBounds(vertexArray);
        var buffers = CreateVbib(vertexArray, indices.ToArray());
        var mesh = new ModelMeshSource("brush", buffers, drawCalls, min, max) { MeshGroupMask = 1UL };
        var materialGroups = new List<ModelMaterialGroup> { new("skin0", materials) };

        return S2Model.Create(path, [mesh], [], materialGroups, ["brush"], 1UL);
    }

    private static (Vector3 Min, Vector3 Max) GetBounds(BrushVertex[] vertices)
    {
        if (vertices.Length == 0)
        {
            return (Vector3.Zero, Vector3.Zero);
        }

        var min = vertices[0].Position;
        var max = min;

        foreach (var vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        return (min, max);
    }

    private static BrushVertex CreateVertex(in BspVertex vertex)
    {
        var alpha = (byte)MathF.Round(Math.Clamp(vertex.DisplacementAlpha, 0f, 1f) * 255f);

        return new BrushVertex
        {
            Position = vertex.Position,
            Normal = vertex.Normal,
            Tangent = new Vector4(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z, -vertex.Tangent.W),
            TexCoord = vertex.TexCoord,
            LightmapCoord = vertex.LightmapCoord,
            Color = 0x00FFFFFFu | ((uint)alpha << 24),
        };
    }

    private static VBIB CreateVbib(BrushVertex[] vertices, int[] indices)
    {
        var vbib = new VBIB { Resource = null! };

        vbib.VertexBuffers.Add(new VBIB.OnDiskBufferData
        {
            ElementCount = (uint)vertices.Length,
            ElementSizeInBytes = (uint)Marshal.SizeOf<BrushVertex>(),
            Data = MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray(),
            InputLayoutFields =
            [
                CreateField("POSITION", DXGI_FORMAT.R32G32B32_FLOAT, nameof(BrushVertex.Position)),
                CreateField("NORMAL", DXGI_FORMAT.R32G32B32_FLOAT, nameof(BrushVertex.Normal)),
                CreateField("TANGENT", DXGI_FORMAT.R32G32B32A32_FLOAT, nameof(BrushVertex.Tangent)),
                CreateField("TEXCOORD", DXGI_FORMAT.R32G32_FLOAT, nameof(BrushVertex.TexCoord)),
                CreateLightmapField(),
                CreateField("COLOR", DXGI_FORMAT.R8G8B8A8_UNORM, nameof(BrushVertex.Color)),
            ],
        });

        vbib.IndexBuffers.Add(new VBIB.OnDiskBufferData
        {
            ElementCount = (uint)indices.Length,
            ElementSizeInBytes = sizeof(int),
            Data = MemoryMarshal.AsBytes(indices.AsSpan()).ToArray(),
            InputLayoutFields = [],
        });

        return vbib;
    }

    private static VBIB.RenderInputLayoutField CreateField(string semantic, DXGI_FORMAT format, string fieldName)
    {
        var offset = (uint)Marshal.OffsetOf<BrushVertex>(fieldName);

        return new VBIB.RenderInputLayoutField(semantic, format, offset);
    }

    // No semantic names the lightmap coordinates, so the field names the shader input directly
    private static VBIB.RenderInputLayoutField CreateLightmapField()
    {
        var field = CreateField("LIGHTMAPUV", DXGI_FORMAT.R32G32_FLOAT, nameof(BrushVertex.LightmapCoord));
        field.ShaderSemantic = "vLightmapUV";

        return field;
    }
}
