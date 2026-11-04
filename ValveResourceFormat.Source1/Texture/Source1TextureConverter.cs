using VrfTexture = ValveResourceFormat.ResourceTypes.Texture;

namespace ValveResourceFormat.Source1.Texture;

/// <summary>Converts a Source 1 texture to an in-memory Source 2 texture resource.</summary>
public static class Source1TextureConverter
{
    /// <summary>
    /// Builds a texture resource from one frame of a Source 1 texture, with all of its mip levels.
    /// An environment map becomes a cube texture of its six faces.
    /// </summary>
    /// <param name="vtf">The parsed texture.</param>
    /// <param name="frame">The frame of an animated texture.</param>
    /// <returns>A resource whose data block is a <see cref="ResourceTypes.Texture"/>. The caller disposes it.</returns>
    /// <remarks>
    /// Formats that Source 2 stores identically are copied as they are. Every other format is decoded to
    /// <see cref="VTexFormat.BGRA8888"/>, or to <see cref="VTexFormat.RGBA32323232F"/> for floating point textures
    /// that have no matching format.
    /// </remarks>
    internal static Resource Convert(VtfFile vtf, int frame = 0)
    {
        ArgumentNullException.ThrowIfNull(vtf);

        var isCube = vtf.FaceCount >= 6 && (vtf.Flags & VtfFlags.ENVMAP) != 0;
        var faceCount = isCube ? 6 : 1;
        var mipLevels = (vtf.Flags & VtfFlags.NOMIP) != 0 ? 1 : Math.Max(1, vtf.MipmapCount);
        var flags = isCube ? VTexFlags.CUBE_TEXTURE : 0;

        var format = GetNativeFormat(vtf.Format);
        var decode = format == VTexFormat.UNKNOWN;

        if (decode)
        {
            format = vtf.IsHighDynamicRange ? VTexFormat.RGBA32323232F : VTexFormat.BGRA8888;
        }

        var pixelData = new List<ReadOnlyMemory<byte>>(mipLevels * faceCount);

        for (var mip = 0; mip < mipLevels; mip++)
        {
            for (var face = 0; face < faceCount; face++)
            {
                pixelData.Add(GetMipFace(vtf, mip, frame, face, decode));
            }
        }

        var reflectivity = new Vector4(vtf.Reflectivity, 1f);
        var sheet = vtf.GetSpriteSheetData();

        return VrfTexture.Create((ushort)vtf.Width, (ushort)vtf.Height, format, flags, reflectivity, pixelData, sheet);
    }

    private static VTexFormat GetNativeFormat(VtfFormat format) => format switch
    {
        VtfFormat.DXT1 => VTexFormat.DXT1,
        VtfFormat.DXT5 => VTexFormat.DXT5,
        VtfFormat.RGBA8888 => VTexFormat.RGBA8888,
        VtfFormat.BGRA8888 => VTexFormat.BGRA8888,
        VtfFormat.RGBA16161616F => VTexFormat.RGBA16161616F,
        VtfFormat.RGBA32323232F => VTexFormat.RGBA32323232F,
        _ => VTexFormat.UNKNOWN,
    };

    private static byte[] GetMipFace(VtfFile vtf, int mip, int frame, int face, bool decode)
    {
        if (!decode)
        {
            return vtf.GetHiResPixelData(mip, frame, face).ToArray();
        }

        using var bitmap = vtf.DecodeMip(mip, frame, face);
        return bitmap.GetPixelSpan().ToArray();
    }
}
