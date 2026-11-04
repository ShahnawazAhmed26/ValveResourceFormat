using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using ValveResourceFormat.IO;
using ValveResourceFormat.IO.Fgd;
using ValveResourceFormat.Source1.Bsp;
using ValveResourceFormat.Source1.Entities;
using ValveResourceFormat.Source1.FileSystem;
using ValveResourceFormat.Source1.Material;
using ValveResourceFormat.Source1.Model;
using ValveResourceFormat.Source1.Particles;
using ValveResourceFormat.Source1.Texture;
using VrfTexture = ValveResourceFormat.ResourceTypes.Texture;

namespace ValveResourceFormat.Source1;

/// <summary>
/// Serves the files of a Source 1 game as Source 2 resources: a <c>.vmt</c> as a material and a <c>.vtf</c> as a texture.
/// The paths may carry the <c>_c</c> suffix of compiled files.
/// </summary>
internal sealed class Source1ResourceProvider(Source1FileSystem fileSystem, ILogger logger) : IResourceProvider
{
    private const string MaterialExtension = ".vmt";
    private const string ModelExtension = ".mdl";
    private const string TextureExtension = ".vtf";
    private const string HdrTextureExtension = ".hdr.vtf";
    private const string ParticleFolder = "particles/";
    private const string ParticleExtension = ".vpcf";
    private const string Source2ModelExtension = ".vmdl";
    private const string Source2MaterialExtension = ".vmat";

    private readonly Source1ParticleLibrary particles = new(fileSystem, logger);

    /// <summary>The variant name of a material that is drawn as an editor icon.</summary>
    public const string ToolSpriteVariant = "tools_sprite";

    /// <summary>The variant name of a material that is drawn on top of the surface under it.</summary>
    public const string OverlayVariant = "overlay";

    /// <summary>The variant name of a material that is drawn as a detail sprite, which fades with the distance.</summary>
    public const string DetailSpriteVariant = "detailsprite";

    /// <summary>The variant name of a material that is drawn as the ribbon of a rope.</summary>
    public const string CableVariant = "cable";

    private const string SpriteVariantPrefix = "sprite";

    /// <summary>Gets the variant name of a material that is drawn as the sprite of an entity of a render mode.</summary>
    /// <param name="renderMode">The render mode of the entity.</param>
    internal static string GetSpriteVariant(RenderMode renderMode)
        => string.Create(CultureInfo.InvariantCulture, $"{SpriteVariantPrefix}{(int)renderMode}");

    /// <summary>
    /// Whether materials are loaded in high dynamic range. It has to be set before the materials of a map are loaded.
    /// </summary>
    public bool HighDynamicRange { get; set; } = true;

    private Source1ConvertedMap? map;

    /// <summary>
    /// Mounts the pakfile of a map, so its files win over the game's, then converts the map and serves its map, world,
    /// world node, entity lump and brush models by path. Materials load in the dynamic range the map was lit in.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <returns>The converted map.</returns>
    public Source1ConvertedMap LoadMap(BspFile bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);

#pragma warning disable CA2000 // Disposed with the file system that owns the archive
        if (BspPakFileReader.Open(bsp) is { } pakFile)
#pragma warning restore CA2000
        {
            fileSystem.AddArchive(pakFile, ownsArchive: true);
        }

        // Lump files may come from the pakfile, so they are read after it is mounted
        bsp.ApplyLumpFiles(fileSystem);

        HighDynamicRange = bsp.HasHdrLighting;
        particles.SetMap(bsp.Name);

        var worldspawn = bsp.Entities.FirstOrDefault(static entity => entity.ClassName == "worldspawn");
        var skyName = worldspawn?.GetString("skyname") ?? string.Empty;
        var sky = Source1SkyConverter.Create(fileSystem, skyName, bsp.HasHdrLighting, logger);
        var locator = new Source1MapLighting(bsp);

        map = Source1WorldConverter.Convert(bsp, path => LoadModel(path)?.Source, fileSystem.OpenFile, GetMaterialInfo, GetDecalSize, locator, sky);

