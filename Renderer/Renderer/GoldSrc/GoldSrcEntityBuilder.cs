using System.Globalization;
using System.IO;
using System.Linq;
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
    /// Adds the point entities of a GoldSrc map to a scene: sprites and studio models are drawn, every other entity
    /// gets a box the size of its editor class.
    /// </summary>
    internal sealed class GoldSrcEntityBuilder(Scene scene, GoldSrcBspFile bsp, GoldSrcFileSystem? fileSystem, EntityClassTable? classes, GoldSrcTextureAnimator animator)
    {
        private const int ConstantGlowEffect = 14;
        private const int MinAnimatedFrames = 2;
        private const float DefaultMarkerHalfSize = 8f;
        private static readonly Color32 DefaultMarkerColor = new(200, 200, 200, 255);

        private readonly record struct SpriteTexture(RenderTexture Texture, SprFile Sprite);

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct SpriteVertex(Vector3 center, Vector2 corner, Vector2 texCoord, Color32 color)
        {
            [VertexAttribute(VertexSlot.Position)] public readonly Vector3 Center = center;
            [VertexAttribute(VertexSlot.Normal)] public readonly Vector2 Corner = corner;
            [VertexAttribute(VertexSlot.TexCoord)] public readonly Vector2 TexCoord = texCoord;
            [VertexAttribute(VertexSlot.Color)] public readonly Color32 Color = color;

            public static readonly VertexInputLayout InputLayout = VertexInputLayout.FromStruct<SpriteVertex>();
        }

        private sealed class MarkerBox(Scene scene, Vector3 mins, Vector3 maxs, Color32 color) : ShapeSceneNode(scene, mins, maxs, color)
        {
            public override bool IsTranslucent => false;
        }

        private readonly GoldSrcModelCache models = new(scene, fileSystem);
        private readonly Dictionary<string, SpriteTexture?> spriteTextures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<SpriteMaterialKey, RenderMaterial> spriteMaterials = [];
        private readonly Dictionary<string, RenderTexture[]> spriteFrames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> cycleCounts = new(StringComparer.OrdinalIgnoreCase);
        private GoldSrcLightProbe? lightProbe;

        private readonly record struct SpriteMaterialKey(string Path, bool Additive, bool ColorOnly, bool Glow);

        /// <summary>Gets the light styles whose current brightness lights the models, or <see langword="null"/> for full brightness.</summary>
        public GoldSrcLightStyles? LightStyles { get; init; }

        /// <summary>Adds the nodes of every entity that is not a brush entity.</summary>
        public void Build()
        {
            foreach (var entity in bsp.Entities)
            {
                var className = entity.ClassName;
                var model = entity.GetString("model");

                if (className.Length == 0 || className == "worldspawn" || model.StartsWith('*'))
                {
                    continue;
                }

                var entityClass = classes?.FindClass(className);

                if (model.EndsWith(".spr", StringComparison.OrdinalIgnoreCase) && AddSprite(entity, model))
                {
                    continue;
                }

                if (AddModel(entity, model, entityClass))
                {
                    continue;
                }

                AddMarker(entity, entityClass);
            }
        }

        private bool AddModel(BspEntity entity, string modelKey, EntityClass? entityClass)
        {
            var path = GetModelPath(entity, modelKey, entityClass);

            if (path == null || models.LoadModel(path) is not { } model)
            {
                return false;
            }

            // A studio model turns by the negated pitch, unlike a brush model
            var angles = GoldSrcEntityAngles.Get(entity);
            angles.X = -angles.X;

            lightProbe ??= new GoldSrcLightProbe(bsp, LightStyles);

            // An entity that names no valid sequence plays the first one the model can
            var sequences = model.Sequences;
            var sequenceIndex = entity.GetInt("sequence");
            GoldSrcSequence? sequence = null;

            if ((uint)sequenceIndex < (uint)sequences.Length && model.HasAnimationData(sequences[sequenceIndex]))
            {
                sequence = sequences[sequenceIndex];
            }

            var renderSettings = GoldSrcRenderSettings.FromEntity(entity);
            var light = lightProbe.Sample(entity.Origin, model.Flags, entity.GetInt("effects"));

            var node = new GoldSrcModelSceneNode(scene, model, models, entity.GetInt("skin"), renderSettings, light, sequence)
            {
                Name = $"{bsp.Name}/{entity.ClassName}/{path}",
                Transform = EntityTransformHelper.ToRigidTransformationMatrix(angles, entity.Origin),
            };

            // A body of -1 asks the game to pick one at random
            node.SetBody(Math.Max(entity.GetInt("body"), 0));

            scene.Add(node, true);

            return true;
        }

        // An entity names its model in the model key, and the editor class of one that does not can name a default
        private string? GetModelPath(BspEntity entity, string modelKey, EntityClass? entityClass)
        {
            if (modelKey.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            {
                return modelKey;
            }

            if (entityClass == null)
            {
                return null;
            }

            foreach (var helper in entityClass.Helpers)
            {
                var path = GetHelperModel(entity, helper);

                if (path != null && path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            return null;
        }

        private string? GetHelperModel(BspEntity entity, HelperDeclaration helper)
        {
            if (helper.Name.Equals("studio", StringComparison.OrdinalIgnoreCase))
            {
                return helper.Arguments.Count > 0 ? helper.Arguments[0] : null;
            }

            if (helper.Name.Equals("studio_keyed", StringComparison.OrdinalIgnoreCase))
            {
                return GetKeyedModel(entity, helper.Arguments);
            }

            if (helper.Name.Equals("studio_cycle", StringComparison.OrdinalIgnoreCase))
            {
                return GetCycledModel(entity.ClassName, helper.Arguments);
            }

            return null;
        }

        // The key to read, then pairs of a value and the model it picks, then the model of any other value
        private static string? GetKeyedModel(BspEntity entity, IReadOnlyList<string> arguments)
        {
            if (arguments.Count < 2)
            {
                return null;
            }

            var value = entity.GetInt(arguments[0]);

            for (var i = 1; i + 1 < arguments.Count; i += 2)
            {
                if (int.TryParse(arguments[i], CultureInfo.InvariantCulture, out var candidate) && candidate == value)
                {
                    return arguments[i + 1];
                }
            }

            return arguments.Count % 2 == 0 ? arguments[^1] : null;
        }

        // The entities of a class take the models in turn, in the order the map lists them
        private string? GetCycledModel(string className, IReadOnlyList<string> arguments)
        {
            if (arguments.Count == 0)
            {
                return null;
            }

            cycleCounts.TryGetValue(className, out var count);
            cycleCounts[className] = count + 1;

            return arguments[count % arguments.Count];
        }

        private bool AddSprite(BspEntity entity, string path)
        {
            var spriteTexture = GetSpriteTexture(path);

            if (spriteTexture is not { } loaded)
            {
                return false;
            }

            var sprite = loaded.Sprite;
            var frame = sprite.Frames[0];
            var settings = GoldSrcRenderSettings.FromEntity(entity);

            // A sprite without opacity is not drawn
            if (settings.Amount == 0)
            {
                return true;
            }

            var additive = settings.Mode is GoldSrcRenderMode.Glow or GoldSrcRenderMode.Additive || sprite.TextureFormat == SprTextureFormat.Additive;
            var colorOnly = settings.Mode == GoldSrcRenderMode.Color;

            // A glow keeps its size on the screen and fades with distance, which the vertex shader does from the distance to the camera
            var isGlow = settings.Mode == GoldSrcRenderMode.Glow && entity.GetInt("renderfx") != ConstantGlowEffect;

            // A sprite that has a rate and more than two frames is animated, and repeats. Its material is its own,
            // as the animation changes its texture.
            var framesPerSecond = entity.GetFloat("framerate");

            // Oriented sprites are drawn facing the view as well
            var orientation = sprite.Type switch
            {
                SprType.ParallelUpright => 0,
                SprType.FacingUpright => 1,
                _ => 2,
            };

            RenderMaterial material;

            if (framesPerSecond > 0f && sprite.Frames.Length > MinAnimatedFrames)
            {
                var frames = GetSpriteFrames(path, loaded);

                material = CreateSpriteMaterial(path, loaded.Texture, additive, colorOnly, isGlow, orientation);
                animator.Add(material, frames, framesPerSecond, frames.Length - 1);
            }
            else
            {
                material = GetSpriteMaterial(path, loaded.Texture, additive, colorOnly, isGlow, orientation);
            }

            var scale = isGlow ? 1f : entity.GetFloat("scale", 1f);
            scale = scale > 0f ? scale : 1f;

            // A sprite without a render color is drawn white. The render color is in gamma space and the texture is
            // decoded to linear, so the vertex color is converted to linear too.
            var rgb = settings.Color == Vector3.Zero ? Vector3.One : settings.Color / 255f;
            var alpha = 1f;

            if (additive)
            {
                rgb *= settings.Alpha;
            }
            else
            {
                alpha = settings.Alpha;
            }

            var linear = ColorSpace.SrgbGammaToLinear(rgb);
            var color = new Color32(linear.X, linear.Y, linear.Z, alpha);

            var left = frame.OriginX * scale;
            var right = (frame.OriginX + frame.Width) * scale;
            var top = frame.OriginY * scale;
            var bottom = (frame.OriginY - frame.Height) * scale;

            var center = entity.Origin;
            Vector2[] corners = [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];
            Vector2[] texCoords = [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)];
            var vertices = new SpriteVertex[4];
            var extent = Math.Max(Math.Abs(right - left), Math.Abs(top - bottom));

            for (var i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new SpriteVertex(center, corners[i], texCoords[i], color);
            }

            int[] indices = [0, 1, 2, 0, 2, 3];
            var name = string.Create(CultureInfo.InvariantCulture, $"{bsp.Name}/{entity.ClassName}/{path}");
            var buffers = SpriteVertex.InputLayout.CreateBuffers<SpriteVertex, int>(vertices, indices);
            var bounds = new AABB(center - new Vector3(extent), center + new Vector3(extent));
            var mesh = RenderableMesh.CreateMesh(name, material, buffers, bounds, scene.RendererContext);
            mesh.PlaceDrawCall(mesh.DrawCallsOpaque[0]);

            var node = new MeshSceneNode(scene, mesh) { Name = name };
            scene.Add(node, false);

            return true;
        }

        private RenderMaterial GetSpriteMaterial(string path, RenderTexture texture, bool additive, bool colorOnly, bool glow, int orientation)
        {
            var key = new SpriteMaterialKey(path, additive, colorOnly, glow);

            if (spriteMaterials.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var material = CreateSpriteMaterial(path, texture, additive, colorOnly, glow, orientation);

            spriteMaterials.Add(key, material);
            return material;
        }

        // The textures of all frames of a sprite, the first being the one that is already loaded
        private RenderTexture[] GetSpriteFrames(string path, SpriteTexture loaded)
        {
            if (spriteFrames.TryGetValue(path, out var frames))
            {
                return frames;
            }

            frames = new RenderTexture[loaded.Sprite.Frames.Length];
            frames[0] = loaded.Texture;

            for (var i = 1; i < frames.Length; i++)
            {
                frames[i] = CreateSpriteTexture(loaded.Sprite, i, path);
            }

            spriteFrames.Add(path, frames);
            return frames;
        }

        private RenderMaterial CreateSpriteMaterial(string path, RenderTexture texture, bool additive, bool colorOnly, bool glow, int orientation)
        {
            var material = new RenderMaterial(scene.RendererContext.ShaderLoader.LoadShader("goldsrc/sprite"));
            material.Material.Name = path;
            material.IntParams["F_TRANSLUCENT"] = 1;
            material.IntParams["F_ADDITIVE_BLEND"] = additive ? 1 : 0;
            material.IntParams["F_COLOR_ONLY"] = colorOnly ? 1 : 0;
            material.IntParams["F_GLOW"] = glow ? 1 : 0;
            material.IntParams["g_nOrientation"] = orientation;
            material.IntParams["F_RENDER_BACKFACES"] = 1;
            material.IntParams["F_NO_Z_PREPASS"] = 1;
            material.LoadRenderState();
            material.Textures["g_tColor"] = texture;

            return material;
        }

        private SpriteTexture? GetSpriteTexture(string path)
        {
            if (spriteTextures.TryGetValue(path, out var cached))
            {
                return cached;
            }

            SpriteTexture? result = null;

            try
            {
                if (fileSystem?.FindFile(path) is { } file)
                {
                    using var stream = File.OpenRead(file);
                    var sprite = SprFile.Open(stream);
                    result = new SpriteTexture(CreateSpriteTexture(sprite, 0, path), sprite);
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnexpectedMagicException)
            {
                scene.RendererContext.Logger.LogWarning("Failed to load sprite {Path}: {Message}", path, e.Message);
            }

            spriteTextures.Add(path, result);
            return result;
        }

        private RenderTexture CreateSpriteTexture(SprFile sprite, int frameIndex, string label)
        {
            var frame = sprite.Frames[frameIndex];
            var rgba = sprite.ToRgba(frameIndex);
            var texture = RenderTexture.Create(frame.Width, frame.Height, ImageFormat.RGBA8888, 1, label, srgb: true);

            GL.TextureSubImage2D(texture.Handle, 0, 0, 0, frame.Width, frame.Height, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
            texture.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
            texture.SetWrapMode(RsTextureAddressMode.Clamp);
            animator.Own(texture);

            return texture;
        }

        private void AddMarker(BspEntity entity, EntityClass? entityClass)
        {
            var (mins, maxs) = entityClass?.Size ?? (new Vector3(-DefaultMarkerHalfSize), new Vector3(DefaultMarkerHalfSize));
            var color = GetMarkerColor(entityClass);

            var node = new MarkerBox(scene, mins, maxs, color)
            {
                Name = $"{bsp.Name}/{entity.ClassName}",
                Transform = Matrix4x4.CreateTranslation(entity.Origin),
            };

            scene.Add(node, false);
        }

        private static Color32 GetMarkerColor(EntityClass? entityClass)
        {
            var declaration = entityClass?.Helpers.FirstOrDefault(static helper => helper.Name.Equals("color", StringComparison.OrdinalIgnoreCase));

            if (declaration?.Arguments is not [var red, var green, var blue]
                || !byte.TryParse(red, CultureInfo.InvariantCulture, out var r)
                || !byte.TryParse(green, CultureInfo.InvariantCulture, out var g)
                || !byte.TryParse(blue, CultureInfo.InvariantCulture, out var b))
            {
                return DefaultMarkerColor;
            }

            return new Color32(r, g, b, 255);
        }
    }
}
