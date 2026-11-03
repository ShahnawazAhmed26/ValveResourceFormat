using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Source1.Bsp;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>
    /// The brightness patterns of the light styles of a GoldSrc map, kept in a texture the world shader reads
    /// by style number.
    /// </summary>
    internal sealed class GoldSrcLightStyles
    {
        /// <summary>The number of style slots a face can refer to, which is every value of a byte.</summary>
        public const int StyleCount = 256;

        private const float StylesPerSecond = 10f;
        private const string OffPattern = "a";

        private readonly string?[] patterns = new string?[StyleCount];
        private readonly float[] values = new float[StyleCount];
        private int lastStep = -1;

        public GoldSrcLightStyles(RendererContext rendererContext)
        {
            ArgumentNullException.ThrowIfNull(rendererContext);

            for (var i = 0; i < BspLightStyles.DefaultPatternCount; i++)
            {
                patterns[i] = BspLightStyles.GetDefaultPattern(i);
            }

            Texture = RenderTexture.Create(StyleCount, 1, ImageFormat.R32F, "GoldSrcLightStyles");
            Texture.SetFiltering(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
            Texture.SetWrapMode(RsTextureAddressMode.Clamp);

            Update(0f);
        }

        /// <summary>Gets the texture that holds the brightness of every style in its red channel.</summary>
        public RenderTexture Texture { get; }

        /// <summary>Sets the pattern of a style, a string of letters where 'a' is dark and 'm' is normal.</summary>
        public void SetPattern(int style, string pattern)
        {
            if (style is < 0 or >= StyleCount - 1 || pattern.Length == 0)
            {
                return;
            }

            patterns[style] = pattern;
            lastStep = -1;
        }

        /// <summary>
        /// Sets the patterns of the styles that the lights of a map switch. A light that starts off is dark until
        /// something switches it on.
        /// </summary>
        public void SetPatterns(IReadOnlyList<BspEntity> entities)
        {
            ArgumentNullException.ThrowIfNull(entities);

            foreach (var entity in entities)
            {
                if (!BspLightStyles.TryGetLightStyle(entity, out var light))
                {
                    continue;
                }

                if (light.StartsDisabled)
                {
                    SetPattern(light.Style, OffPattern);
                }
                else if (light.Pattern.Length > 0)
                {
                    SetPattern(light.Style, light.Pattern);
                }
            }
        }

        /// <summary>Evaluates the patterns at a time and uploads the brightness when it changed.</summary>
        public void Update(float time)
        {
            var step = (int)(time * StylesPerSecond);

            if (step == lastStep)
            {
                return;
            }

            lastStep = step;

            for (var i = 0; i < StyleCount; i++)
            {
                values[i] = patterns[i] is { } pattern ? GetBrightness(pattern, step) : NoPatternScale * ScaleToBrightness;
            }

            Upload();
        }

        // A lightmap sample is scaled by 22 for each letter above 'a' and by 256 for a style without a pattern,
        // and the sum becomes one of 1023 light levels by dividing it by 64. A sample of 255 at brightness 1
        // reaches the top level.
        private const int LetterScale = 22;
        private const int NoPatternScale = 256;
        private const float ScaleToBrightness = 255f / (64f * 1023f);

        private static float GetBrightness(string pattern, int step)
        {
            var letter = pattern[step % pattern.Length];
            return (letter - 'a') * LetterScale * ScaleToBrightness;
        }

        /// <summary>Gets the integer factor a lightmap sample of a style is multiplied with, where 256 is full strength.</summary>
        public int GetSampleScale(int style)
        {
            var pattern = (uint)style < StyleCount ? patterns[style] : null;

            if (pattern == null)
            {
                return NoPatternScale;
            }

            return (pattern[Math.Max(lastStep, 0) % pattern.Length] - 'a') * LetterScale;
        }

        private void Upload()
        {
            GL.TextureSubImage2D(Texture.Handle, 0, 0, 0, StyleCount, 1, PixelFormat.Red, PixelType.Float, values);
        }
    }
}
