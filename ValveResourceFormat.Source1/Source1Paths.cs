using System.IO;
using System.Text;
using System.Text.Unicode;

namespace ValveResourceFormat.Source1;

/// <summary>
/// Helpers for the game relative paths that Source 1 files are opened by.
/// </summary>
internal static class Source1Paths
{
    private const string MaterialsFolder = "materials/";

    /// <summary>
    /// Decodes a name stored in a map as UTF-8, or as Latin-1 when the bytes are not valid UTF-8.
    /// </summary>
    public static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        if (Utf8.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>
    /// Converts backslashes to slashes, removes leading slashes and lower-cases the path.
    /// </summary>
    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    /// <summary>
    /// Gets whether a normalized path stays inside the folder it is opened from: it is not rooted and has no <c>..</c> segment.
    /// </summary>
    public static bool IsContained(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the path of a material from its name, such as <c>brick/wall</c> to <c>materials/brick/wall.vmt</c>.
    /// </summary>
    public static string ToMaterialPath(string name) => ToResourcePath(name, ".vmt");

    /// <summary>
    /// Gets the path of a texture from its name, such as <c>brick/wall</c> to <c>materials/brick/wall.vtf</c>.
    /// The final extension of the name is dropped first, so <c>brick/wall.tga</c> names the same texture.
    /// </summary>
    public static string ToTexturePath(string name)
    {
        var path = StripTextureExtension(Normalize(name));
        return AddMaterialsFolder(path) + ".vtf";
    }

    /// <summary>
    /// Removes the final extension of a texture name, except for <c>.hdr</c>, which belongs to the name of a high dynamic range cubemap.
    /// </summary>
    public static string StripTextureExtension(string name)
    {
        if (name.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        var extensionStart = name.LastIndexOf('.');
        var folderEnd = name.AsSpan().LastIndexOfAny('/', '\\');

        if (extensionStart <= 0 || extensionStart < folderEnd)
        {
            return name;
        }

        return name[..extensionStart];
    }

    private static string AddMaterialsFolder(string path)
    {
        if (path.StartsWith(MaterialsFolder, StringComparison.Ordinal))
        {
            return path;
        }

        return MaterialsFolder + path;
    }

    private static string ToResourcePath(string name, string extension)
    {
        var path = AddMaterialsFolder(Normalize(name));

        if (!path.EndsWith(extension, StringComparison.Ordinal))
        {
            path += extension;
        }

        return path;
    }
}
