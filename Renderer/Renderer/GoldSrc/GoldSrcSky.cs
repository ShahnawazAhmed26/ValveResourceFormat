using System.IO;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using ValveResourceFormat.GoldSrc;
using ValveResourceFormat.Renderer.SceneEnvironment;
using ValveResourceFormat.Renderer.Source1;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>
    /// Creates the 2D sky of a GoldSrc map from the six images of its sky name.
    /// </summary>
    internal static class GoldSrcSky
    {
        // The faces in the order the cube map builder takes them
        private static readonly string[] FaceSuffixes = ["ft", "bk", "lf", "rt", "up", "dn"];

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

            var bitmaps = new SKBitmap[FaceSuffixes.Length];

            try
            {
                var size = 0;

                for (var i = 0; i < FaceSuffixes.Length; i++)
                {
                    var path = fileSystem.FindFile($"gfx/env/{skyName}{FaceSuffixes[i]}.tga");

                    if (path == null)
                    {
                        scene.RendererContext.Logger.LogWarning("Sky image gfx/env/{Name}{Face}.tga was not found", skyName, FaceSuffixes[i]);
                        return null;
                    }

                    bitmaps[i] = TgaImage.Decode(File.ReadAllBytes(path));
                    size = Math.Max(size, bitmaps[i].Width);
                }

                var cube = Source1MapSceneBuilder.CreateSkyCube(size, bitmaps);

                return Source1MapSceneBuilder.CreateSkybox2D(scene, cube);
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
    }
}
