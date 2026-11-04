using System.IO;
using Microsoft.Extensions.Logging;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.Source1.Entities;
using ValveResourceFormat.Source1.FileSystem;
using ValveResourceFormat.Source1.Material.Proxies;
using ValveResourceFormat.Source1.Model;
using ValveResourceFormat.Source1.Texture;
using ValveResourceFormat.Utils;

namespace ValveResourceFormat.Source1.Material;

/// <summary>
/// Converts Source 1 materials to Source 2 style material resources.
/// </summary>
internal static class Source1MaterialConverter
{
    private const float DefaultAlphaTestReference = 0.7f;

    private static readonly (string Key, string Parameter)[] TextureMap =
    [
        ("$basetexture", "g_tColor"),
        ("$bumpmap", "g_tNormal"),
        ("$basetexture2", "g_tColor2"),
        ("$bumpmap2", "g_tNormal2"),
        ("$texture2", "g_tColor2"),
        ("$envmapmask", "g_tEnvMapMask"),
        ("$selfillummask", "g_tSelfIllumMask"),
        ("$detail", "g_tDetail"),
        ("$lightwarptexture", "g_tLightWarp"),
        ("$phongexponenttexture", "g_tPhongExponent"),
        ("$phongwarptexture", "g_tPhongWarp"),
        ("$blendmodulatetexture", "g_tBlendModulate"),
        ("$iris", "g_tIris"),
        ("$corneatexture", "g_tCornea"),
        ("$ambientoccltexture", "g_tAmbientOccl"),
        ("$gradienttexture", "g_tGradient"),
        ("$burndetailtexture", "g_tBurnDetail"),
    ];

    /// <summary>
    /// Loads a VMT, selecting the high dynamic range or the plain variants of its conditional blocks.
    /// </summary>
    /// <param name="path">The path of the material, such as <c>materials/concrete/floor.vmt</c>.</param>
    /// <param name="fileSystem">The files of the game.</param>
    /// <param name="highDynamicRange">Whether the <c>hdr</c> condition holds.</param>
    public static VmtFile LoadVmt(string path, Source1FileSystem fileSystem, bool highDynamicRange)
        => VmtFile.Load(path, fileSystem.OpenFile, symbol => ResolveHdrCondition(symbol, highDynamicRange));

    /// <summary>
    /// Converts a VMT of the game to a material resource.
    /// </summary>
    /// <param name="path">The path of the material, such as <c>materials/concrete/floor.vmt</c>. It names the material.</param>
    /// <param name="fileSystem">The files of the game, which the textures are read from for the flags that change the material.</param>
    /// <param name="highDynamicRange">Whether the material is loaded in high dynamic range, which selects the <c>hdr</c> variants of its
    /// conditional blocks and the <c>.hdr.vtf</c> variant of its cube map.</param>
    /// <param name="logger">Receives a warning for a material proxy that does not exist or cannot be set up.</param>
    /// <returns>The material resource, whose data block is a <see cref="ResourceTypes.Material"/>.</returns>
    /// <remarks>The proxies of the material are evaluated once, so what they animate keeps its value at time zero.</remarks>
    public static Resource Convert(string path, Source1FileSystem fileSystem, bool highDynamicRange, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var vmt = LoadVmt(path, fileSystem, highDynamicRange);
        var baseTextureHasAlpha = BaseTextureHasAlpha(fileSystem, vmt);
        var material = ToVrfMaterial(vmt, path, baseTextureHasAlpha);

        ApplySsbumpDetail(fileSystem, material, vmt.NormalizedShaderName);
        Source1MaterialProxyApplier.Apply(material, vmt, texturePath => GetFrameCount(fileSystem, texturePath), logger);
        ApplyTextureSampling(fileSystem, material);
        ApplyBlackBaseColor(material);

        if (IsToolsMaterial(vmt))
        {
            material.IntAttributes["tools.toolsmaterial"] = 1;

            // Tools materials are looked up by the name their Source 2 counterpart has
            material.Name = Path.ChangeExtension(path, ".vmat");
        }

        if (highDynamicRange && material.TextureParams.TryGetValue("g_tEnvMap", out var envMapPath))
        {
            // The provider falls back to the plain cube map when a high dynamic range one does not exist
            material.TextureParams["g_tEnvMap"] = Source1Paths.ToHdrTexturePath(envMapPath);
        }

        return Finish(material, path);
    }

    // Materials that mark their faces as not drawn by the game, which Source 2 tools materials do too
    private static readonly string[] ToolsCompileFlags =
    [
        "%compiletrigger",
        "%compilenodraw",
        "%compileclip",
        "%playerclip",
        "%compileplayercontrolclip",
        "%compilenpcclip",
        "%compileinvisible",
        "%compileladder",
        "%compileskip",
        "%compilehint",
        "%compileorigin",
        "%compilefog",
        "%compilesky",
        "%compile2dsky",
        "%compileblocklos",
    ];

