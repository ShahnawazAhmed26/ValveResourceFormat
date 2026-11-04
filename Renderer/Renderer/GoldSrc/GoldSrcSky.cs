using System.IO;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;
using ValveResourceFormat.GoldSrc;
using ValveResourceFormat.Renderer.SceneEnvironment;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>
    /// Creates the 2D sky of a GoldSrc map from the six images of its sky name.
    /// </summary>
    internal static class GoldSrcSky
    {
        private const string SkyTextureSamplerName = "g_tSkyTexture";

        private readonly record struct SkyFace(string Suffix, Vector3 Forward, Vector3 Right, Vector3 Up);

        private static readonly SkyFace[] SkyFaces =
        [
            new("ft", -Vector3.UnitY, -Vector3.UnitX, Vector3.UnitZ),
            new("bk", Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            new("lf", -Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
            new("rt", Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ),
            new("up", Vector3.UnitZ, -Vector3.UnitY, -Vector3.UnitX),
            new("dn", -Vector3.UnitZ, -Vector3.UnitY, Vector3.UnitX),
        ];

        /// <summary>Creates the sky from <c>gfx/env/&lt;name&gt;&lt;face&gt;.tga</c>.</summary>
        /// <param name="scene">The scene the sky is for.</param>
        /// <param name="fileSystem">Where the images are looked up.</param>
        /// <param name="skyName">The <c>skyname</c> of the map, such as <c>desert</c>.</param>
        /// <returns>The sky, or null when the map has none or an image is missing.</returns>
        public static SceneSkybox2D? Create(Scene scene, GoldSrcFileSystem fileSystem, string skyName)
        {
            ArgumentNullException.ThrowIfNull(scene);
            ArgumentNullException.ThrowIfNull(fileSystem);

            if (skyName.Length == 0)
            {
                return null;
            }

            var bitmaps = new SKBitmap[SkyFaces.Length];

            try
            {
                var size = 0;

                for (var i = 0; i < SkyFaces.Length; i++)
                {
                    var suffix = SkyFaces[i].Suffix;
                    var path = fileSystem.FindFile($"gfx/env/{skyName}{suffix}.tga");

                    if (path == null)
                    {
                        scene.RendererContext.Logger.LogWarning("Sky image gfx/env/{Name}{Face}.tga was not found", skyName, suffix);
                        return null;
                    }

                    bitmaps[i] = TgaImage.Decode(File.ReadAllBytes(path));
                    size = Math.Max(size, bitmaps[i].Width);
                }

                var cube = CreateSkyCube(size, bitmaps);
                var material = new RenderMaterial(scene.RendererContext.ShaderLoader.LoadShader("sky.vfx"));
                material.Textures[SkyTextureSamplerName] = cube;

                return new SceneSkybox2D(material);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnexpectedMagicException)
            {
                scene.RendererContext.Logger.LogWarning("Failed to load the sky {Name}: {Message}", skyName, e.Message);
                return null;
            }
            finally
            {
                foreach (var bitmap in bitmaps)
                {
                    bitmap?.Dispose();
                }
            }
        }

        // Resamples the six sky faces, in the order of SkyFaces, into a cube map
        private static RenderTexture CreateSkyCube(int size, SKBitmap[] bitmaps)
        {
            var texture = new RenderTexture(TextureTarget.TextureCubeMap, size, size, 1, 1, "GoldSrcSky");
            GL.TextureStorage2D(texture.Handle, 1, SizedInternalFormat.Srgb8Alpha8, size, size);

            var pixels = new byte[size * size * 4];

            for (var glFace = 0; glFace < 6; glFace++)
            {
                var texels = ResampleCubeFace(glFace, size, bitmaps);

                for (var i = 0; i < texels.Length; i++)
                {
                    var texel = texels[i];
                    var offset = i * 4;

                    pixels[offset] = texel.Blue;
                    pixels[offset + 1] = texel.Green;
                    pixels[offset + 2] = texel.Red;
                    pixels[offset + 3] = 255;
                }

                GL.TextureSubImage3D(texture.Handle, 0, 0, 0, glFace, size, size, 1, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
            }

            texture.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
            texture.SetWrapMode(RsTextureAddressMode.Clamp, RsTextureAddressMode.Clamp, RsTextureAddressMode.Clamp);

            return texture;
        }

        // The texel of the sky faces that each texel of a cube face looks at
        private static SKColor[] ResampleCubeFace(int glFace, int size, SKBitmap[] bitmaps)
        {
            // Every texel of a cube face looks furthest along the face's own axis, so one sky face fills all of it
            var faceIndex = FindSkyFace(GetCubeDirection(glFace, 0f, 0f));
            var face = SkyFaces[faceIndex];
            var bitmap = bitmaps[faceIndex];
            var sourcePixels = bitmap.Pixels;
            var texels = new SKColor[size * size];

            for (var y = 0; y < size; y++)
            {
                var v = (2f * (y + 0.5f) / size) - 1f;

                for (var x = 0; x < size; x++)
                {
                    var u = (2f * (x + 0.5f) / size) - 1f;
                    var direction = GetCubeDirection(glFace, u, v);
                    var forward = Vector3.Dot(direction, face.Forward);
                    var s = Vector3.Dot(direction, face.Right) / forward;
                    var t = Vector3.Dot(direction, face.Up) / forward;
                    var sourceX = Math.Clamp((int)((s + 1f) * 0.5f * bitmap.Width), 0, bitmap.Width - 1);
                    var sourceY = Math.Clamp((int)((1f - t) * 0.5f * bitmap.Height), 0, bitmap.Height - 1);

                    texels[(y * size) + x] = sourcePixels[(sourceY * bitmap.Width) + sourceX];
                }
            }

            return texels;
        }

        private static Vector3 GetCubeDirection(int glFace, float u, float v) => glFace switch
        {
            0 => new Vector3(1f, -v, -u),
            1 => new Vector3(-1f, -v, u),
            2 => new Vector3(u, 1f, v),
            3 => new Vector3(u, -1f, -v),
            4 => new Vector3(u, -v, 1f),
            _ => new Vector3(-u, -v, -1f),
        };

        private static int FindSkyFace(Vector3 direction)
        {
            var best = 0;
            var bestDot = float.MinValue;

            for (var i = 0; i < SkyFaces.Length; i++)
            {
                var dot = Vector3.Dot(direction, SkyFaces[i].Forward);

                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = i;
                }
            }

            return best;
        }
    }
}
