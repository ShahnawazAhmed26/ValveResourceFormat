using ValveResourceFormat.GoldSrc;
using ValveResourceFormat.IO.Fgd;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>
    /// Fills a scene with a GoldSrc map: the world, the brush and point entities, the light styles and the sky.
    /// </summary>
    internal static class GoldSrcMapScene
    {
        /// <summary>Adds a map to a scene.</summary>
        /// <param name="scene">The scene to fill.</param>
        /// <param name="bsp">The map.</param>
        /// <param name="fileSystem">Where the textures, models, sprites and sky images that the map does not embed are looked up, or <see langword="null"/> for none.</param>
        /// <param name="classes">The editor classes of the game, or <see langword="null"/> when they are not known.</param>
        /// <returns>The bounds of the world model.</returns>
        public static AABB Build(Scene scene, GoldSrcBspFile bsp, GoldSrcFileSystem? fileSystem, EntityClassTable? classes)
        {
            ArgumentNullException.ThrowIfNull(scene);
            ArgumentNullException.ThrowIfNull(bsp);

            var worldBounds = bsp.Models.Length > 0 ? new AABB(bsp.Models[0].Min, bsp.Models[0].Max) : new AABB();

            var lightStyles = new GoldSrcLightStyles(scene.RendererContext);
            lightStyles.SetPatterns(bsp.Entities);

            var animator = new GoldSrcTextureAnimator(scene, worldBounds, lightStyles) { Name = "GoldSrcAnimator" };

            new GoldSrcMapSceneBuilder(scene, bsp, fileSystem, classes, lightStyles, animator).Build();
            scene.Add(animator, true);

            new GoldSrcEntityBuilder(scene, bsp, fileSystem, classes, animator) { LightStyles = lightStyles }.Build();

            if (fileSystem != null)
            {
                var skyName = bsp.Entities.Count > 0 ? bsp.Entities[0].GetString("skyname") : string.Empty;
                scene.Skybox2D = GoldSrcSky.Create(scene, fileSystem, skyName);
            }

            return worldBounds;
        }
    }
}