        return map;
    }

    /// <summary>A model of the game and its conversion.</summary>
    /// <param name="Source">The model as the game has it.</param>
    /// <param name="Model">The converted model, which is also served by the path of the model.</param>
    internal sealed record ConvertedModel(Source1Model Source, ResourceTypes.Model Model);

    private readonly Dictionary<string, ConvertedModel?> models = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Converts a model of the game to a Source 2 model once, for everything that draws it.</summary>
    /// <param name="path">The path of the model, such as <c>models/props/crate.mdl</c>. It names the converted model, and so its meshes.</param>
    /// <returns>The converted model, or <see langword="null"/> when the game has none of that name or it does not load.</returns>
    internal ConvertedModel? LoadModel(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (models)
        {
            if (models.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var converted = ConvertModel(path);
            models[path] = converted;

            return converted;
        }
    }

    private ConvertedModel? ConvertModel(string path)
    {
        if (!fileSystem.Exists(path))
        {
            return null;
        }

        try
        {
            var source = Source1Model.Load(path, fileSystem.OpenFile);
            var model = Source1ModelConverter.Convert(source, path, fileSystem.Exists);

            return new ConvertedModel(source, model);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException or UnexpectedMagicException)
        {
            logger.LogWarning("Failed to load model {Model}: {Message}", path, e.Message);
            return null;
        }
    }

    /// <summary>Gets the world size of an <c>infodecal</c> material.</summary>
    /// <param name="name">The name of the material.</param>
    /// <returns>The size, or <see langword="null"/> when the material or its texture is unknown.</returns>
    public Vector2? GetDecalSize(string name)
    {
        try
        {
            var vmt = VmtFile.Load(Source1Paths.ToMaterialPath(name), fileSystem.OpenFile);

            if (Source1TextureHeaders.TryReadBaseTexture(fileSystem, vmt) is not { } header)
            {
                return null;
            }

            var scale = vmt.Values.Float("$decalscale", 1f);

            return new Vector2(header.Width, header.Height) * scale;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>Reads what the sprites and ropes drawn with a material need to know about it.</summary>
    /// <param name="name">The name of the material.</param>
    /// <returns>The info, or <see langword="null"/> when the material or its base texture is unknown.</returns>
    internal Source1MaterialInfo? GetMaterialInfo(string name)
    {
        try
        {
            var vmt = VmtFile.Load(Source1Paths.ToMaterialPath(name), fileSystem.OpenFile);

            if (Source1TextureHeaders.TryReadBaseTexture(fileSystem, vmt) is not { } header)
            {
                return null;
            }

            var size = new Vector2(header.Width, header.Height);
            var origin = vmt.Values.Pair("$spriteorigin", new Vector2(0.5f));

            return new Source1MaterialInfo(size, vmt.Values.String("$spriteorientation"), origin);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the path a particle system is served by. Source 1 has no <c>.vpcf</c> files, so the path cannot name a file of the game.
    /// The name is escaped, as it may hold characters a path cannot.
    /// </summary>
    /// <param name="name">The name of the system.</param>
    /// <returns>The path, such as <c>particles/teleporter_blue.vpcf</c>.</returns>
    internal static string GetParticlePath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return string.Concat(ParticleFolder, Uri.EscapeDataString(name), ParticleExtension);
    }

    private static bool TryGetParticleName(string path, out string name)
    {
        name = string.Empty;

        if (!path.StartsWith(ParticleFolder, StringComparison.Ordinal) || !path.EndsWith(ParticleExtension, StringComparison.Ordinal))
        {
            return false;
        }

        var escaped = path[ParticleFolder.Length..^ParticleExtension.Length];
        name = Uri.UnescapeDataString(escaped);

        return true;
    }

    private static string RemoveCompiledSuffix(string file)
    {
        var path = Source1Paths.Normalize(file);

        if (path.EndsWith(GameFileLoader.CompiledFileSuffix, StringComparison.Ordinal))
        {
            return path[..^GameFileLoader.CompiledFileSuffix.Length];
        }

        return path;
    }

    // Source 2 names of what the game has as a model and a material: the model of the same name, and the material of
    // the same name drawn as an editor icon
    private static string GetGamePath(string path)
    {
        if (path.EndsWith(Source2ModelExtension, StringComparison.Ordinal))
        {
            return string.Concat(path.AsSpan(0, path.Length - Source2ModelExtension.Length), ModelExtension);
        }

        if (path.EndsWith(Source2MaterialExtension, StringComparison.Ordinal))
        {
            var materialPath = Source1Paths.ToMaterialPath(path[..^Source2MaterialExtension.Length]);

            return Source1Paths.AddVariant(materialPath, ToolSpriteVariant);
        }

        return path;
    }

    /// <inheritdoc/>
    public EntityClassTable GetEntityClasses(ILogger logger) => Source1EntityClassCatalog.Get(fileSystem, logger);

    /// <inheritdoc/>
    public bool Exists(string file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var path = GetGamePath(RemoveCompiledSuffix(file));

        if (path.EndsWith(MaterialExtension, StringComparison.Ordinal))
        {
            return fileSystem.Exists(Source1Paths.RemoveVariant(path, out _));
        }

        if (path.EndsWith(ModelExtension, StringComparison.Ordinal) || path.EndsWith(TextureExtension, StringComparison.Ordinal))
        {
            return fileSystem.Exists(path);
        }

        if (TryGetParticleName(path, out var particleName))
        {
            return particles.GetParticleSystem(particleName) != null;
        }

        return false;
    }

    /// <inheritdoc/>
    public Resource? LoadFile(string file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var sourcePath = RemoveCompiledSuffix(file);

        if (map != null && map.Files.TryGetValue(sourcePath + GameFileLoader.CompiledFileSuffix, out var mapResource))
        {
            return mapResource;
        }

        var path = GetGamePath(sourcePath);
        Resource? resource;

        if (path.EndsWith(ModelExtension, StringComparison.Ordinal))
        {
            return LoadModel(path)?.Model.Resource;
        }

        if (path.EndsWith(MaterialExtension, StringComparison.Ordinal))
        {
            resource = LoadMaterialVariant(path);
        }
        else if (path == Source1Paths.BlackTexturePath)
        {
            resource = CreateBlackTexture();
        }
        else if (path.EndsWith(TextureExtension, StringComparison.Ordinal))
        {
            resource = LoadTextureFrame(path);
        }
        else if (TryGetParticleName(path, out var particleName))
        {
            return LoadParticleSystem(particleName);
        }
        else
        {
            return null;
        }

        if (resource != null)
        {
            resource.FileName = file;
        }

        return resource;
    }

    private Resource? LoadParticleSystem(string name)
    {
        if (particles.GetParticleSystem(name) is not { } particleSystem)
        {
            return null;
        }

        var resource = new Resource(ResourceType.Particle)
        {
            FileName = GetParticlePath(name),
        };
        resource.AddBlock(particleSystem);

        return resource;
    }

    private Resource? LoadTextureFrame(string path)
    {
        var texturePath = Source1Paths.RemoveVariant(path, out var variant);
        var frame = 0;

        if (variant != null && !int.TryParse(variant, NumberStyles.None, CultureInfo.InvariantCulture, out frame))
        {
            return null;
        }

        return LoadTexture(texturePath, frame);
    }

    private static Resource CreateBlackTexture()
    {
        ReadOnlyMemory<byte> pixel = new byte[] { 0, 0, 0, 255 };

        return VrfTexture.Create(1, 1, VTexFormat.RGBA8888, 0, Vector4.Zero, [pixel], null);
    }

    private Resource? LoadTexture(string path, int frame)
    {
        var resolvedPath = ResolveTexturePath(Source1Paths.Normalize(path));
        using var stream = fileSystem.OpenFile(resolvedPath);

        if (stream == null)
        {
            return null;
        }

        try
        {
            var vtf = new VtfFile(stream);

            return Source1TextureConverter.Convert(vtf, frame);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException or UnexpectedMagicException)
        {
            logger.LogWarning("Failed to load texture {Texture}: {Message}", path, e.Message);
            return null;
        }
    }

    private string ResolveTexturePath(string path)
    {
        // A high dynamic range variant of a texture is optional
        if (path.EndsWith(HdrTextureExtension, StringComparison.Ordinal) && !fileSystem.Exists(path))
        {
            return string.Concat(path.AsSpan(0, path.Length - HdrTextureExtension.Length), TextureExtension);
        }

        return path;
    }

    // A variant of a material is drawn by another shader: a tools sprite, or a Source 1 shader of the variant's name
    private Resource? LoadMaterialVariant(string path)
    {
        var materialPath = Source1Paths.RemoveVariant(path, out var variant);

        if (variant == null)
        {
            return LoadMaterial(path);
        }

        if (variant == ToolSpriteVariant)
        {
            return LoadToolSprite(materialPath);
        }

#pragma warning disable CA2000 // A material of the map is shared and stays open
        var source = LoadFile(materialPath);
#pragma warning restore CA2000

        if (source?.DataBlock is not ResourceTypes.Material material)
        {
            return null;
        }

        if (variant == OverlayVariant)
        {
            return Source1MaterialConverter.AsOverlay(material);
        }

        if (variant == CableVariant)
        {
            return Source1MaterialConverter.AsCable(material);
        }

        if (TryParseSpriteVariant(variant, out var renderMode))
        {
            return Source1MaterialConverter.AsSprite(material, renderMode);
        }

        var withShader = Source1MaterialConverter.WithShader(material, variant);

        if (variant == DetailSpriteVariant && map?.DetailFadeSquared is { } fade && withShader.DataBlock is ResourceTypes.Material detailMaterial)
        {
            detailMaterial.FloatParams["g_flDetailFadeStartSq"] = fade.X;
            detailMaterial.FloatParams["g_flDetailFadeEndSq"] = fade.Y;
            detailMaterial.BuildData();
        }

        return withShader;
    }

    private static bool TryParseSpriteVariant(string variant, out RenderMode renderMode)
    {
        renderMode = RenderMode.Normal;

        if (!variant.StartsWith(SpriteVariantPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var number = variant.AsSpan(SpriteVariantPrefix.Length);

        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        renderMode = (RenderMode)value;

        return true;
    }

    private Resource? LoadToolSprite(string path)
    {
        try
        {
            return Source1MaterialConverter.ConvertToolSprite(path, fileSystem);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            logger.LogWarning("Failed to load icon {Material}: {Message}", path, e.Message);
            return null;
        }
    }

    private Resource? LoadMaterial(string path)
    {
        try
        {
            var resource = Source1MaterialConverter.Convert(path, fileSystem, HighDynamicRange, logger);

            if (map != null && map.BumpedMaterials.Contains(path) && resource.DataBlock is ResourceTypes.Material material)
            {
                material.IntParams["F_BUMPED_LIGHTMAP"] = 1;
                material.BuildData();
            }

            return resource;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            logger.LogWarning("Failed to load material {Material}: {Message}", path, e.Message);
            return null;
        }
    }
}
