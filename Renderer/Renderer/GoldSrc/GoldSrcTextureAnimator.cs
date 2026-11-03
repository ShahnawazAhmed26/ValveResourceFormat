using ValveResourceFormat.Renderer.Materials;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>
    /// Advances the animated textures of a GoldSrc map, the world textures and the sprites, and the light styles
    /// that go with them. It draws nothing, and deletes the textures it owns with the scene.
    /// </summary>
    internal sealed class GoldSrcTextureAnimator : SceneNode
    {
        private const string ColorSamplerName = "g_tColor";

        private sealed class Entry(RenderMaterial material, RenderTexture[] frames, float framesPerSecond, int cycleLength)
        {
            public RenderMaterial Material { get; } = material;
            public RenderTexture[] Frames { get; } = frames;
            public float FramesPerSecond { get; } = framesPerSecond;
            public int CycleLength { get; } = cycleLength;
            public int FrameIndex { get; set; } = -1;
        }

        private readonly GoldSrcLightStyles? lightStyles;
        private readonly List<Entry> entries = [];
        private readonly List<RenderTexture> ownedTextures = [];

        /// <summary>Creates an animator.</summary>
        /// <param name="scene">The scene the animator is in.</param>
        /// <param name="bounds">The bounds of the animator, which has to stay in view for it to update.</param>
        /// <param name="lightStyles">The light styles to evaluate on every update, or <see langword="null"/> for none.</param>
        public GoldSrcTextureAnimator(Scene scene, AABB bounds, GoldSrcLightStyles? lightStyles = null) : base(scene)
        {
            this.lightStyles = lightStyles;
            LocalBoundingBox = bounds;

            if (lightStyles != null)
            {
                ownedTextures.Add(lightStyles.Texture);
            }
        }

        /// <summary>Makes the animator delete a texture when the scene deletes it.</summary>
        /// <param name="texture">The texture to delete.</param>
        public void Own(RenderTexture texture) => ownedTextures.Add(texture);

        /// <summary>Plays the frames of an animation as the color texture of a material.</summary>
        /// <param name="material">The material to animate.</param>
        /// <param name="frames">The textures of the frames.</param>
        /// <param name="framesPerSecond">The rate of the animation.</param>
        /// <param name="cycleLength">The number of frames shown before the animation starts over, which is less than the number of textures when the last one only marks the wrap point.</param>
        public void Add(RenderMaterial material, RenderTexture[] frames, float framesPerSecond, int cycleLength)
        {
            entries.Add(new Entry(material, frames, framesPerSecond, cycleLength));
        }

        /// <inheritdoc/>
        public override void Update(Scene.UpdateContext context)
        {
            lightStyles?.Update(context.Uptime);

            foreach (var entry in entries)
            {
                var position = MathF.Max(context.Uptime * entry.FramesPerSecond, 0f) % entry.CycleLength;
                var frameIndex = (int)position;

                if (frameIndex == entry.FrameIndex)
                {
                    continue;
                }

                entry.FrameIndex = frameIndex;
                entry.Material.Textures[ColorSamplerName] = entry.Frames[frameIndex];
            }
        }

        /// <inheritdoc/>
        public override void Delete()
        {
            foreach (var texture in ownedTextures)
            {
                texture.Delete();
            }

            ownedTextures.Clear();
        }
    }
}
