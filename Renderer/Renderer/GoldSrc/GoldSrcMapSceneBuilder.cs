using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.GoldSrc;
using ValveResourceFormat.IO.Fgd;
using ValveResourceFormat.Renderer.Materials;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Source1.Bsp;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>
    /// Turns a GoldSrc BSP into scene nodes: the world and the brush models of entities, drawn with their
    /// textures and baked, animated lightmaps.
    /// </summary>
    internal sealed class GoldSrcMapSceneBuilder
    {
        private const string ShaderName = "goldsrc/world";

        private const int NoDrawEffect = 32;
        private const int FullbrightStyle = 254;
        private const float AnimatedTextureFramesPerSecond = 10f;

        private const int RandomTileGrid = 20;
        private const int RandomTileOrigin = 65536;

        private static readonly int[] RandomTileTable = CreateRandomTileTable();

        private const int SurfaceModeLit = 0;
        private const int SurfaceModeFullbright = 1;
        private const int SurfaceModeColor = 2;

        private static readonly HashSet<string> ToolTextures = new(StringComparer.OrdinalIgnoreCase)
        {
            "aaatrigger", "clip", "origin", "null", "hint", "skip", "bevel", "splitface", "solidhint",
            "contentwater", "contentempty", "contentsolid", "cliphull1", "cliphull2", "cliphull3", "noclip",
        };

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct WorldVertex(Vector3 position, Vector2 texCoord, Vector2 lightmapUV, Color32 styles)
        {
            [VertexAttribute(VertexSlot.Position)] public readonly Vector3 Position = position;
            [VertexAttribute(VertexSlot.TexCoord)] public readonly Vector2 TexCoord = texCoord;
            [VertexAttribute(VertexSlot.LightmapUV)] public readonly Vector2 LightmapUV = lightmapUV;
            [VertexAttribute(VertexSlot.Color)] public readonly Color32 Styles = styles;

            public static readonly VertexInputLayout InputLayout = VertexInputLayout.FromStruct<WorldVertex>();
        }

        private readonly record struct MaterialKey(int Texture, GoldSrcRenderSettings Render, bool IsTool);

        private sealed class MeshBucket
        {
            public List<WorldVertex> Vertices { get; } = [];
            public List<int> Indices { get; } = [];
            public AABB Bounds { get; set; }
            public string TextureName { get; init; } = string.Empty;
            public RenderMaterial Material { get; init; } = null!;
        }

        private readonly Scene scene;
        private readonly GoldSrcBspFile bsp;
        private readonly Func<string, IReadOnlyList<string>, PalettedImage?>? findInWads;
        private readonly EntityClassTable? classes;
        private readonly GoldSrcLightmapAtlas atlas;
        private readonly GoldSrcLightStyles lightStyles;
        private readonly RenderTexture[] lightmapTextures = new RenderTexture[GoldSrcLightmapAtlas.SlotCount];
        private readonly Dictionary<int, (RenderTexture Texture, int Width, int Height)> textures = [];
        private readonly Dictionary<MaterialKey, RenderMaterial> materials = [];
        private readonly Dictionary<string, int> textureIndices = new(StringComparer.OrdinalIgnoreCase);
        private readonly GoldSrcTextureAnimator animator;
        private readonly string[] textureNames;

        /// <summary>Creates a builder.</summary>
        /// <param name="scene">The scene to add the nodes to.</param>
        /// <param name="bsp">The map.</param>
        /// <param name="fileSystem">Where the textures that the map does not embed are looked up.</param>
        /// <param name="classes">The editor classes of the game, which tell how a brush entity turns.</param>
        /// <param name="lightStyles">The light styles the lightmaps are scaled by.</param>
        /// <param name="animator">The animator that plays the animated textures.</param>
        public GoldSrcMapSceneBuilder(Scene scene, GoldSrcBspFile bsp, GoldSrcFileSystem? fileSystem, EntityClassTable? classes, GoldSrcLightStyles lightStyles, GoldSrcTextureAnimator animator)
        {
            ArgumentNullException.ThrowIfNull(scene);
            ArgumentNullException.ThrowIfNull(bsp);
            ArgumentNullException.ThrowIfNull(lightStyles);
            ArgumentNullException.ThrowIfNull(animator);

            this.scene = scene;
            this.bsp = bsp;
            this.classes = classes;
            this.lightStyles = lightStyles;
            this.animator = animator;

            findInWads = fileSystem == null ? null : fileSystem.FindTexture;
            textureNames = [.. bsp.TextureNames];

            for (var i = 0; i < textureNames.Length; i++)
            {
                textureIndices.TryAdd(textureNames[i], i);
            }

            atlas = new GoldSrcLightmapAtlas(bsp);

            CreateLightmapTextures();
        }

        /// <summary>Adds the world and the brush entities to the scene.</summary>
        public void Build()
        {
            var models = bsp.Models;

            if (models.Length == 0)
            {
                return;
            }

            AddModel(0, Matrix4x4.Identity, GoldSrcRenderSettings.Default, forceTool: false);

            foreach (var entity in bsp.Entities)
            {
                var modelName = entity.GetString("model");

                if (modelName.Length < 2 || modelName[0] != '*' || !int.TryParse(modelName.AsSpan(1), CultureInfo.InvariantCulture, out var modelIndex))
                {
                    continue;
                }

                if (modelIndex <= 0 || modelIndex >= models.Length || (entity.GetInt("effects") & NoDrawEffect) != 0)
                {
                    continue;
                }

                var settings = GoldSrcRenderSettings.FromEntity(entity);

                // An entity without opacity is not drawn, whatever its mode
                if (settings.Amount == 0)
                {
                    continue;
                }

                var forceTool = entity.ClassName.StartsWith("trigger_", StringComparison.Ordinal);

                AddModel(modelIndex, GetBrushTransform(entity), settings, forceTool);
            }
        }

        // The model of a brush entity is relative to its origin, which is the center of its origin brush. A brush
        // entity turns about its origin by its angles, unless its class takes them as the way it moves.
        private Matrix4x4 GetBrushTransform(BspEntity entity)
        {
            var entityClass = classes?.FindClass(entity.ClassName);

            if (entityClass == null || GoldSrcEntityAngles.IsDirection(entityClass))
            {
                return Matrix4x4.CreateTranslation(entity.Origin);
            }

            return EntityTransformHelper.ToRigidTransformationMatrix(GoldSrcEntityAngles.Get(entity), entity.Origin);
        }

        private void AddModel(int modelIndex, Matrix4x4 transform, GoldSrcRenderSettings settings, bool forceTool)
        {
            var model = bsp.Models[modelIndex];
            var buckets = new Dictionary<MaterialKey, MeshBucket>();

            for (var i = 0; i < model.NumFaces; i++)
            {
                var faceIndex = model.FirstFace + i;

                if (faceIndex < 0 || faceIndex >= bsp.Faces.Length)
                {
                    break;
                }

                AddFace(faceIndex, settings, forceTool, buckets);
            }

            foreach (var bucket in buckets.Values)
            {
                AddMeshNode(modelIndex, bucket, transform);
            }
        }

        private void AddFace(int faceIndex, GoldSrcRenderSettings settings, bool forceTool, Dictionary<MaterialKey, MeshBucket> buckets)
        {
            var face = bsp.Faces[faceIndex];

            if (face.TexInfo < 0 || face.TexInfo >= bsp.TextureInfos.Length || face.NumEdges < 3)
            {
                return;
            }

            var info = bsp.TextureInfos[face.TexInfo];

            if (info.MipTexture < 0 || info.MipTexture >= textureNames.Length)
            {
                return;
            }

            var name = textureNames[info.MipTexture];

            if (name.StartsWith("sky", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var textureIndex = ResolveAnimationBase(ResolveRandomTile(info.MipTexture, faceIndex));
            var isTool = forceTool || ToolTextures.Contains(name);
            var key = new MaterialKey(textureIndex, GetMaterialSettings(settings, name), isTool);

            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new MeshBucket
                {
                    TextureName = textureNames[textureIndex],
                    Material = GetMaterial(key),
                };
                buckets.Add(key, bucket);
            }

            var (_, width, height) = GetTexture(textureIndex);
            var textureSize = new Vector2(width, height);
            var positions = bsp.GetFaceVertices(face);
            var hasLightmap = atlas.HasLightmap(faceIndex);
            var styles = GetStyles(face, hasLightmap);
            var first = bucket.Vertices.Count;

            ReverseIfBackFacing(positions, face);

            foreach (var position in positions)
            {
                var texCoord = info.GetTextureCoordinates(position);
                var lightmapUV = hasLightmap ? atlas.GetCoordinates(faceIndex, texCoord) : Vector2.Zero;

                bucket.Vertices.Add(new WorldVertex(position, texCoord, lightmapUV, styles));
                bucket.Bounds = bucket.Vertices.Count == 1 ? new AABB(position, position) : bucket.Bounds.Encapsulate(position);
            }

            for (var i = 1; i < positions.Length - 1; i++)
            {
                bucket.Indices.Add(first);
                bucket.Indices.Add(first + i);
                bucket.Indices.Add(first + i + 1);
            }
        }

        private static GoldSrcRenderSettings GetMaterialSettings(GoldSrcRenderSettings settings, string textureName)
        {
            var usesAmount = settings.Mode is GoldSrcRenderMode.Color or GoldSrcRenderMode.Texture or GoldSrcRenderMode.Glow or GoldSrcRenderMode.Additive;
            var usesColor = settings.Mode == GoldSrcRenderMode.Color || IsScrolling(textureName);

            return settings.WithoutUnused(usesAmount, usesColor);
        }

        private static bool IsScrolling(string textureName) => textureName.StartsWith("scroll", StringComparison.OrdinalIgnoreCase);

        // An entity sets the speed of a scrolling texture with its render color: the green and blue bytes are the
        // speed in sixteenths of a texel per second, and the texture moves backwards when red is 0
        private static float GetScrollSpeed(Vector3 renderColor)
        {
            var speed = ((((int)renderColor.Y) << 8) + (int)renderColor.Z) / 16f;
            return (int)renderColor.X == 0 ? -speed : speed;
        }

        private static Color32 GetStyles(in GoldSrcFace face, bool hasLightmap)
        {
            if (!hasLightmap)
            {
                return new Color32(FullbrightStyle, BspLightStyles.None, BspLightStyles.None, BspLightStyles.None);
            }

            return new Color32(face.GetLightStyle(0), face.GetLightStyle(1), face.GetLightStyle(2), face.GetLightStyle(3));
        }

        // The edge winding of a face depends on the side of its plane the face is on, so match the plane normal
        private void ReverseIfBackFacing(Vector3[] positions, in GoldSrcFace face)
        {
            var normal = bsp.Planes[face.PlaneNum].Normal;

            if (face.Side != 0)
            {
                normal = -normal;
            }

            var polygonNormal = Vector3.Zero;

            for (var i = 0; i < positions.Length; i++)
            {
                var current = positions[i];
                var next = positions[(i + 1) % positions.Length];

                polygonNormal.X += (current.Y - next.Y) * (current.Z + next.Z);
                polygonNormal.Y += (current.Z - next.Z) * (current.X + next.X);
                polygonNormal.Z += (current.X - next.X) * (current.Y + next.Y);
            }

            if (Vector3.Dot(polygonNormal, normal) < 0f)
            {
                Array.Reverse(positions);
            }
        }

        private void AddMeshNode(int modelIndex, MeshBucket bucket, Matrix4x4 transform)
        {
            if (bucket.Indices.Count == 0)
            {
                return;
            }

            var name = string.Create(CultureInfo.InvariantCulture, $"{bsp.Name}/model{modelIndex}/{bucket.TextureName}");
            var buffers = WorldVertex.InputLayout.CreateBuffers<WorldVertex, int>(CollectionsMarshal.AsSpan(bucket.Vertices), CollectionsMarshal.AsSpan(bucket.Indices));
            var mesh = RenderableMesh.CreateMesh(name, bucket.Material, buffers, bucket.Bounds, scene.RendererContext);
            mesh.PlaceDrawCall(mesh.DrawCallsOpaque[0]);

            var node = new MeshSceneNode(scene, mesh)
            {
                Name = name,
                Transform = transform,
            };

            scene.Add(node, false);
        }

        // Textures named -0 to -9 are variants of one texture. A face shows the variant its position in the grid of
        // texture-sized tiles picks, so neighboring tiles differ. The variant comes from a table of random numbers,
        // so any fixed table is as good.
        private int ResolveRandomTile(int textureIndex, int faceIndex)
        {
            var name = textureNames[textureIndex];

            if (!IsNumbered(name, '-'))
            {
                return textureIndex;
            }

            var variants = FindNumberedTextures('-', name.AsSpan(2));

            if (variants.Count < 2)
            {
                return textureIndex;
            }

            var (_, width, height) = GetTexture(textureIndex);
            var extent = bsp.LightmapExtents[faceIndex];

            // The origin keeps the tile numbers of faces left of or below the origin of the texture positive
            var tileX = (int)MathF.Floor(extent.MinS * 16f / width) + RandomTileOrigin;
            var tileY = (int)MathF.Floor(extent.MinT * 16f / height) + RandomTileOrigin;
            var random = RandomTileTable[((tileX % RandomTileGrid) * RandomTileGrid) + (tileY % RandomTileGrid)];

            return variants[random % variants.Count];
        }

        private static int[] CreateRandomTileTable()
        {
            var random = new Random(0);
            var table = new int[RandomTileGrid * RandomTileGrid];

            for (var i = 0; i < table.Length; i++)
            {
                table[i] = random.Next(0x7FFF);
            }

            return table;
        }

        // Whether a texture is named like one of a numbered series: a prefix, a digit and the name of the series
        private static bool IsNumbered(string name, char prefix)
            => name.Length >= 3 && name[0] == prefix && char.IsAsciiDigit(name[1]);

        // Finds the textures of a series from the digit 0 up to the first one the map does not have
        private List<int> FindNumberedTextures(char prefix, ReadOnlySpan<char> seriesName)
        {
            var found = new List<int>();

            for (var digit = 0; digit < 10; digit++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"{prefix}{digit}{seriesName}");

                if (!textureIndices.TryGetValue(name, out var index))
                {
                    break;
                }

                found.Add(index);
            }

            return found;
        }

        // Textures named +0 to +9 are the frames of an animation that every frame of draws, starting at +0
        private int ResolveAnimationBase(int textureIndex)
        {
            var name = textureNames[textureIndex];

            if (!IsNumbered(name, '+'))
            {
                return textureIndex;
            }

            var baseName = string.Concat("+0", name.AsSpan(2));
            return textureIndices.GetValueOrDefault(baseName, textureIndex);
        }

        private RenderTexture[] GetAnimationFrames(int baseIndex)
        {
            var name = textureNames[baseIndex];

            if (!IsNumbered(name, '+') || name[1] != '0')
            {
                return [];
            }

            var frameIndices = FindNumberedTextures('+', name.AsSpan(2));

            if (frameIndices.Count < 2)
            {
                return [];
            }

            var frames = new RenderTexture[frameIndices.Count];

            for (var i = 0; i < frames.Length; i++)
            {
                frames[i] = GetTexture(frameIndices[i]).Texture;
            }

            return frames;
        }

        private (RenderTexture Texture, int Width, int Height) GetTexture(int textureIndex)
        {
            if (textures.TryGetValue(textureIndex, out var cached))
            {
                return cached;
            }

            var name = textureNames[textureIndex];
            var image = bsp.ResolveTexture(textureIndex, findInWads);

            (RenderTexture, int, int) result;

            if (image == null)
            {
                scene.RendererContext.Logger.LogDebug("GoldSrc texture {Texture} was not found", name);
                result = (scene.RendererContext.MaterialLoader.GetErrorTexture(), 64, 64);
            }
            else
            {
                var texture = GoldSrcTextureUpload.Create(image, name, image.IsMasked, useMips: true);

                texture.SetWrapMode(RsTextureAddressMode.Wrap);
                animator.Own(texture);
                result = (texture, image.Width, image.Height);
            }

            textures.Add(textureIndex, result);
            return result;
        }

        private RenderMaterial GetMaterial(MaterialKey key)
        {
            if (materials.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var name = textureNames[key.Texture];
            var settings = key.Render;
            var (texture, width, height) = GetTexture(key.Texture);

            var isWater = name.StartsWith('!') || name.StartsWith("laser", StringComparison.OrdinalIgnoreCase) || name.StartsWith("water", StringComparison.OrdinalIgnoreCase);
            var isMasked = name.StartsWith('{') || settings.Mode == GoldSrcRenderMode.Solid;
            var isAdditive = settings.Mode == GoldSrcRenderMode.Additive;
            var isTranslucent = settings.Mode is GoldSrcRenderMode.Color or GoldSrcRenderMode.Texture or GoldSrcRenderMode.Glow or GoldSrcRenderMode.Additive;

            // Only the normal and solid modes use the lightmap
            var surfaceMode = settings.Mode switch
            {
                GoldSrcRenderMode.Color => SurfaceModeColor,
                GoldSrcRenderMode.Texture or GoldSrcRenderMode.Glow or GoldSrcRenderMode.Additive => SurfaceModeFullbright,
                _ => isWater ? SurfaceModeFullbright : SurfaceModeLit,
            };

            var material = new RenderMaterial(scene.RendererContext.ShaderLoader.LoadShader(ShaderName));
            material.Material.Name = name;

            material.IntParams["F_ALPHA_TEST"] = isMasked ? 1 : 0;
            material.IntParams["F_TRANSLUCENT"] = isTranslucent ? 1 : 0;
            material.IntParams["F_ADDITIVE_BLEND"] = isAdditive ? 1 : 0;
            material.IntParams["F_WATER"] = isWater ? 1 : 0;
            material.IntParams["g_nSurfaceMode"] = surfaceMode;
            material.VectorParams["g_vTextureSize"] = new Vector4(width, height, 0f, 0f);

            // The color is in the gamma space of the frame buffer, and the shader converts the texture to linear
            var color = settings.Mode == GoldSrcRenderMode.Color ? settings.Color / 256f : Vector3.One;
            material.VectorParams["g_vRenderColor"] = new Vector4(ColorSpace.SrgbGammaToLinear(color), settings.Alpha);

            if (IsScrolling(name))
            {
                material.FloatParams["g_flTextureScroll"] = GetScrollSpeed(settings.Color);
            }

            if (isMasked || isTranslucent)
            {
                material.IntParams["F_NO_Z_PREPASS"] = 1;
            }

            if (key.IsTool)
            {
                material.Material.IntAttributes["tools.toolsmaterial"] = 1;
            }

            material.LoadRenderState();

            material.Textures["g_tColor"] = texture;
            material.Textures["g_tLightStyles"] = lightStyles.Texture;

            for (var slot = 0; slot < lightmapTextures.Length; slot++)
            {
                material.Textures["g_tLightmap" + slot.ToString(CultureInfo.InvariantCulture)] = lightmapTextures[slot];
            }

            if (GetAnimationFrames(key.Texture) is { Length: > 0 } frames)
            {
                animator.Add(material, frames, AnimatedTextureFramesPerSecond, frames.Length);
            }

            materials.Add(key, material);
            return material;
        }

        private void CreateLightmapTextures()
        {
            var black = new byte[4];

            for (var slot = 0; slot < lightmapTextures.Length; slot++)
            {
                var pixels = atlas.Slots[slot];
                var width = pixels == null ? 1 : atlas.Width;
                var height = pixels == null ? 1 : atlas.Height;
                var label = string.Create(CultureInfo.InvariantCulture, $"GoldSrcLightmap{slot}");
                var texture = RenderTexture.Create(width, height, ImageFormat.RGBA8888, 1, label);

                GL.TextureSubImage2D(texture.Handle, 0, 0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels ?? black);

                texture.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
                texture.SetWrapMode(RsTextureAddressMode.Clamp);

                lightmapTextures[slot] = texture;
                animator.Own(texture);
            }
        }
    }
}
