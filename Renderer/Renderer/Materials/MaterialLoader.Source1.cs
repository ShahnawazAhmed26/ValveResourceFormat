using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer.Source1;
using ValveResourceFormat.Renderer.Source1.MaterialProxies;
using ValveResourceFormat.Source1.FileSystem;
using ValveResourceFormat.Source1.Material;
using ValveResourceFormat.Source1.Texture;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace ValveResourceFormat.Renderer.Materials
{
    public partial class MaterialLoader
    {
        private const string Source1MaterialExtension = ".vmt";
        private const string Source1TextureExtension = ".vtf";
        private const string Source1HdrTextureExtension = ".hdr.vtf";

        // The tools sprite shader belongs to the renderer, so the game has no compiled file of it to read
        private static readonly BasicShaderDataProvider Source1ToolSpriteData = new();

        private RenderTexture? Source1DefaultBlack;
        private readonly Dictionary<(string Name, string ShaderArguments), RenderMaterial?> Source1ToolSprites = [];
        private readonly Dictionary<RenderMaterial, Source1MaterialProxyBinder> Source1ProxyBinders = [];
        private readonly Dictionary<string, int> Source1FrameCounts = [];
        private readonly Dictionary<(string Path, bool SrgbRead, int Frame), RenderTexture> Source1FrameTextures = [];

        private static bool IsSource1Material(string name)
            => name.EndsWith(Source1MaterialExtension, StringComparison.OrdinalIgnoreCase);

        private static bool IsSource1Texture(string name)
            => name.EndsWith(Source1TextureExtension, StringComparison.OrdinalIgnoreCase);

        private RenderMaterial LoadSource1Material(string name, Dictionary<string, byte>? shaderArguments)
        {
            var fileSystem = RendererContext.Source1FileSystem;

            if (fileSystem == null)
            {
                RendererContext.Logger.LogWarning("No Source 1 file system to load material {Material}", name);
                return GetErrorMaterial();
            }

            VrfMaterial vrfMaterial;
            VmtFile vmt;

            try
            {
                var highDynamicRange = RendererContext.Source1HighDynamicRange;
                vmt = VmtFile.Load(name, fileSystem.OpenFile, symbol => ResolveHdrCondition(symbol, highDynamicRange));
                var baseTextureHasAlpha = BaseTextureHasAlpha(fileSystem, vmt);

                vrfMaterial = Source1MaterialConverter.ToVrfMaterial(vmt, name, baseTextureHasAlpha);

                ApplySource1SsbumpDetail(fileSystem, vrfMaterial);

                // The loader falls back to the plain cube map when a high dynamic range one does not exist
                if (highDynamicRange && vrfMaterial.TextureParams.TryGetValue("g_tEnvMap", out var envMapPath))
                {
                    vrfMaterial.TextureParams["g_tEnvMap"] = ToHdrTexturePath(envMapPath);
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                RendererContext.Logger.LogWarning("Failed to load material {Material}: {Message}", name, e.Message);
                return GetErrorMaterial();
            }

            var material = CreateSource1Material(vrfMaterial, shaderArguments);
            AddSource1Proxies(material, vmt);

            return material;
        }

        private void AddSource1Proxies(RenderMaterial material, VmtFile vmt)
        {
            var seed = (int)StringToken.Get(material.Material.Name);
            var proxies = new Source1MaterialProxies(vmt, seed, GetSource1FrameCount, RendererContext.Logger);
            var binder = new Source1MaterialProxyBinder(material, proxies, this);
            var hasStaticFrames = binder.BindStaticFrames();

            if (proxies.HasProxies || hasStaticFrames)
            {
                Source1ProxyBinders.Add(material, binder);
            }
        }

        /// <summary>
        /// Evaluates the material proxies of every Source 1 material for a time, such as the scroll of water or a pulsing color.
        /// A material is evaluated once per time.
        /// </summary>
        /// <param name="time">The time in seconds the proxies read as the current time.</param>
        public void UpdateSource1Proxies(float time)
        {
            foreach (var binder in Source1ProxyBinders.Values)
            {
                binder.Update(time);
            }
        }

        /// <summary>The number of frames of a Source 1 texture, or 1 when it does not exist.</summary>
        internal int GetSource1FrameCount(string path)
        {
            if (Source1FrameCounts.TryGetValue(path, out var count))
            {
                return count;
            }

            count = ReadSource1FrameCount(path);
            Source1FrameCounts.Add(path, count);
            return count;
        }

        private int ReadSource1FrameCount(string path)
        {
            var fileSystem = RendererContext.Source1FileSystem;

            if (fileSystem == null || Source1TextureHeaders.TryRead(fileSystem, path) is not { } header)
            {
                return 1;
            }

            return Math.Max(1, (int)header.Frames);
        }

        /// <summary>A frame of a Source 1 texture. The first frame is the texture <see cref="GetTexture"/> returns.</summary>
        internal RenderTexture GetSource1TextureFrame(string path, bool srgbRead, int frame)
        {
            if (frame == 0)
            {
                return GetTexture(path, srgbRead, anisotropicFiltering: true, streaming: true);
            }

            var key = (path, srgbRead, frame);

            if (Source1FrameTextures.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var texture = LoadSource1Texture(path, srgbRead, frame);

            if (MaxTextureMaxAnisotropy >= 4)
            {
                texture.SetMaxAnisotropy(MaxTextureMaxAnisotropy);
            }

            Source1FrameTextures.Add(key, texture);
            return texture;
        }

        private static bool? ResolveHdrCondition(string symbol, bool highDynamicRange)
        {
            if (symbol.Equals("hdr", StringComparison.OrdinalIgnoreCase))
            {
                return highDynamicRange;
            }

            return null;
        }

        private static string ToHdrTexturePath(string path)
        {
            if (!path.EndsWith(Source1TextureExtension, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(Source1HdrTextureExtension, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return string.Concat(path.AsSpan(0, path.Length - Source1TextureExtension.Length), Source1HdrTextureExtension);
        }

        // A detail texture of a lightmapped surface that is flagged as a self shadowed bump map holds lightmap weights
        // when the surface has a bump map and a brightness otherwise, whatever blend mode the material asks for
        private static void ApplySource1SsbumpDetail(Source1FileSystem fileSystem, VrfMaterial material)
        {
            const int DetailBlendBumpWeights = 10;
            const int DetailBlendBrightness = 11;

            var shaderName = material.ShaderName;

            if (shaderName != VrfMaterial.Source1ShaderPrefix + "lightmappedgeneric"
                && shaderName != VrfMaterial.Source1ShaderPrefix + "worldvertextransition"
                && shaderName != VrfMaterial.Source1ShaderPrefix + "lightmappedtwotexture"
                && shaderName != VrfMaterial.Source1ShaderPrefix + "lightmappedreflective")
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

        private static bool BaseTextureHasAlpha(Source1FileSystem fileSystem, VmtFile vmt)
        {
            if (Source1TextureHeaders.TryReadBaseTexture(fileSystem, vmt) is not { } header)
            {
                return true;
            }

            return (header.Flags & (VtfFlags.ONEBITALPHA | VtfFlags.EIGHTBITALPHA)) != 0;
        }

        /// <summary>
        /// Creates another instance of a Source 1 material, so a mesh can bind textures or set uniforms the other users of the material do not share.
        /// </summary>
        /// <param name="source">The material to copy.</param>
        /// <param name="shaderArguments">The shader combos the copy is created with.</param>
        public RenderMaterial CreateSource1MaterialInstance(RenderMaterial source, Dictionary<string, byte>? shaderArguments)
        {
            ArgumentNullException.ThrowIfNull(source);

            var instance = CreateSource1Material(source.Material, shaderArguments);

            if (Source1ProxyBinders.TryGetValue(source, out var binder))
            {
                AddSource1Proxies(instance, binder.Proxies.Material);
            }

            return instance;
        }

        /// <summary>
        /// Creates a copy of a Source 1 material that another Source 1 shader draws, with the same parameters and textures.
        /// </summary>
        /// <param name="source">The material to copy.</param>
        /// <param name="shaderName">The lowercase Source 1 shader name, such as <c>detailsprite</c>.</param>
        public RenderMaterial CreateSource1MaterialWithShader(RenderMaterial source, string shaderName)
        {
            ArgumentNullException.ThrowIfNull(source);

            var vrfMaterial = new VrfMaterial
            {
                Resource = new Resource(ResourceType.Material),
                Name = source.Material.Name,
                ShaderName = VrfMaterial.Source1ShaderPrefix + shaderName,
            };

            foreach (var (name, value) in source.Material.IntParams)
            {
                vrfMaterial.IntParams[name] = value;
            }

            foreach (var (name, value) in source.Material.FloatParams)
            {
                vrfMaterial.FloatParams[name] = value;
            }

            foreach (var (name, value) in source.Material.VectorParams)
            {
                vrfMaterial.VectorParams[name] = value;
            }

            foreach (var (name, value) in source.Material.TextureParams)
            {
                vrfMaterial.TextureParams[name] = value;
            }

            return CreateSource1Material(vrfMaterial, null);
        }

        /// <summary>
        /// Creates the material an editor icon of a Source 1 game is drawn with: the base texture of a material, drawn by the
        /// shader of the tools sprites, whose vertices the Source 1 sprite shaders do not read.
        /// </summary>
        /// <param name="name">The path of the material, such as <c>materials/editor/info_target.vmt</c>.</param>
        /// <param name="shaderArguments">The shader combos the material is created with.</param>
        /// <returns>The material, or <see langword="null"/> when it or its base texture does not exist.</returns>
        internal RenderMaterial? CreateSource1ToolSprite(string name, Dictionary<string, byte>? shaderArguments)
        {
            var fileSystem = RendererContext.Source1FileSystem;

            if (fileSystem == null)
            {
                return null;
            }

            // Every entity of a class has the icon of the class
            var arguments = shaderArguments?.Select(static argument => argument.Key + "=" + argument.Value.ToString(CultureInfo.InvariantCulture)) ?? [];
            var key = (name, string.Join(',', arguments));

            if (Source1ToolSprites.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var material = LoadSource1ToolSprite(fileSystem, name, shaderArguments);

            Source1ToolSprites.Add(key, material);
            return material;
        }

        private RenderMaterial? LoadSource1ToolSprite(Source1FileSystem fileSystem, string name, Dictionary<string, byte>? shaderArguments)
        {
            try
            {
                var vmt = VmtFile.Load(name, fileSystem.OpenFile);
                var baseTexture = vmt.Values.String("$basetexture");

                if (string.IsNullOrWhiteSpace(baseTexture))
                {
                    return null;
                }

                var hasAlpha = BaseTextureHasAlpha(fileSystem, vmt);
                var vrfMaterial = new VrfMaterial
                {
                    Resource = new Resource(ResourceType.Material),
                    Name = name,
                    ShaderName = "tools_sprite.vfx",
                };

                // The shader reads the alpha of the color texture only when it is set to cut the sprite out
                vrfMaterial.IntParams["F_ALPHA_TEST"] = hasAlpha ? 1 : 0;
                vrfMaterial.TextureParams[hasAlpha ? "g_tColorB" : "g_tColorA"] = VmtFile.NormalizePath(baseTexture, Source1TextureExtension);

                return CreateSource1Material(vrfMaterial, shaderArguments, Source1ToolSpriteData);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                RendererContext.Logger.LogWarning("Failed to load icon {Material}: {Message}", name, e.Message);
                return null;
            }
        }

        private RenderMaterial CreateSource1Material(VrfMaterial vrfMaterial, Dictionary<string, byte>? shaderArguments, IShaderDataProvider? shaderDataProvider = null)
        {
            var material = CreateMaterial(vrfMaterial, shaderArguments, shaderDataProvider);
            // Samplers the material does not set read neutral values, so optional features such as the lightmap or a
            // second texture are a no-op instead of showing the error texture
            foreach (var uniformName in material.Shader.UniformNames)
            {
                if (!uniformName.StartsWith("g_t", StringComparison.Ordinal) || material.Textures.ContainsKey(uniformName))
                {
                    continue;
                }

                material.Textures[uniformName] = GetSource1DefaultTexture(uniformName, vrfMaterial);
            }

            return material;
        }

        private RenderTexture GetSource1DefaultTexture(string uniformName, VrfMaterial vrfMaterial)
        {
            if (uniformName == "g_tEnvMap")
            {
                return GetDefaultCube();
            }

            // A surface that only reflects has no color of its own
            if (uniformName == "g_tColor"
                && vrfMaterial.IntParams.GetValueOrDefault("F_ENVMAP") != 0
                && vrfMaterial.IntParams.GetValueOrDefault("F_PHONG") == 0)
            {
                return Source1DefaultBlack ??= CreateSolidTexture(0, 0, 0);
            }

            if (uniformName.Contains("Normal", StringComparison.Ordinal))
            {
                return GetDefaultNormal();
            }

            return GetDefaultColor();
        }

        private RenderTexture LoadSource1Texture(string name, bool srgbRead, int frame = 0)
        {
            var fileSystem = RendererContext.Source1FileSystem;
            var stream = fileSystem?.OpenFile(name);

            // A high dynamic range variant of a texture is optional
            if (stream == null && name.EndsWith(Source1HdrTextureExtension, StringComparison.OrdinalIgnoreCase))
            {
                var ldrName = string.Concat(name.AsSpan(0, name.Length - Source1HdrTextureExtension.Length), Source1TextureExtension);
                stream = fileSystem?.OpenFile(ldrName);
            }

            using var textureStream = stream;

            if (textureStream == null)
            {
                RendererContext.Logger.LogWarning("Source 1 texture {Texture} was not found", name);
                return GetErrorTexture();
            }

            try
            {
                var vtf = new VtfFile(textureStream);
                return LoadVtfTexture(vtf, System.IO.Path.GetFileName(name), srgbRead, frame);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException or UnexpectedMagicException)
            {
                RendererContext.Logger.LogWarning("Failed to load texture {Texture}: {Message}", name, e.Message);
                return GetErrorTexture();
            }
        }

        /// <summary>Uploads a Source 1 texture, all of its mip levels of one frame. Six face textures become cube maps.</summary>
        /// <param name="vtf">The parsed texture.</param>
        /// <param name="label">Debug name of the texture.</param>
        /// <param name="srgbRead">Whether to read the color data as sRGB. High dynamic range textures are always linear.</param>
        /// <param name="frame">The frame of an animated texture.</param>
        internal static RenderTexture LoadVtfTexture(VtfFile vtf, string label, bool srgbRead, int frame = 0)
        {
            ArgumentNullException.ThrowIfNull(vtf);

            var isCube = vtf.FaceCount >= 6 && (vtf.Flags & VtfFlags.ENVMAP) != 0;
            var target = isCube ? TextureTarget.TextureCubeMap : TextureTarget.Texture2D;
            var isHdr = vtf.IsHighDynamicRange;
            var levels = (vtf.Flags & VtfFlags.NOMIP) != 0 ? 1 : Math.Max(1, vtf.MipmapCount);

            var texture = new RenderTexture(target, vtf.Width, vtf.Height, 1, levels, label)
            {
                Reflectivity = new Vector4(vtf.Reflectivity, 1f),
                SpriteSheetData = vtf.GetSpriteSheetData(),
            };

            SizedInternalFormat internalFormat;
            PixelFormat pixelFormat;
            PixelType pixelType;

            if (isHdr)
            {
                internalFormat = SizedInternalFormat.Rgba32f;
                pixelFormat = PixelFormat.Rgba;
                pixelType = PixelType.Float;
            }
            else
            {
                internalFormat = srgbRead ? SizedInternalFormat.Srgb8Alpha8 : SizedInternalFormat.Rgba8;
                pixelFormat = PixelFormat.Bgra;
                pixelType = PixelType.UnsignedByte;
            }

            GL.TextureStorage2D(texture.Handle, levels, internalFormat, vtf.Width, vtf.Height);

            var faces = isCube ? 6 : 1;

            for (var mip = 0; mip < levels; mip++)
            {
                var width = Math.Max(1, vtf.Width >> mip);
                var height = Math.Max(1, vtf.Height >> mip);

                for (var face = 0; face < faces; face++)
                {
                    using var bitmap = vtf.DecodeMip(mip, frame, face);

                    if (isCube)
                    {
                        GL.TextureSubImage3D(texture.Handle, mip, 0, 0, face, width, height, 1, pixelFormat, pixelType, bitmap.GetPixels());
                    }
                    else
                    {
                        GL.TextureSubImage2D(texture.Handle, mip, 0, 0, width, height, pixelFormat, pixelType, bitmap.GetPixels());
                    }
                }
            }

            var pointSample = (vtf.Flags & VtfFlags.POINTSAMPLE) != 0;

            var minFilter = (pointSample, levels > 1) switch
            {
                (true, true) => TextureMinFilter.NearestMipmapNearest,
                (true, false) => TextureMinFilter.Nearest,
                (false, true) => TextureMinFilter.LinearMipmapLinear,
                _ => TextureMinFilter.Linear,
            };

            texture.SetFiltering(minFilter, pointSample ? TextureMagFilter.Nearest : TextureMagFilter.Linear);

            var clampS = isCube || (vtf.Flags & (VtfFlags.CLAMPS | VtfFlags.BORDER)) != 0 ? RsTextureAddressMode.Clamp : RsTextureAddressMode.Wrap;
            var clampT = isCube || (vtf.Flags & (VtfFlags.CLAMPT | VtfFlags.BORDER)) != 0 ? RsTextureAddressMode.Clamp : RsTextureAddressMode.Wrap;
            texture.SetWrapMode(clampS, clampT, isCube ? RsTextureAddressMode.Clamp : RsTextureAddressMode.Wrap);

            return texture;
        }
    }
}
