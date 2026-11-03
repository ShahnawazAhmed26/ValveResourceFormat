using System.IO;
using Microsoft.Extensions.Logging;
using ValveResourceFormat.GoldSrc;
using ValveResourceFormat.Renderer.Materials;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>How an entity that has a model is drawn, from its <c>rendermode</c> key.</summary>
    internal enum GoldSrcRenderMode
    {
        Normal = 0,
        Color = 1,
        Texture = 2,
        Glow = 3,
        Solid = 4,
        Additive = 5,
    }

    /// <summary>
    /// Holds what the studio model nodes of one scene share: the loaded model files, their textures and the
    /// materials made from them, so that many placed instances of a model use the same GPU resources.
    /// </summary>
    /// <remarks>
    /// Every node that uses the cache counts as a user, and the textures are deleted when the last one is deleted.
    /// </remarks>
    internal sealed class GoldSrcModelCache(Scene scene, GoldSrcFileSystem? fileSystem)
    {
        private const string SkinningCombo = "D_SKINNING";
        private const string ShaderName = "goldsrc/studio";

        private readonly record struct MaterialKey(GoldSrcMdlFile Source, int TextureIndex, GoldSrcRenderSettings Render, GoldSrcStudioLight Light);

        private readonly Dictionary<string, GoldSrcMdlFile?> models = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(GoldSrcMdlFile Source, int TextureIndex), RenderTexture> textures = [];
        private readonly Dictionary<MaterialKey, RenderMaterial> materials = [];
        private Shader? shader;
        private int users;

        /// <summary>Loads a model by its game relative path, once. Returns null when it cannot be found or read.</summary>
        public GoldSrcMdlFile? LoadModel(string gamePath)
        {
            if (models.TryGetValue(gamePath, out var cached))
            {
                return cached;
            }

            GoldSrcMdlFile? model = null;

            try
            {
                var path = fileSystem?.FindFile(gamePath);

                if (path != null)
                {
                    var name = Path.GetFileNameWithoutExtension(gamePath);
                    model = GoldSrcMdlFile.Load(File.ReadAllBytes(path), name, fileName => GoldSrcFileSystem.ReadCompanion(path, fileName, fileSystem));
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnexpectedMagicException)
            {
                scene.RendererContext.Logger.LogWarning("Failed to load model {Path}: {Message}", gamePath, e.Message);
            }

            models.Add(gamePath, model);
            return model;
        }

        /// <summary>Gets the material of a texture of a model for the way an entity draws it.</summary>
        /// <param name="source">The model file that holds the texture.</param>
        /// <param name="textureIndex">The index of the texture in <paramref name="source"/>.</param>
        /// <param name="render">How the entity that shows the model is drawn.</param>
        /// <param name="light">The light the model receives.</param>
        public RenderMaterial GetMaterial(GoldSrcMdlFile source, int textureIndex, GoldSrcRenderSettings render, GoldSrcStudioLight light)
        {
            var renderMode = render.Mode;

            var usesAmount = renderMode != GoldSrcRenderMode.Normal;
            var usesColor = renderMode == GoldSrcRenderMode.Color;
            render = render.WithoutUnused(usesAmount, usesColor);

            if (renderMode is GoldSrcRenderMode.Additive or GoldSrcRenderMode.Color)
            {
                light = GoldSrcStudioLight.Default;
            }

            var key = new MaterialKey(source, textureIndex, render, light);

            if (materials.TryGetValue(key, out var cached))
            {
                return cached;
            }

            shader ??= scene.RendererContext.ShaderLoader.LoadShader(ShaderName, (SkinningCombo, (byte)MeshSkinning.OneBone));

            var material = new RenderMaterial(shader);

            if ((uint)textureIndex < (uint)source.Textures.Length)
            {
                var flags = source.Textures[textureIndex].Flags;
                var isMasked = flags.HasFlag(GoldSrcTextureFlags.Masked);

                // The additive texture flag only counts when the entity is drawn normally
                var isAdditive = renderMode == GoldSrcRenderMode.Additive || (renderMode == GoldSrcRenderMode.Normal && flags.HasFlag(GoldSrcTextureFlags.Additive));
                var isTranslucent = renderMode != GoldSrcRenderMode.Normal;

                material.Textures["g_tColor"] = GetTexture(source, textureIndex);
                material.IntParams["F_ALPHA_TEST"] = isMasked ? 1 : 0;
                material.IntParams["F_TRANSLUCENT"] = isTranslucent ? 1 : 0;
                material.IntParams["F_ADDITIVE_BLEND"] = isAdditive ? 1 : 0;
                material.IntParams["F_CHROME"] = flags.HasFlag(GoldSrcTextureFlags.Chrome) ? 1 : 0;
                material.IntParams["F_FLAT_SHADE"] = flags.HasFlag(GoldSrcTextureFlags.FlatShade) ? 1 : 0;
                material.IntParams["F_NO_Z_PREPASS"] = isMasked || isTranslucent || isAdditive ? 1 : 0;
                material.IntParams["g_nRenderMode"] = (int)renderMode;
            }
            else
            {
                material.Textures["g_tColor"] = scene.RendererContext.MaterialLoader.GetDefaultColor();
            }

            var color = renderMode == GoldSrcRenderMode.Color ? render.Color / 256f : Vector3.One;
            material.VectorParams["g_vRenderColor"] = new Vector4(color, render.Alpha);
            material.FloatParams["g_flAmbientLight"] = light.Ambient;
            material.FloatParams["g_flShadeLight"] = light.Shade;
            material.VectorParams["g_vLightColor"] = new Vector4(light.Color, 1f);
            material.VectorParams["g_vLightDirection"] = new Vector4(light.Direction, 0f);

            material.LoadRenderState();
            materials.Add(key, material);
            return material;
        }

        private RenderTexture GetTexture(GoldSrcMdlFile source, int textureIndex)
        {
            if (textures.TryGetValue((source, textureIndex), out var cached))
            {
                return cached;
            }

            var texture = CreateTexture(source, source.Textures[textureIndex]);
            textures.Add((source, textureIndex), texture);
            return texture;
        }

        private static RenderTexture CreateTexture(GoldSrcMdlFile source, GoldSrcMdlTexture info)
        {
            var image = source.ReadTextureImage(info);
            var isMasked = info.Flags.HasFlag(GoldSrcTextureFlags.Masked);
            var useMips = !info.Flags.HasFlag(GoldSrcTextureFlags.NoMips);

            return GoldSrcTextureUpload.Create(image, info.Name, isMasked, useMips);
        }

        /// <summary>Registers a node that uses the cache.</summary>
        public void AddUser() => users++;

        /// <summary>Unregisters a node, and deletes the textures when no node is left.</summary>
        public void RemoveUser()
        {
            users--;

            if (users > 0)
            {
                return;
            }

            foreach (var texture in textures.Values)
            {
                texture.Delete();
            }

            textures.Clear();
            materials.Clear();
        }
    }
}
