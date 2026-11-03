using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.GoldSrc;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>
    /// Uploads paletted images as RGBA textures. The texels are not sRGB: the shaders shade in the gamma space of the texture.
    /// </summary>
    internal static class GoldSrcTextureUpload
    {
        /// <summary>Creates a texture from an image.</summary>
        /// <param name="image">The image.</param>
        /// <param name="label">The label of the texture in graphics debuggers.</param>
        /// <param name="isMasked">Whether the last palette color is transparent.</param>
        /// <param name="useMips">Whether the texture is minified with mips. The mips of the image are used, or generated when it has only one.</param>
        /// <returns>The texture, with filtering set.</returns>
        public static RenderTexture Create(PalettedImage image, string label, bool isMasked, bool useMips)
        {
            var maxLevels = BitOperations.Log2((uint)Math.Max(image.Width, image.Height)) + 1;
            var generateMips = useMips && image.Mips.Length == 1;
            var uploadedLevels = useMips ? Math.Min(image.Mips.Length, maxLevels) : 1;
            var levels = generateMips ? maxLevels : uploadedLevels;
            var texture = RenderTexture.Create(image.Width, image.Height, ImageFormat.RGBA8888, levels, label);

            for (var mip = 0; mip < uploadedLevels; mip++)
            {
                var rgba = image.ToRgba(mip, isMasked);
                var width = Math.Max(1, image.Width >> mip);
                var height = Math.Max(1, image.Height >> mip);

                GL.TextureSubImage2D(texture.Handle, mip, 0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
            }

            if (generateMips)
            {
                GL.GenerateTextureMipmap(texture.Handle);
            }

            var minFilter = levels > 1 ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear;
            texture.SetFiltering(minFilter, TextureMagFilter.Linear);

            return texture;
        }
    }
}
