using System.Globalization;

namespace ValveResourceFormat.Source1.Model;

/// <summary>
/// The variants of a material that carry what a model tells its eye and teeth shaders: the eyeball it draws, or the mouth
/// that lights it. The variant name holds the values, in the space the converted vertices are in, so a material needs
/// nothing but its name to be made.
/// </summary>
internal static class Source1ModelMaterials
{
    private const string EyeballPrefix = "eyeball;";
    private const string MouthPrefix = "mouth;";
    private const char Separator = ';';

    /// <summary>Gets the variant name of a material drawn as an eyeball.</summary>
    public static string GetEyeballVariant(Source1Eyeball eyeball)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{EyeballPrefix}{eyeball.Bone};{eyeball.Origin.X:R};{eyeball.Origin.Y:R};{eyeball.Origin.Z:R};{eyeball.Up.X:R};{eyeball.Up.Y:R};{eyeball.Up.Z:R};{eyeball.Forward.X:R};{eyeball.Forward.Y:R};{eyeball.Forward.Z:R};{eyeball.ZOffset:R};{eyeball.IrisScale:R}");
    }

    /// <summary>Gets the variant name of a material drawn as the teeth of a mouth.</summary>
    public static string GetMouthVariant(Source1Mouth mouth)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{MouthPrefix}{mouth.Bone};{mouth.Forward.X:R};{mouth.Forward.Y:R};{mouth.Forward.Z:R}");
    }

    /// <summary>Reads the eyeball of a variant made by <see cref="GetEyeballVariant"/>.</summary>
    public static bool TryParseEyeball(string variant, out Source1Eyeball eyeball)
    {
        eyeball = default;

        if (!variant.StartsWith(EyeballPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = variant[EyeballPrefix.Length..].Split(Separator);

        if (fields.Length != 12 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bone))
        {
            return false;
        }

        var values = new float[11];

        for (var i = 0; i < values.Length; i++)
        {
            if (!float.TryParse(fields[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        eyeball = new Source1Eyeball(
            bone,
            new Vector3(values[0], values[1], values[2]),
            new Vector3(values[3], values[4], values[5]),
            new Vector3(values[6], values[7], values[8]),
            values[9],
            values[10]);

        return true;
    }

    /// <summary>Reads the bone and the forward vector of a variant made by <see cref="GetMouthVariant"/>.</summary>
    public static bool TryParseMouth(string variant, out int bone, out Vector3 forward)
    {
        bone = 0;
        forward = default;

        if (!variant.StartsWith(MouthPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = variant[MouthPrefix.Length..].Split(Separator);

        if (fields.Length != 4 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out bone))
        {
            return false;
        }

        var values = new float[3];

        for (var i = 0; i < values.Length; i++)
        {
            if (!float.TryParse(fields[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        forward = new Vector3(values[0], values[1], values[2]);

        return true;
    }
}
