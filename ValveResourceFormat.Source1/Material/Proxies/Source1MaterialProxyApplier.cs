using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ValveResourceFormat.Source1.Material.Proxies;

/// <summary>
/// Evaluates the proxies of a material once and writes the variables they set into the parameters of the converted material.
/// </summary>
internal static class Source1MaterialProxyApplier
{
    private static readonly Dictionary<string, string> FloatUniforms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$alpha"] = "g_flOpacityScale",
        ["$detailblendfactor"] = "g_flDetailBlendFactor",
        ["$phongboost"] = "g_flPhongBoost",
        ["$refractamount"] = "g_flRefractAmount",
        ["$envmapfresnel"] = "g_flEnvMapFresnel",
        ["$envmapcontrast"] = "g_flEnvMapContrast",
        ["$envmapsaturation"] = "g_flEnvMapSaturation",
        ["$fresnelreflection"] = "g_flFresnelReflection",
    };

    private static readonly Dictionary<string, string> VectorUniforms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$envmaptint"] = "g_vEnvMapTint",
        ["$selfillumtint"] = "g_vSelfIllumTint",
        ["$detailtint"] = "g_vDetailTint",
        ["$refracttint"] = "g_vRefractTint",
        ["$reflecttint"] = "g_vReflectTint",
        ["$fogcolor"] = "g_vWaterFogColor",
    };

    private static readonly Dictionary<string, string> FrameSamplers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$frame"] = "g_tColor",
        ["$bumpframe"] = "g_tNormal",
        ["$detailframe"] = "g_tDetail",
    };

    /// <summary>
    /// Selects the texture frames the material sets, then evaluates its proxies at time zero.
    /// </summary>
    /// <param name="material">The converted material to write into.</param>
    /// <param name="vmt">The parsed material.</param>
    /// <param name="getFrameCount">Returns the number of frames of the texture at a path.</param>
    /// <param name="logger">Receives a warning for a proxy that does not exist or cannot be set up.</param>
    public static void Apply(ResourceTypes.Material material, VmtFile vmt, Func<string, int> getFrameCount, ILogger logger)
    {
        var seed = (int)StringToken.Get(material.Name);
        var proxies = new Source1MaterialProxies(vmt, seed, getFrameCount, logger);
        var shader = vmt.NormalizedShaderName;

        foreach (var (name, sampler) in FrameSamplers)
        {
            if (proxies.Find(name) is { } variable)
            {
                BindFrame(material, sampler, variable, getFrameCount);
            }
        }

        if (!proxies.HasProxies)
        {
            return;
        }

        proxies.Update(0f);

        foreach (var name in proxies.WrittenVars)
        {
            if (proxies.Find(name) is { } variable)
            {
                Bind(material, proxies, shader, name, variable, getFrameCount);
            }
        }
    }

    private static void Bind(ResourceTypes.Material material, Source1MaterialProxies proxies, string shader, string name, Source1MaterialVar variable, Func<string, int> getFrameCount)
    {
        if (name.Equals("$color", StringComparison.OrdinalIgnoreCase) || name.Equals("$color2", StringComparison.OrdinalIgnoreCase))
        {
            BindColor(material, proxies, shader);
        }
        else if (name.Equals("$basetexturetransform", StringComparison.OrdinalIgnoreCase))
        {
            BindTransform(material, variable, "g_vTexCoordTransformRow0", "g_vTexCoordTransformRow1");
        }
        else if (name.Equals("$bumptransform", StringComparison.OrdinalIgnoreCase))
        {
            BindTransform(material, variable, "g_vBumpTransformRow0", "g_vBumpTransformRow1");
        }
        else if (name.Equals("$envmapmasktransform", StringComparison.OrdinalIgnoreCase))
        {
            BindTransform(material, variable, "g_vEnvMapMaskTransformRow0", "g_vEnvMapMaskTransformRow1");
        }
        else if (FrameSamplers.TryGetValue(name, out var sampler))
        {
            BindFrame(material, sampler, variable, getFrameCount);
        }
        else if (FloatUniforms.TryGetValue(name, out var floatUniform))
        {
            if (variable.Type is Source1MaterialVarType.Float or Source1MaterialVarType.Int)
            {
                var value = variable.GetFloat();

                if (name.Equals("$alpha", StringComparison.OrdinalIgnoreCase))
                {
                    value = Source1MaterialConverter.ConvertOpacity(value, shader);
                }

                material.FloatParams[floatUniform] = value;
            }
        }
        else if (VectorUniforms.TryGetValue(name, out var vectorUniform))
        {
            BindVector(material, shader, name, vectorUniform, variable);
        }
    }

    // The tint is the product of both colors, a missing one does not tint
    private static void BindColor(ResourceTypes.Material material, Source1MaterialProxies proxies, string shader)
    {
        var color = ReadColor(proxies, "$color");
        var color2 = ReadColor(proxies, "$color2");
        var tint = Source1MaterialConverter.ConvertColorTint(color * color2, shader);

        material.VectorParams["g_vColorTint"] = new Vector4(tint, 1f);
    }

    private static Vector3 ReadColor(Source1MaterialProxies proxies, string name)
    {
        if (proxies.Find(name) is not { } variable)
        {
            return Vector3.One;
        }

        return ReadVector3(variable, Vector3.One);
    }

    private static Vector3 ReadVector3(Source1MaterialVar variable, Vector3 fallback)
    {
        if (variable.Type == Source1MaterialVarType.Vector)
        {
            return new Vector3(variable.GetFloat(0), variable.GetFloat(1), variable.GetFloat(2));
        }

        if (variable.Type is Source1MaterialVarType.Float or Source1MaterialVarType.Int)
        {
            return new Vector3(variable.GetFloat());
        }

        return fallback;
    }

    private static void BindVector(ResourceTypes.Material material, string shader, string name, string uniform, Source1MaterialVar variable)
    {
        if (!material.VectorParams.TryGetValue(uniform, out var current))
        {
            return;
        }

        if (variable.Type is not (Source1MaterialVarType.Vector or Source1MaterialVarType.Float or Source1MaterialVarType.Int))
        {
            return;
        }

        var value = Source1MaterialConverter.ConvertTint(name, ReadVector3(variable, Vector3.One), shader);
        material.VectorParams[uniform] = new Vector4(value, current.W);
    }

    private static void BindTransform(ResourceTypes.Material material, Source1MaterialVar variable, string row0Name, string row1Name)
    {
        if (variable.Type != Source1MaterialVarType.Matrix)
        {
            return;
        }

        var m = variable.Matrix;
        material.VectorParams[row0Name] = new Vector4(m[0], m[1], 0f, m[3]);
        material.VectorParams[row1Name] = new Vector4(m[4], m[5], 0f, m[7]);
    }

    private static void BindFrame(ResourceTypes.Material material, string sampler, Source1MaterialVar variable, Func<string, int> getFrameCount)
    {
        if (!material.TextureParams.TryGetValue(sampler, out var currentPath))
        {
            return;
        }

        var path = Source1Paths.RemoveVariant(currentPath, out _);
        var frame = variable.GetInt();

        if (frame < 0 || frame >= getFrameCount(path))
        {
            frame = 0;
        }

        material.TextureParams[sampler] = frame == 0
            ? path
            : Source1Paths.AddVariant(path, frame.ToString(CultureInfo.InvariantCulture));
    }
}
