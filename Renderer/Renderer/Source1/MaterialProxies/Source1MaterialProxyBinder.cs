using ValveResourceFormat.Source1.Material;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace ValveResourceFormat.Renderer.Source1.MaterialProxies
{
    /// <summary>
    /// Keeps the uniforms and textures of a material in step with the variables its proxies write.
    /// </summary>
    internal sealed class Source1MaterialProxyBinder(RenderMaterial material, Source1MaterialProxies proxies, MaterialLoader loader)
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

        public Source1MaterialProxies Proxies => proxies;

        private string ShaderName => material.Material.ShaderName[VrfMaterial.Source1ShaderPrefix.Length..];

        /// <summary>Selects the texture frames the material file sets. Returns <see langword="true"/> when it sets any.</summary>
        public bool BindStaticFrames()
        {
            var any = false;

            foreach (var (name, sampler) in FrameSamplers)
            {
                if (proxies.Find(name) is { } variable)
                {
                    BindFrame(sampler, variable);
                    any = true;
                }
            }

            return any;
        }

        /// <summary>Evaluates the proxies for a time and binds what they wrote, once per time.</summary>
        public void Update(float time)
        {
            if (!proxies.Update(time))
            {
                return;
            }

            foreach (var name in proxies.WrittenVars)
            {
                if (proxies.Find(name) is { } variable)
                {
                    Bind(name, variable);
                }
            }
        }

        private void Bind(string name, Source1MaterialVar variable)
        {
            if (name.Equals("$color", StringComparison.OrdinalIgnoreCase) || name.Equals("$color2", StringComparison.OrdinalIgnoreCase))
            {
                BindColor();
            }
            else if (name.Equals("$basetexturetransform", StringComparison.OrdinalIgnoreCase))
            {
                BindTransform(variable, "g_vTexCoordTransformRow0", "g_vTexCoordTransformRow1");
            }
            else if (name.Equals("$bumptransform", StringComparison.OrdinalIgnoreCase))
            {
                BindTransform(variable, "g_vBumpTransformRow0", "g_vBumpTransformRow1");
            }
            else if (name.Equals("$envmapmasktransform", StringComparison.OrdinalIgnoreCase))
            {
                BindTransform(variable, "g_vEnvMapMaskTransformRow0", "g_vEnvMapMaskTransformRow1");
            }
            else if (FrameSamplers.TryGetValue(name, out var sampler))
            {
                BindFrame(sampler, variable);
            }
            else if (FloatUniforms.TryGetValue(name, out var floatUniform))
            {
                if (variable.Type is Source1MaterialVarType.Float or Source1MaterialVarType.Int)
                {
                    var value = variable.GetFloat();

                    if (name.Equals("$alpha", StringComparison.OrdinalIgnoreCase))
                    {
                        value = Source1MaterialConverter.ConvertOpacity(value, ShaderName);
                    }

                    material.FloatParams[floatUniform] = value;
                }
            }
            else if (VectorUniforms.TryGetValue(name, out var vectorUniform))
            {
                BindVector(name, vectorUniform, variable);
            }
        }

        // The tint is the product of both colors, a missing one does not tint
        private void BindColor()
        {
            var color = ReadColor("$color");
            var color2 = ReadColor("$color2");
            var tint = Source1MaterialConverter.ConvertColorTint(color * color2, ShaderName);

            material.VectorParams["g_vColorTint"] = new Vector4(tint, 1f);
        }

        private Vector3 ReadColor(string name)
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

        private void BindVector(string name, string uniform, Source1MaterialVar variable)
        {
            if (!material.VectorParams.TryGetValue(uniform, out var current))
            {
                return;
            }

            if (variable.Type is not (Source1MaterialVarType.Vector or Source1MaterialVarType.Float or Source1MaterialVarType.Int))
            {
                return;
            }

            var value = Source1MaterialConverter.ConvertTint(name, ReadVector3(variable, Vector3.One), ShaderName);
            material.VectorParams[uniform] = new Vector4(value, current.W);
        }

        private void BindTransform(Source1MaterialVar variable, string row0Name, string row1Name)
        {
            if (variable.Type != Source1MaterialVarType.Matrix)
            {
                return;
            }

            if (!material.Shader.UniformNames.Contains(row0Name))
            {
                return;
            }

            var m = variable.Matrix;
            material.VectorParams[row0Name] = new Vector4(m[0], m[1], 0f, m[3]);
            material.VectorParams[row1Name] = new Vector4(m[4], m[5], 0f, m[7]);
        }

        private void BindFrame(string sampler, Source1MaterialVar variable)
        {
            if (!material.Material.TextureParams.TryGetValue(sampler, out var path) || !material.Shader.UniformNames.Contains(sampler))
            {
                return;
            }

            var frameCount = loader.GetSource1FrameCount(path);
            var frame = variable.GetInt();

            if (frame < 0 || frame >= frameCount)
            {
                frame = 0;
            }

            var srgbRead = material.Shader.SrgbUniforms.Contains(sampler);

            material.Textures[sampler] = loader.GetSource1TextureFrame(path, srgbRead, frame);
        }
    }
}