    internal static bool IsToolsMaterial(VmtFile vmt)
    {
        foreach (var flag in ToolsCompileFlags)
        {
            if (vmt.Values.Boolean(flag))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copies a material so that another Source 1 shader draws it, with the same parameters and textures.
    /// </summary>
    /// <param name="source">The material to copy.</param>
    /// <param name="shaderName">The lowercase Source 1 shader name, such as <c>detailsprite</c>.</param>
    public static Resource WithShader(ResourceTypes.Material source, string shaderName)
    {
        ArgumentNullException.ThrowIfNull(source);

        var material = Copy(source, ResourceTypes.Material.Source1ShaderPrefix + shaderName);

        return Finish(material, source.Name);
    }

    /// <summary>
    /// Copies a material so that the Source 1 sprite shader draws it with the blending of a render mode of an entity.
    /// </summary>
    /// <param name="source">The material to copy.</param>
    /// <param name="renderMode">The render mode of the sprite.</param>
    internal static Resource AsSprite(ResourceTypes.Material source, RenderMode renderMode)
    {
        ArgumentNullException.ThrowIfNull(source);

        var material = Copy(source, ResourceTypes.Material.Source1ShaderPrefix + "sprite");

        var isAdditive = renderMode is RenderMode.Glow or RenderMode.WorldGlow or RenderMode.TransAdd or RenderMode.TransAddFrameBlend;
        var isBlended = isAdditive || renderMode is RenderMode.TransColor or RenderMode.TransTexture or RenderMode.TransAlpha or RenderMode.TransAlphaAdd;

        material.IntParams["F_TRANSLUCENT"] = isBlended ? 1 : 0;
        material.IntParams["F_ADDITIVE_BLEND"] = isAdditive ? 1 : 0;
        material.IntParams["F_ALPHA_TEST"] = 0;

        // The shader places the quads itself, and checks a glow for being in view instead of depth testing it
        material.IntParams["F_RENDER_BACKFACES"] = 1;
        material.IntParams["F_NO_Z_PREPASS"] = 1;
        material.IntParams["F_DISABLE_Z_BUFFERING"] = renderMode is RenderMode.Glow or RenderMode.WorldGlow ? 1 : 0;

        return Finish(material, source.Name);
    }

    /// <summary>
    /// Copies a material so that the Source 1 cable shader draws it, which turns the ribbons of a rope to face the camera.
    /// </summary>
    /// <param name="source">The material to copy.</param>
    internal static Resource AsCable(ResourceTypes.Material source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var material = Copy(source, ResourceTypes.Material.Source1ShaderPrefix + "cable");
        material.IntParams["F_RENDER_BACKFACES"] = 1;
        material.IntParams["F_NO_Z_PREPASS"] = 1;

        return Finish(material, source.Name);
    }

    /// <summary>
    /// Copies a material so that it is drawn as an overlay: on top of the surface under it, without writing depth.
    /// </summary>
    /// <param name="source">The material to copy.</param>
    public static Resource AsOverlay(ResourceTypes.Material source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var material = Copy(source, source.ShaderName);
        material.IntAttributes["overlay"] = 1;

        return Finish(material, source.Name);
    }

    /// <summary>
    /// Copies an eye material so its shader skins the eyeball with the bone the model attaches it to. Other materials are copied as they are.
    /// </summary>
    /// <param name="source">The material to copy.</param>
    /// <param name="eyeball">The eyeball in the space of the converted vertices.</param>
    internal static Resource AsEyeball(ResourceTypes.Material source, Source1Eyeball eyeball)
    {
        ArgumentNullException.ThrowIfNull(source);

        var material = Copy(source, source.ShaderName);

        if (IsShader(source, "eyes") || IsShader(source, "eyerefract"))
        {
            material.IntParams["g_nEyeBoneIndex"] = eyeball.Bone;
            material.VectorParams["g_vEyeBindOrigin"] = new Vector4(eyeball.Origin, 0f);
            material.VectorParams["g_vEyeBindUp"] = new Vector4(eyeball.Up, 0f);
            material.VectorParams["g_vEyeBindForward"] = new Vector4(eyeball.Forward, 0f);
            material.FloatParams["g_flEyeZOffset"] = eyeball.ZOffset;
            material.FloatParams["g_flEyeIrisScale"] = eyeball.IrisScale;
        }

        return Finish(material, source.Name);
    }

    /// <summary>
    /// Copies a teeth material so its shader lights the teeth from the mouth of the model. Other materials are copied as they are.
    /// </summary>
    /// <param name="source">The material to copy.</param>
    /// <param name="bone">The bone the mouth is attached to.</param>
    /// <param name="forward">The direction the mouth faces, in the space of the converted vertices.</param>
    internal static Resource AsTeeth(ResourceTypes.Material source, int bone, Vector3 forward)
    {
        ArgumentNullException.ThrowIfNull(source);

        var material = Copy(source, source.ShaderName);

        if (IsShader(source, "teeth"))
        {
            material.IntParams["g_nMouthBoneIndex"] = bone;
            material.VectorParams["g_vMouthBindForward"] = new Vector4(forward, 0f);
        }

        return Finish(material, source.Name);
    }

    private static bool IsShader(ResourceTypes.Material material, string shader)
    {
        return material.ShaderName == ResourceTypes.Material.Source1ShaderPrefix + shader;
    }

    private static ResourceTypes.Material Copy(ResourceTypes.Material source, string shaderName)
    {
        var material = new ResourceTypes.Material
        {
            Resource = new Resource(ResourceType.Material),
            Name = source.Name,
            ShaderName = shaderName,
        };

        foreach (var (name, value) in source.IntParams)
        {
            material.IntParams[name] = value;
        }

        foreach (var (name, value) in source.FloatParams)
        {
            material.FloatParams[name] = value;
        }

        foreach (var (name, value) in source.VectorParams)
        {
            material.VectorParams[name] = value;
        }

        foreach (var (name, value) in source.TextureParams)
        {
            material.TextureParams[name] = value;
        }

        foreach (var (name, value) in source.IntAttributes)
        {
            material.IntAttributes[name] = value;
        }

        return material;
    }

    /// <summary>
    /// Creates the material an editor icon is drawn with: the base texture of a material, drawn by the shader of the tools
    /// sprites, whose vertices the Source 1 sprite shaders do not read.
    /// </summary>
    /// <param name="path">The path of the material, such as <c>materials/editor/info_target.vmt</c>.</param>
    /// <param name="fileSystem">The files of the game.</param>
    /// <returns>The material resource, or <see langword="null"/> when the material has no base texture.</returns>
    public static Resource? ConvertToolSprite(string path, Source1FileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var vmt = VmtFile.Load(path, fileSystem.OpenFile);
        var baseTexture = vmt.Values.String("$basetexture");

        if (string.IsNullOrWhiteSpace(baseTexture))
        {
            return null;
        }

        var hasAlpha = BaseTextureHasAlpha(fileSystem, vmt);
        var material = new ResourceTypes.Material
        {
            Resource = new Resource(ResourceType.Material),
            Name = path,
            ShaderName = "tools_sprite.vfx",
        };

        // The shader reads the alpha of the color texture only when it is set to cut the sprite out
        material.IntParams["F_ALPHA_TEST"] = hasAlpha ? 1 : 0;
        SetTexture(material, hasAlpha ? "g_tColorB" : "g_tColorA", baseTexture);

        return Finish(material, path);
    }

    /// <summary>
    /// Creates a sky material that draws a cube texture around the camera.
    /// </summary>
    /// <param name="path">The path of the material.</param>
    /// <param name="cubeTexturePath">The path of the cube texture.</param>
    public static Resource CreateSkyMaterial(string path, string cubeTexturePath)
    {
        var material = new ResourceTypes.Material
        {
            Resource = new Resource(ResourceType.Material),
            Name = path,
            ShaderName = "sky.vfx",
        };

        material.TextureParams["g_tSkyTexture"] = cubeTexturePath;

        return Finish(material, path);
    }

    // A texture path that climbs out of its mount is never found by the game, so the material draws without it
    private static void SetTexture(ResourceTypes.Material material, string parameter, string path)
    {
        var texturePath = VmtFile.TryNormalizePath(path, ".vtf");

        if (texturePath != null)
        {
            material.TextureParams[parameter] = texturePath;
        }
    }

    private static Resource Finish(ResourceTypes.Material material, string path)
    {
        var resource = material.Resource;
        resource.FileName = path;
        material.BuildData();
        resource.AddBlock(material);

        return resource;
    }

    private static int GetFrameCount(Source1FileSystem fileSystem, string texturePath)
    {
        if (Source1TextureHeaders.TryRead(fileSystem, texturePath) is not { } header)
        {
            return 1;
        }

        return Math.Max(1, (int)header.Frames);
    }

    // The sampler state of a texture is a property of the material. The base texture decides it for every texture of the
    // material that reads the shared coordinates.
    private static void ApplyTextureSampling(Source1FileSystem fileSystem, ResourceTypes.Material material)
    {
        if (!material.TextureParams.TryGetValue("g_tColor", out var path) && !material.TextureParams.TryGetValue("g_tNormal", out path))
        {
            return;
        }

        var texturePath = Source1Paths.RemoveVariant(path, out _);

        if (Source1TextureHeaders.TryRead(fileSystem, texturePath) is not { } header)
        {
            return;
        }

        var flags = header.Flags;

        if ((flags & (VtfFlags.CLAMPS | VtfFlags.BORDER)) != 0)
        {
            material.IntParams["g_nTextureAddressModeU"] = (long)RsTextureAddressMode.Clamp;
        }

        if ((flags & (VtfFlags.CLAMPT | VtfFlags.BORDER)) != 0)
        {
            material.IntParams["g_nTextureAddressModeV"] = (long)RsTextureAddressMode.Clamp;
        }

        if ((flags & VtfFlags.POINTSAMPLE) != 0)
        {
            material.IntParams["g_nTextureFilter"] = 1;
        }
    }

    // A surface that only reflects has no color of its own
    private static void ApplyBlackBaseColor(ResourceTypes.Material material)
    {
        var reflectsOnly = material.IntParams.GetValueOrDefault("F_ENVMAP") != 0 && material.IntParams.GetValueOrDefault("F_PHONG") == 0;

        if (reflectsOnly && !material.TextureParams.ContainsKey("g_tColor"))
        {
            material.TextureParams["g_tColor"] = Source1Paths.BlackTexturePath;
        }
    }

    /// <summary>
    /// Creates a material resource from a parsed VMT. The Source 1 shader name is kept, lowercase, with the <see cref="ResourceTypes.Material.Source1ShaderPrefix"/>.
    /// </summary>
    /// <param name="vmt">The parsed material.</param>
    /// <param name="name">The name of the material resource.</param>
    /// <param name="baseTextureHasAlpha">Whether the base texture has an alpha channel. Without one, its alpha cannot drive self illumination or the environment map mask.</param>
    public static ResourceTypes.Material ToVrfMaterial(VmtFile vmt, string? name = null, bool baseTextureHasAlpha = true)
    {
        ArgumentNullException.ThrowIfNull(vmt);

        var values = vmt.Values;
        var material = new ResourceTypes.Material
        {
            Resource = new Resource(ResourceType.Material),
            Name = name ?? string.Empty,
            ShaderName = ResourceTypes.Material.Source1ShaderPrefix + vmt.NormalizedShaderName,
        };

        var shader = vmt.NormalizedShaderName;

        SetTextures(material, values, shader);

        var features = ResolveFeatures(material, values, shader, baseTextureHasAlpha);

        if (!features.HasEnvmapMask)
        {
            material.TextureParams.Remove("g_tEnvMapMask");
        }

        if (!features.HasBump)
        {
            material.TextureParams.Remove("g_tNormal2");
        }

        if (features.Envmap is { } envmap && !envmap.Equals("env_cubemap", StringComparison.OrdinalIgnoreCase))
        {
            SetTexture(material, "g_tEnvMap", envmap);
        }

        SetSurfaceFlags(material, values, shader, features);
        SetEnvmap(material, values, shader, features);
        SetSelfIllum(material, values, features);
        SetPhong(material, values, features);
        SetInfected(material, values, shader);
        SetEyeRefract(material, values, shader);
        SetWater(material, values, shader);
        SetModulate(material, values, shader);
        SetMonitorScreen(material, values, shader);
        SetAlpha(material, values, shader);
        SetTextureFlags(material, features);
        SetDetail(material, values, shader, features);
        SetColorTint(material, values, shader);
        SetTransforms(material, values, shader, features);

        return material;
    }

    private static void SetTextures(ResourceTypes.Material material, VmtParameters values, string shader)
    {
        var isWater = IsWater(shader);

        // The ripples of water come from the normal map, a $bumpmap of water is the du/dv map of older hardware
        if (isWater && values.String("$normalmap") is { } normalMap && !string.IsNullOrWhiteSpace(normalMap))
        {
            SetTexture(material, "g_tNormal", normalMap);
        }

        // A bump mapped surface lit by its bumped lightmaps takes the albedo texture, which has no baked light, as its base texture
        if (shader == "worldtwotextureblend"
            && values.Contains("$bumpmap")
            && !values.Boolean("$nodiffusebumplighting")
            && values.String("$albedo") is { } albedo
            && !string.IsNullOrWhiteSpace(albedo))
        {
            SetTexture(material, "g_tColor", albedo);
        }

        foreach (var (key, parameter) in TextureMap)
        {
            var path = values.String(key);

            if (string.IsNullOrWhiteSpace(path) || material.TextureParams.ContainsKey(parameter))
            {
                continue;
            }

            if (isWater && key == "$bumpmap")
            {
                continue;
            }

            SetTexture(material, parameter, path);
        }
    }

    // Decides which of the optional features of the shader the textures and parameters of the material turn on, as the
    // shaders combine them
    private static SurfaceFeatures ResolveFeatures(ResourceTypes.Material material, VmtParameters values, string shader, bool baseTextureHasAlpha)
    {
        var isLightmapped = IsLightmapped(shader);
        var isVertexLit = IsVertexLit(shader);
        var isUnlit = shader is "unlitgeneric";

        var hasBaseTexture = material.TextureParams.ContainsKey("g_tColor");
        var hasBump = material.TextureParams.ContainsKey("g_tNormal");
        var hasLightWarp = material.TextureParams.ContainsKey("g_tLightWarp");
        var hasDetail = material.TextureParams.ContainsKey("g_tDetail");
        var hasEnvmapMask = material.TextureParams.ContainsKey("g_tEnvMapMask");
        // The lightmapped shaders have no sampler for a self illumination mask
        var hasSelfIllumMask = material.TextureParams.ContainsKey("g_tSelfIllumMask") && !isLightmapped;

        // Phong needs a bump map, the base alpha as its mask or a light warp texture. The infected shader takes its mask
        // from the variation texture.
        var basemapAlphaPhongMask = values.Boolean("$basemapalphaphongmask");
        var phong = values.Boolean("$phong") && (hasBump || basemapAlphaPhongMask || hasLightWarp || shader == "infected");

        var envmap = values.String("$envmap");
        var hasEnvmap = !string.IsNullOrWhiteSpace(envmap);

        var selfIllum = values.Boolean("$selfillum");
        var wantsSelfIllumFresnel = values.Boolean("$selfillumfresnel");
        var baseAlphaEnvmapMask = values.Boolean("$basealphaenvmapmask");
        var normalMapAlphaEnvmapMask = values.Boolean("$normalmapalphaenvmapmask");

        if (!hasBaseTexture)
        {
            selfIllum = false;
            baseAlphaEnvmapMask = false;
        }
        else if (!baseTextureHasAlpha)
        {
            // A vertex lit model can still glow from its own mask texture or a fresnel term
            selfIllum = selfIllum && isVertexLit && (hasSelfIllumMask || wantsSelfIllumFresnel);
            baseAlphaEnvmapMask = false;
        }

        if (!isVertexLit && !isLightmapped)
        {
            selfIllum = false;
        }

        if (isVertexLit && !phong)
        {
            if (!hasBump && !hasLightWarp)
            {
                normalMapAlphaEnvmapMask = false;
            }

            if (normalMapAlphaEnvmapMask)
            {
                hasEnvmapMask = false;
                baseAlphaEnvmapMask = false;
            }

            // A bump mapped model can only mask the environment map with the alpha of its normal map
            if (hasBump && baseAlphaEnvmapMask && !normalMapAlphaEnvmapMask)
            {
                hasEnvmap = false;
            }

            if (hasBump && hasEnvmapMask)
            {
                hasEnvmapMask = false;

                if (!normalMapAlphaEnvmapMask)
                {
                    hasEnvmap = false;
                }
            }
        }

        if (isLightmapped && hasBump)
        {
            hasEnvmapMask = false;
        }

        // The shader has no environment map and no fresnel term
        if (shader == "worldtwotextureblend")
        {
            hasEnvmap = false;
            hasEnvmapMask = false;
            baseAlphaEnvmapMask = false;
            wantsSelfIllumFresnel = false;
        }

        // An unlit surface has no normal map to take a mask from
        if (isUnlit)
        {
            normalMapAlphaEnvmapMask = false;
        }

        // Only the shader without a normal map or light warp can take the self illumination from the mask alpha, and it
        // cannot also use the base alpha
        var selfIllumFromMask = hasEnvmapMask
            && values.Float("$selfillum_envmapmask_alpha", 0f) != 0f
            && (isUnlit || (isVertexLit && !hasBump && !hasLightWarp && !phong));

        if (selfIllumFromMask)
        {
            selfIllum = false;
        }

        // The fresnel term is in the shaders with a normal map or light warp, which only have it without a detail texture,
        // and in the phong shader
        var selfIllumFresnel = selfIllum && wantsSelfIllumFresnel && (phong || ((hasBump || hasLightWarp) && !hasDetail));
        var useLightWarp = hasLightWarp && !(selfIllumFresnel && !phong);

        if (selfIllumFresnel && !phong)
        {
            normalMapAlphaEnvmapMask = false;
        }

        return new SurfaceFeatures(
            hasEnvmap ? envmap : null,
            phong,
            basemapAlphaPhongMask,
            hasBump,
            hasEnvmapMask,
            hasSelfIllumMask,
            hasDetail,
            selfIllum,
            selfIllumFromMask,
            selfIllumFresnel,
            baseAlphaEnvmapMask,
            normalMapAlphaEnvmapMask,
            useLightWarp);
    }

    private static void SetSurfaceFlags(ResourceTypes.Material material, VmtParameters values, string shader, SurfaceFeatures features)
    {
        var isVertexLit = IsVertexLit(shader);
        var hasBump = features.HasBump;

        // A surface that uses the alpha of its base texture for something else is not alpha tested
        var alphaTest = values.Boolean("$alphatest") && (features.Phong || !(features.SelfIllum || features.BaseAlphaEnvmapMask));

        SetFlag(material, "F_ALPHA_TEST", alphaTest);
        bool translucent;

        if (shader == "refract")
        {
            // The alpha of the normal map is the translucency, unless an environment map takes it as the reflection amount
            translucent = hasBump && !features.HasEnvmap;
        }
        else if (IsWater(shader))
        {
            // Cheap water that is not forced draws its reflection over the scene
            translucent = !values.Boolean("$forcecheap");
        }
        else
        {
            translucent = values.Boolean("$translucent");
        }

        SetFlag(material, "F_TRANSLUCENT", translucent);
        SetFlag(material, "F_ADDITIVE_BLEND", values.Boolean("$additive"));
        SetFlag(material, "F_RENDER_BACKFACES", values.Boolean("$nocull"));

        // Models are lit by their vertices and ignore the vertex colors of the mesh
        var vertexColor = values.Boolean("$vertexcolor");
        var vertexAlpha = values.Boolean("$vertexalpha");

        // The modulate shader takes the vertex color and alpha together
        if (shader == "modulate")
        {
            vertexColor |= vertexAlpha;
            vertexAlpha = vertexColor;
        }

        SetFlag(material, "F_VERTEX_COLOR", !isVertexLit && vertexColor);
        SetFlag(material, "F_VERTEX_ALPHA", !isVertexLit && vertexAlpha);
        SetFlag(material, "F_SELF_ILLUM", features.SelfIllum);

        // Meshes of models can have flexes, which reach the shader as morphs
        SetFlag(material, "F_MORPH_SUPPORTED", isVertexLit);

        SetFlag(material, "F_BUMPMAP", hasBump);
        SetFlag(material, "F_BUMPMAP2", hasBump && material.TextureParams.ContainsKey("g_tNormal2"));
        SetFlag(material, "F_SSBUMP", hasBump && values.Boolean("$ssbump"));

        var isLightmapped = IsLightmapped(shader);
        SetFlag(material, "F_MASKED_BLENDING", isLightmapped && values.Boolean("$maskedblending"));
        SetFlag(material, "F_BASE_TEXTURE_NO_ENVMAP", isLightmapped && values.Boolean("$basetexturenoenvmap"));
        SetFlag(material, "F_BASE_TEXTURE2_NO_ENVMAP", isLightmapped && values.Boolean("$basetexture2noenvmap"));

        SetFlag(material, "F_ENVMAP", features.HasEnvmap);
        SetFlag(material, "F_ENVMAP_SCENE", features.HasEnvmap && !material.TextureParams.ContainsKey("g_tEnvMap"));
        SetFlag(material, "F_ENVMAP_MASK", features.HasEnvmapMask);
        SetFlag(material, "F_BASE_ALPHA_ENVMAP_MASK", features.BaseAlphaEnvmapMask);
        SetFlag(material, "F_NORMALMAP_ALPHA_ENVMAP_MASK", features.NormalMapAlphaEnvmapMask);
    }

    private static void SetEnvmap(ResourceTypes.Material material, VmtParameters values, string shader, SurfaceFeatures features)
    {
        if (!features.HasEnvmap)
        {
            return;
        }

        var contrast = values.Float("$envmapcontrast", 0f);
        var saturation = values.Float("$envmapsaturation", 1f);
        var fresnelReflection = values.Float("$fresnelreflection", 1f);

        // The lightmapped shader only reads the contrast and the saturation when both change the reflection or when
        // the fresnel or a self illumination tint needs its slower variant. Otherwise it uses its fixed ones.
        if (IsLightmapped(shader))
        {
            var changesContrast = contrast != 0f && contrast != 1f && saturation != 1f;
            var hasSelfIllumTint = features.SelfIllum && values.Vector("$selfillumtint", Vector3.One) != Vector3.One;

            if (!changesContrast && fresnelReflection == 1f && !hasSelfIllumTint)
            {
                contrast = contrast == 1f ? 1f : 0f;
                saturation = 1f;
            }

            // The variant with a blend modulate texture has no contrast and saturation
            if (material.TextureParams.ContainsKey("g_tBlendModulate"))
            {
                contrast = 0f;
                saturation = 1f;
            }
        }

        var envMapTint = values.Vector("$envmaptint", Vector3.One);
        material.VectorParams["g_vEnvMapTint"] = new Vector4(ConvertTint("$envmaptint", envMapTint, shader), 1f);
        material.FloatParams["g_flEnvMapContrast"] = contrast;
        material.FloatParams["g_flEnvMapSaturation"] = saturation;
        material.FloatParams["g_flEnvMapFresnel"] = values.Float("$envmapfresnel", 0f);
        material.FloatParams["g_flFresnelReflection"] = fresnelReflection;
    }

    private static void SetSelfIllum(ResourceTypes.Material material, VmtParameters values, SurfaceFeatures features)
    {
        SetFlag(material, "F_SELF_ILLUM_ENVMAPMASK_ALPHA", features.SelfIllumFromMask);
        SetFlag(material, "F_SELF_ILLUM_FRESNEL", features.SelfIllumFresnel);

        if (features.SelfIllum || features.SelfIllumFromMask)
        {
            material.VectorParams["g_vSelfIllumTint"] = new Vector4(values.Vector("$selfillumtint", Vector3.One), 1f);
        }

        if (features.SelfIllumFresnel)
        {
            var minMaxExp = values.Vector("$selfillumfresnelminmaxexp", new Vector3(0f, 1f, 1f));
            var bias = minMaxExp.Y != 0f ? minMaxExp.X / minMaxExp.Y : 0f;

            // Scale, bias, exponent and brightness
            material.VectorParams["g_vSelfIllumFresnel"] = new Vector4(1f - bias, bias, minMaxExp.Z, minMaxExp.Y);
        }
    }

    private static void SetPhong(ResourceTypes.Material material, VmtParameters values, SurfaceFeatures features)
    {
        var phong = features.Phong;

        SetFlag(material, "F_LIGHTWARP", features.UseLightWarp);
        SetFlag(material, "F_HALF_LAMBERT", phong || values.Boolean("$halflambert"));
        SetFlag(material, "F_PHONG", phong);

        if (phong)
        {
            var hasExponentTexture = material.TextureParams.ContainsKey("g_tPhongExponent");

            SetFlag(material, "F_PHONG_EXPONENT_TEXTURE", hasExponentTexture);
            SetFlag(material, "F_PHONG_WARP", material.TextureParams.ContainsKey("g_tPhongWarp"));
            SetFlag(material, "F_BASEMAP_ALPHA_PHONG_MASK", features.BasemapAlphaPhongMask);
            SetFlag(material, "F_INVERT_PHONG_MASK", values.Boolean("$invertphongmask"));

            material.FloatParams["g_flPhongExponent"] = values.Float("$phongexponent", 0f);
            material.FloatParams["g_flPhongExponentFactor"] = values.Float("$phongexponentfactor", 0f);
            material.FloatParams["g_flPhongBoost"] = values.Float("$phongboost", 1f);

            var ranges = values.Vector("$phongfresnelranges", new Vector3(0f, 0.5f, 1f));
            material.VectorParams["g_vPhongFresnelRanges"] = new Vector4(ranges, 0f);

            // A zero tint tints by the albedo when the exponent texture has a channel for it, flagged with a negative red
            // channel, and by white otherwise
            var tint = values.Vector("$phongtint", Vector3.Zero);

            if (tint == Vector3.Zero)
            {
                tint = hasExponentTexture && values.Boolean("$phongalbedotint") ? new Vector3(-1f, 0f, 0f) : Vector3.One;
            }

            material.VectorParams["g_vPhongTint"] = new Vector4(tint, 0f);
        }

        var rimLight = phong && values.Boolean("$rimlight");
        SetFlag(material, "F_RIM_LIGHT", rimLight);

        if (rimLight)
        {
            SetFlag(material, "F_RIM_MASK", values.Boolean("$rimmask") && material.TextureParams.ContainsKey("g_tPhongExponent"));
            material.FloatParams["g_flRimLightExponent"] = MathF.Max(values.Float("$rimlightexponent", 4f), 1f);
            material.FloatParams["g_flRimLightBoost"] = values.Float("$rimlightboost", 1f);
        }
    }

    private static void SetInfected(ResourceTypes.Material material, VmtParameters values, string shader)
    {
        if (shader != "infected")
        {
            return;
        }

        var disableVariation = values.Boolean("$disablevariation") || !material.TextureParams.ContainsKey("g_tGradient");

        SetFlag(material, "F_DISABLE_VARIATION", disableVariation);
        SetFlag(material, "F_BURNING", !disableVariation && values.Boolean("$burning") && material.TextureParams.ContainsKey("g_tBurnDetail"));

        material.IntParams["g_nSheetIndex"] = values.Integer("$sheetindex", 0);
        material.FloatParams["g_flSkinTintGradient"] = values.Float("$skintintgradient", 0f);
        material.FloatParams["g_flColorTintGradient"] = values.Float("$colortintgradient", 0f);
        material.FloatParams["g_flDefaultPhongExponent"] = values.Float("$defaultphongexponent", 5f);
        material.FloatParams["g_flSkinPhongExponent"] = values.Float("$skinphongexponent", 5f);
        material.FloatParams["g_flBloodPhongExponent"] = values.Float("$bloodphongexponent", 5f);
        material.FloatParams["g_flBurnStrength"] = values.Float("$burnstrength", 0f);
        material.VectorParams["g_vBloodColor"] = new Vector4(values.Vector("$bloodcolor", new Vector3(1f, 0f, 0f)), 0f);

        var bloodMaskRange = values.Vector("$bloodmaskrange", new Vector3(0f, 1f, 0f));
        material.VectorParams["g_vBloodMaskRange"] = new Vector4(bloodMaskRange.X, bloodMaskRange.Y, 0f, 0f);
    }

    private static void SetEyeRefract(ResourceTypes.Material material, VmtParameters values, string shader)
    {
        if (shader != "eyerefract")
        {
            return;
        }

        SetFlag(material, "F_RAYTRACE_SPHERE", values.Boolean("$raytracesphere", true));
        SetFlag(material, "F_SPHERE_TEXKILL", values.Boolean("$spheretexkillcombo", true));

        material.FloatParams["g_flDilation"] = values.Float("$dilation", 0.5f);
        material.FloatParams["g_flGlossiness"] = values.Float("$glossiness", 1f);
        material.FloatParams["g_flCorneaBumpStrength"] = values.Float("$corneabumpstrength", 1f);
        material.FloatParams["g_flParallaxStrength"] = values.Float("$parallaxstrength", 0.25f);
        material.FloatParams["g_flEyeballRadius"] = values.Float("$eyeballradius", 0.5f);
        material.VectorParams["g_vAmbientOcclColor"] = new Vector4(values.Vector("$ambientocclcolor", new Vector3(0.33f)), 0f);
    }

    private static void SetModulate(ResourceTypes.Material material, VmtParameters values, string shader)
    {
        if (shader != "modulate")
        {
            return;
        }

        SetFlag(material, "F_MOD2X", values.Boolean("$mod2x"));
        SetFlag(material, "F_WRITE_Z", values.Boolean("$writez"));
    }

    private static void SetMonitorScreen(ResourceTypes.Material material, VmtParameters values, string shader)
    {
        if (shader != "monitorscreen")
        {
            return;
        }

        material.FloatParams["g_flMonitorContrast"] = values.Float("$contrast", 0f);
        material.FloatParams["g_flMonitorSaturation"] = values.Float("$saturation", 1f);
        material.VectorParams["g_vMonitorTint"] = new Vector4(values.Vector("$tint", Vector3.One), 1f);
    }

    private static void SetWater(ResourceTypes.Material material, VmtParameters values, string shader)
    {
        if (!IsWater(shader))
        {
            return;
        }

        var fogColor = values.Vector("$fogcolor", new Vector3(0.1f, 0.2f, 0.25f));
        material.VectorParams["g_vWaterFogColor"] = new Vector4(ConvertTint("$fogcolor", fogColor, shader), 1f);
        material.VectorParams["g_vRefractTint"] = new Vector4(ConvertTint("$refracttint", values.Vector("$refracttint", Vector3.One), shader), 1f);
        var reflectTint = ConvertTint("$reflecttint", values.Vector("$reflecttint", Vector3.One), shader);
        material.VectorParams["g_vReflectTint"] = new Vector4(reflectTint, values.Float("$reflectblendfactor", 1f));
        SetFlag(material, "F_NO_FRESNEL", values.Boolean("$nofresnel"));
        material.FloatParams["g_flRefractAmount"] = values.Float("$refractamount", 0f);
        material.FloatParams["g_flWaterFogStart"] = values.Float("$fogstart", 0f);
        material.FloatParams["g_flWaterFogEnd"] = values.Float("$fogend", 0f);
        material.FloatParams["g_flCheapWaterStartDistance"] = values.Float("$cheapwaterstartdistance", 500f);
        material.FloatParams["g_flCheapWaterEndDistance"] = values.Float("$cheapwaterenddistance", 1000f);
        SetFlag(material, "F_REFRACT", values.Contains("$refracttexture") && !values.Boolean("$forcecheap"));
        SetFlag(material, "F_FADE_OUT_ON_SILHOUETTE", values.Boolean("$fadeoutonsilhouette"));
    }

    private static void SetAlpha(ResourceTypes.Material material, VmtParameters values, string shader)
    {
        // Without a positive reference the alpha test uses the default of the render state, then the reference is quantized to 8 bits
        var alphaTestReference = values.Float("$alphatestreference", 0f);

        if (alphaTestReference <= 0f)
        {
            alphaTestReference = DefaultAlphaTestReference;
        }

        material.FloatParams["g_flAlphaTestReference"] = MathUtils.FloorToUnorm8Step(alphaTestReference);
        var opacity = values.Float("$alpha", 1f);
        material.FloatParams["g_flOpacityScale"] = ConvertOpacity(opacity, shader);
    }

    private static void SetTextureFlags(ResourceTypes.Material material, SurfaceFeatures features)
    {
        SetFlag(material, "F_BASE_TEXTURE2", material.TextureParams.ContainsKey("g_tColor2"));
        SetFlag(material, "F_BLEND_MODULATE", material.TextureParams.ContainsKey("g_tBlendModulate"));
        SetFlag(material, "F_SELF_ILLUM_MASK", features.SelfIllum && features.HasSelfIllumMask);
        SetFlag(material, "F_DETAIL", features.HasDetail);
    }

    private static void SetDetail(ResourceTypes.Material material, VmtParameters values, string shader, SurfaceFeatures features)
    {
        if (!features.HasDetail)
        {
            return;
        }

        // The two texture lightmapped shader declares a scale of one, the others four
        var defaultDetailScale = shader == "lightmappedtwotexture" ? 1f : 4f;
        material.FloatParams["g_flDetailScale"] = values.Float("$detailscale", defaultDetailScale);

        // The detail texture blends over the base by its own alpha, without a blend mode, factor or tint
        if (shader == "worldtwotextureblend")
        {
            SetFlag(material, "F_DETAIL_ALPHA_MASK_BASE_TEXTURE", values.Boolean("$detail_alpha_mask_base_texture"));
            return;
        }

        material.FloatParams["g_flDetailBlendFactor"] = values.Float("$detailblendfactor", 1f);
        var detailTint = values.Vector("$detailtint", Vector3.One);
        material.VectorParams["g_vDetailTint"] = new Vector4(ConvertTint("$detailtint", detailTint, shader), 1f);
        material.IntParams["g_nDetailBlendMode"] = values.Integer("$detailblendmode", 0);
    }

    private static void SetColorTint(ResourceTypes.Material material, VmtParameters values, string shader)
    {
        // $color2 modulates the color of every shader, on top of $color
        var colorTint = values.Vector("$color", Vector3.One) * values.Vector("$color2", Vector3.One);

        colorTint = ConvertColorTint(colorTint, shader);

        if (shader == "vertexlitgeneric")
        {
            var blendTint = values.Boolean("$blendtintbybasealpha") && !values.Boolean("$selfillum");

            SetFlag(material, "F_BLEND_TINT_BY_BASE_ALPHA", blendTint);
            material.FloatParams["g_flBlendTintColorOverBase"] = values.Float("$blendtintcoloroverbase", 0f);
        }

        material.VectorParams["g_vColorTint"] = new Vector4(colorTint, 1f);

        if (values.Boolean("$nofog"))
        {
            material.IntParams["g_bFogEnabled"] = 0;
        }
    }

    private static void SetTransforms(ResourceTypes.Material material, VmtParameters values, string shader, SurfaceFeatures features)
    {
        var isLightmapped = IsLightmapped(shader);

        SetTransform(material, values, "$basetexturetransform", "g_vTexCoordTransformRow0", "g_vTexCoordTransformRow1");

        if (shader is "unlittwotexture" or "monitorscreen")
        {
            SetTransform(material, values, "$texture2transform", "g_vTexCoord2TransformRow0", "g_vTexCoord2TransformRow1");
        }

        if (isLightmapped || IsWater(shader))
        {
            SetTransform(material, values, "$bumptransform", "g_vBumpTransformRow0", "g_vBumpTransformRow1");
        }

        if (!isLightmapped)
        {
            return;
        }

        // Only a surface that blends by its blend modulate texture alone moves the texture
        if (values.Boolean("$maskedblending"))
        {
            SetTransform(material, values, "$blendmasktransform", "g_vBlendMaskTransformRow0", "g_vBlendMaskTransformRow1");
        }

        if (features.HasEnvmapMask)
        {
            SetTransform(material, values, "$envmapmasktransform", "g_vEnvMapMaskTransformRow0", "g_vEnvMapMaskTransformRow1");
        }

        // The seamless mapping replaces the base texture coordinates, and has no detail texture
        if (!features.HasDetail)
        {
            material.FloatParams["g_flSeamlessScale"] = values.Float("$seamless_scale", 0f);
        }
    }

    // What the textures and parameters of a material turn on in the shader, once the combinations the shaders cannot do are removed
    private readonly record struct SurfaceFeatures(
        string? Envmap,
        bool Phong,
        bool BasemapAlphaPhongMask,
        bool HasBump,
        bool HasEnvmapMask,
        bool HasSelfIllumMask,
        bool HasDetail,
        bool SelfIllum,
        bool SelfIllumFromMask,
        bool SelfIllumFresnel,
        bool BaseAlphaEnvmapMask,
        bool NormalMapAlphaEnvmapMask,
        bool UseLightWarp)
    {
        public bool HasEnvmap => Envmap != null;
    }

    // Lightmapped surfaces and detail sprites use the tint as it is, the others convert it from gamma space
    internal static Vector3 ConvertColorTint(Vector3 tint, string shader)
    {
        var rawTint = IsLightmapped(shader) || shader is "detailsprite" or "modulate";

        return rawTint ? tint : TintToLinear(tint);
    }

    // The shaders that tint with the material color clamp its alpha
    internal static float ConvertOpacity(float opacity, string shader)
    {
        var clamps = IsLightmapped(shader) || shader is "vertexlitgeneric" or "unlitgeneric" or "skin";

        return clamps ? Math.Clamp(opacity, 0f, 1f) : opacity;
    }

    // The shader tints the converter stores in linear space, any other tint is stored as it is
    internal static Vector3 ConvertTint(string variable, Vector3 tint, string shader)
    {
        var isLinear = false;

        if (variable.Equals("$envmaptint", StringComparison.OrdinalIgnoreCase))
        {
            isLinear = shader == "refract";
        }
        else if (variable.Equals("$detailtint", StringComparison.OrdinalIgnoreCase))
        {
            isLinear = IsVertexLit(shader);
        }
        else if (variable.Equals("$refracttint", StringComparison.OrdinalIgnoreCase)
            || variable.Equals("$reflecttint", StringComparison.OrdinalIgnoreCase)
            || variable.Equals("$fogcolor", StringComparison.OrdinalIgnoreCase))
        {
            isLinear = IsWater(shader);
        }

        return isLinear ? TintToLinear(tint) : tint;
    }

    private static bool IsWater(string shader) => shader is "water" or "refract" or "watercheap";

    private static bool IsLightmapped(string shader)
        => shader is "lightmappedgeneric" or "worldvertextransition" or "lightmappedreflective" or "lightmappedtwotexture" or "worldtwotextureblend";

    private static bool IsVertexLit(string shader) => shader is "vertexlitgeneric" or "eyes" or "eyerefract" or "teeth" or "skin";

    private static Vector3 TintToLinear(Vector3 tint)
        => new(TintChannelToLinear(tint.X), TintChannelToLinear(tint.Y), TintChannelToLinear(tint.Z));

    // A channel brighter than one stays as it is, a nearly white one becomes white and anything else is quantized to
    // 8 bits before the gamma curve
    private static float TintChannelToLinear(float channel)
    {
        if (channel > 1f)
        {
            return channel;
        }

        if (channel < 0f)
        {
            return 0f;
        }

        if (channel >= 0.95f)
        {
            return 1f;
        }

        return MathF.Pow(MathUtils.FloorToUnorm8Step(channel), 2.2f);
    }

    private static bool? ResolveHdrCondition(string symbol, bool highDynamicRange)
    {
        if (symbol.Equals("hdr", StringComparison.OrdinalIgnoreCase))
        {
            return highDynamicRange;
        }

        return null;
    }

    // A detail texture of a lightmapped surface that is flagged as a self shadowed bump map holds lightmap weights
    // when the surface has a bump map and a brightness otherwise, whatever blend mode the material asks for
    private static void ApplySsbumpDetail(Source1FileSystem fileSystem, ResourceTypes.Material material, string shader)
    {
        const int DetailBlendBumpWeights = 10;
        const int DetailBlendBrightness = 11;

        if (!IsLightmapped(shader))
        {
            return;
        }

        if (!material.TextureParams.TryGetValue("g_tDetail", out var detailPath))
        {
            return;
        }

        if (Source1TextureHeaders.TryRead(fileSystem, detailPath) is not { } header || (header.Flags & VtfFlags.SSBUMP) == 0)
        {
            return;
        }

        var hasBump = material.TextureParams.ContainsKey("g_tNormal");
        material.IntParams["g_nDetailBlendMode"] = hasBump ? DetailBlendBumpWeights : DetailBlendBrightness;
    }

    internal static bool BaseTextureHasAlpha(Source1FileSystem fileSystem, VmtFile vmt)
    {
        if (Source1TextureHeaders.TryReadBaseTexture(fileSystem, vmt) is not { } header)
        {
            return true;
        }

        return (header.Flags & (VtfFlags.ONEBITALPHA | VtfFlags.EIGHTBITALPHA)) != 0;
    }

    private static void SetTransform(ResourceTypes.Material material, VmtParameters values, string key, string row0Name, string row1Name)
    {
        if (!values.Contains(key))
        {
            return;
        }

        Matrix4x4 transform;

        try
        {
            transform = values.Transform(key);
        }
        catch (System.IO.InvalidDataException)
        {
            // Shipped materials have transforms that do not parse, which leave the texture untransformed
            return;
        }

        material.VectorParams[row0Name] = new Vector4(transform.M11, transform.M21, 0f, transform.M41);
        material.VectorParams[row1Name] = new Vector4(transform.M12, transform.M22, 0f, transform.M42);
    }

    private static void SetFlag(ResourceTypes.Material material, string name, bool enabled)
        => material.IntParams[name] = enabled ? 1 : 0;
}
