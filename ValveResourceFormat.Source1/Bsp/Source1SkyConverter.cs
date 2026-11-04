using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using ValveResourceFormat.Source1.FileSystem;
using ValveResourceFormat.Source1.Material;
using ValveResourceFormat.Source1.Texture;
using VrfTexture = ValveResourceFormat.ResourceTypes.Texture;

namespace ValveResourceFormat.Source1.Bsp;

/// <summary>The sky material of a map and the cube texture it samples.</summary>
/// <param name="MaterialPath">The path of the sky material, without the <c>_c</c> suffix.</param>
/// <param name="Material">The sky material.</param>
/// <param name="TexturePath">The path of the cube texture, without the <c>_c</c> suffix.</param>
/// <param name="Texture">The cube texture.</param>
/// <param name="Tint">The linear color the sky is multiplied with, the <c>$color</c> of its materials.</param>
internal sealed record Source1Sky(string MaterialPath, Resource Material, string TexturePath, Resource Texture, Vector3 Tint);

/// <summary>
/// Builds the 2D sky of a Source 1 map from the six <c>materials/skybox/</c> textures of its <c>skyname</c> as a cube texture.
/// </summary>
internal static class Source1SkyConverter
{
    // A compressed HDR sky texel is a linear color times a scale in its alpha, which the sky shader does not know of
    private const float CompressedHdrScale = 8f;

    private readonly record struct SkyFace(string Suffix, Vector3 Forward, Vector3 Right, Vector3 Up);

    private readonly record struct SkyTexture(string? Path, string? HdrPath, Matrix4x4 Transform, Vector3 Color);

    private readonly record struct SkyImage(int Width, int Height, Vector4[] Texels);

