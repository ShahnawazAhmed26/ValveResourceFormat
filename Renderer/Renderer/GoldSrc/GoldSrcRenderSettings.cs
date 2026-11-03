using ValveResourceFormat.Source1.Bsp;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>How an entity is drawn: its render mode, opacity and render color.</summary>
    /// <param name="Mode">The render mode.</param>
    /// <param name="Amount">The render amount from 0 to 255.</param>
    /// <param name="Color">The render color as bytes from 0 to 255.</param>
    internal readonly record struct GoldSrcRenderSettings(GoldSrcRenderMode Mode, int Amount, Vector3 Color)
    {
        /// <summary>An entity that is drawn as it is.</summary>
        public static readonly GoldSrcRenderSettings Default = new(GoldSrcRenderMode.Normal, 255, Vector3.Zero);

        /// <summary>Gets the opacity from 0 to 1.</summary>
        public float Alpha => Amount / 255f;

        /// <summary>
        /// Drops the amount and color that a draw does not use, so that draws that look the same share a material.
        /// </summary>
        /// <param name="usesAmount">Whether the draw reads the amount.</param>
        /// <param name="usesColor">Whether the draw reads the color.</param>
        public GoldSrcRenderSettings WithoutUnused(bool usesAmount, bool usesColor)
        {
            return this with
            {
                Amount = usesAmount ? Amount : 255,
                Color = usesColor ? Color : Vector3.Zero,
            };
        }

        /// <summary>Reads the settings from the keys of an entity. A missing amount or color is 0.</summary>
        public static GoldSrcRenderSettings FromEntity(BspEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            var mode = (GoldSrcRenderMode)entity.GetInt("rendermode");
            var amount = Math.Clamp(entity.GetInt("renderamt"), 0, 255);
            var color = entity.Properties.ContainsKey("rendercolor") ? Vector3.Clamp(entity.GetVector3("rendercolor"), Vector3.Zero, new Vector3(255f)) : Vector3.Zero;

            // The amount of an entity that is drawn normally is not used
            if (mode == GoldSrcRenderMode.Normal)
            {
                amount = 255;
            }

            return new GoldSrcRenderSettings(mode, amount, color);
        }
    }
}