    private static readonly SkyFace[] SkyFaces =
    [
        new("ft", -Vector3.UnitY, -Vector3.UnitX, Vector3.UnitZ),
        new("bk", Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
        new("lf", -Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
        new("rt", Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ),
        new("up", Vector3.UnitZ, -Vector3.UnitY, -Vector3.UnitX),
        new("dn", -Vector3.UnitZ, -Vector3.UnitY, Vector3.UnitX),
    ];

    /// <summary>Creates the sky of a <c>skyname</c>.</summary>
    /// <param name="fileSystem">Where the sky textures are read from.</param>
    /// <param name="skyName">The sky name, such as <c>sky_day01_01</c>.</param>
    /// <param name="highDynamicRange">
    /// Whether to use the <c>$hdrcompressedtexture</c> or floating point <c>$hdrbasetexture</c> of the faces, as the
    /// engine does for a map with HDR lighting. The low dynamic range faces are used when a face has none.
    /// </param>
    /// <param name="logger">Receives why a sky could not be made.</param>
    /// <returns>The sky, or null when a face is missing.</returns>
    public static Source1Sky? Create(Source1FileSystem fileSystem, string skyName, bool highDynamicRange, ILogger logger)
    {
        if (skyName.Length == 0)
        {
            return null;
        }

        var skyTextures = new SkyTexture[SkyFaces.Length];
        var allHaveHdr = true;
        var allHaveLdr = true;

        for (var i = 0; i < SkyFaces.Length; i++)
        {
            if (GetSkyTexture(fileSystem, skyName, SkyFaces[i].Suffix) is not { } skyTexture)
            {
                logger.LogWarning("Sky texture of materials/skybox/{Name}{Face}.vmt was not found", skyName, SkyFaces[i].Suffix);
                return null;
            }

            skyTextures[i] = skyTexture;
            allHaveHdr &= skyTexture.HdrPath != null;
            allHaveLdr &= skyTexture.Path != null;
        }

        // A sky that only has high dynamic range faces is drawn with them even when the map is not
        var useHdr = allHaveHdr && (highDynamicRange || !allHaveLdr);

        if (!useHdr && !allHaveLdr)
        {
            for (var i = 0; i < SkyFaces.Length; i++)
            {
                if (skyTextures[i].Path == null)
                {
                    logger.LogWarning("Sky texture of materials/skybox/{Name}{Face}.vmt has no low dynamic range texture", skyName, SkyFaces[i].Suffix);
                    return null;
                }
            }
        }

        var color = skyTextures[0].Color;

        foreach (var skyTexture in skyTextures)
        {
            if (skyTexture.Color != color)
            {
                logger.LogWarning("Sky {Name} has faces with different $color, using the one of the first face", skyName);
                break;
            }
        }

        var images = new SkyImage[SkyFaces.Length];

        try
        {
            var size = 0;

            for (var i = 0; i < SkyFaces.Length; i++)
            {
                var path = useHdr ? skyTextures[i].HdrPath! : skyTextures[i].Path!;
                using var stream = fileSystem.OpenFile(path);

                if (stream == null)
                {
                    logger.LogWarning("Sky texture {Path} was not found", path);
                    return null;
                }

                var vtf = new VtfFile(stream);

                if (vtf.IsHighDynamicRange && !useHdr)
                {
                    logger.LogWarning("Sky texture {Name}{Face} is high dynamic range", skyName, SkyFaces[i].Suffix);
                    return null;
                }

                using var bitmap = vtf.DecodeMip();

                images[i] = ReadImage(bitmap, vtf.IsHighDynamicRange, useHdr);
                size = Math.Max(size, bitmap.Width);
            }

            var materialPath = $"materials/skybox/{skyName}.vmat";
            var texturePath = $"materials/skybox/{skyName}.vtex";

#pragma warning disable CA2000 // The resources are owned by the converted map
            var cube = CreateCube(size, images, skyTextures, useHdr);
            var material = Source1MaterialConverter.CreateSkyMaterial(materialPath, texturePath);
#pragma warning restore CA2000

            return new Source1Sky(materialPath, material, texturePath, cube, color);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnexpectedMagicException)
        {
            logger.LogWarning("Failed to load sky {Name}: {Message}", skyName, e.Message);
            return null;
        }
    }

    private static SkyTexture? GetSkyTexture(Source1FileSystem fileSystem, string skyName, string suffix)
    {
        try
        {
            var vmt = VmtFile.Load($"materials/skybox/{skyName}{suffix}.vmt", fileSystem.OpenFile);
            var baseTexture = vmt.Values.String("$basetexture");
            var hdrBaseTexture = vmt.Values.String("$hdrbasetexture");
            var compressedHdrTexture = vmt.Values.String("$hdrcompressedtexture");

            // A floating point $hdrbasetexture is the high dynamic range texture of a face that has no compressed one
            var isHdrBaseFloat = hdrBaseTexture != null && IsFloatTexture(fileSystem, Source1Paths.ToTexturePath(hdrBaseTexture));
            var path = baseTexture ?? (isHdrBaseFloat ? null : hdrBaseTexture);
            var hdrPath = compressedHdrTexture ?? (isHdrBaseFloat ? hdrBaseTexture : null);

            if (path == null && hdrPath == null)
            {
                return null;
            }

            var color = vmt.Values.Vector("$color", Vector3.One);

            return new SkyTexture(
                path == null ? null : Source1Paths.ToTexturePath(path),
                hdrPath == null ? null : Source1Paths.ToTexturePath(hdrPath),
                vmt.Values.Transform("$basetexturetransform"),
                color);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static bool IsFloatTexture(Source1FileSystem fileSystem, string texturePath)
    {
        var header = Source1TextureHeaders.TryRead(fileSystem, texturePath);

        return header != null && VtfFile.IsHighDynamicRangeFormat(header.Value.HiResFormat);
    }

    // The texels of a face as the values the cube stores: a byte color as 0 to 1, a floating point color as it is
    // and a compressed HDR color as the linear color it encodes
    private static SkyImage ReadImage(SKBitmap bitmap, bool isFloat, bool isHdr)
    {
        var texels = new Vector4[bitmap.Width * bitmap.Height];

        if (isFloat)
        {
            using var pixmap = bitmap.PeekPixels();
            var floats = pixmap.GetPixelSpan<float>();

            for (var i = 0; i < texels.Length; i++)
            {
                var offset = i * 4;

                texels[i] = new Vector4(floats[offset], floats[offset + 1], floats[offset + 2], 1f);
            }
        }
        else
        {
            var pixels = bitmap.Pixels;

            for (var i = 0; i < texels.Length; i++)
            {
                var pixel = pixels[i];
                var scale = isHdr ? pixel.Alpha / 255f * CompressedHdrScale / 255f : 1f / 255f;

                texels[i] = new Vector4(pixel.Red * scale, pixel.Green * scale, pixel.Blue * scale, 1f);
            }
        }

        return new SkyImage(bitmap.Width, bitmap.Height, texels);
    }

    private static Resource CreateCube(int size, SkyImage[] images, SkyTexture[] skyTextures, bool isHdr)
    {
        var faces = new ReadOnlyMemory<byte>[SkyFaces.Length];

        for (var glFace = 0; glFace < faces.Length; glFace++)
        {
            var texels = ResampleCubeFace(glFace, size, images, skyTextures);
            faces[glFace] = isHdr ? ToHalfPixels(texels) : ToBytePixels(texels);
        }

        var format = isHdr ? VTexFormat.RGBA16161616F : VTexFormat.RGBA8888;

        return VrfTexture.Create((ushort)size, (ushort)size, format, VTexFlags.CUBE_TEXTURE, Vector4.One, faces);
    }

    private static byte[] ToBytePixels(Vector4[] texels)
    {
        var pixels = new byte[texels.Length * 4];

        for (var i = 0; i < texels.Length; i++)
        {
            var texel = texels[i];
            var offset = i * 4;

            pixels[offset] = (byte)MathF.Round(texel.X * 255f);
            pixels[offset + 1] = (byte)MathF.Round(texel.Y * 255f);
            pixels[offset + 2] = (byte)MathF.Round(texel.Z * 255f);
            pixels[offset + 3] = 255;
        }

        return pixels;
    }

    private static byte[] ToHalfPixels(Vector4[] texels)
    {
        var pixels = new Half[texels.Length * 4];

        for (var i = 0; i < texels.Length; i++)
        {
            var texel = texels[i];
            var offset = i * 4;

            pixels[offset] = (Half)texel.X;
            pixels[offset + 1] = (Half)texel.Y;
            pixels[offset + 2] = (Half)texel.Z;
            pixels[offset + 3] = Half.One;
        }

        return MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
    }

    // The texel of the sky faces that each texel of a cube face looks at
    private static Vector4[] ResampleCubeFace(int glFace, int size, SkyImage[] images, SkyTexture[] skyTextures)
    {
        // Every texel of a cube face looks furthest along the face's own axis, so one sky face fills all of it
        var faceIndex = FindSkyFace(GetCubeDirection(glFace, 0f, 0f));
        var face = SkyFaces[faceIndex];
        var image = images[faceIndex];
        var texCoordTransform = skyTextures[faceIndex].Transform;
        var texels = new Vector4[size * size];

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
                var texCoord = Vector2.Transform(new Vector2((s + 1f) * 0.5f, (1f - t) * 0.5f), texCoordTransform);
                var sourceX = Math.Clamp((int)(texCoord.X * image.Width), 0, image.Width - 1);
                var sourceY = Math.Clamp((int)(texCoord.Y * image.Height), 0, image.Height - 1);

                texels[(y * size) + x] = image.Texels[(sourceY * image.Width) + sourceX];
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
