using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Source1.Model;

namespace ValveResourceFormat.Source1.Bsp;

/// <summary>
/// Adds the static props of a map to a world node as scene objects, with their baked per vertex lighting as extra vertex streams.
/// </summary>
internal sealed class Source1StaticPropConverter(
    BspFile bsp,
    Func<string, Source1Model?> loadModel,
    Func<string, Stream?> openFile,
    KVObject sceneObjects)
{
    private readonly record struct LodStream(int MeshIndex, int DrawCallCount, Vector3[] Colors);

    /// <summary>Gets the extra vertex streams, one for each level of detail of a prop that has baked lighting.</summary>
    public KVObject ExtraVertexStreams { get; } = KVObject.Array();

    /// <summary>Gets what binds each stream to the draw calls of the scene object it belongs to.</summary>
    public KVObject ExtraVertexStreamOverrides { get; } = KVObject.Array();

    /// <summary>Adds a prop as the next scene object of the world node.</summary>
    /// <param name="propIndex">The index of the prop in the map, which names its lighting file.</param>
    public void Add(int propIndex)
    {
        var prop = bsp.StaticProps.Props[propIndex];
        var source = loadModel(prop.ModelName);

        if (source == null)
        {
            return;
        }

        var sceneObjectIndex = sceneObjects.Count;
        sceneObjects.Add(CreateSceneObject(prop, source));

        if ((prop.Flags & StaticPropFlags.NoPerVertexLighting) != 0)
        {
            return;
        }

        var streams = LoadLodStreams(propIndex, source);

        foreach (var stream in streams)
        {
            AddStream(sceneObjectIndex, stream);
        }
    }

    /// <summary>Adds a detail model as the next scene object of the world node, without baked lighting.</summary>
    /// <param name="modelName">The path of the model.</param>
    /// <param name="transform">The transform of the model.</param>
    public void AddDetailModel(string modelName, Matrix4x4 transform)
    {
        if (loadModel(modelName) == null)
        {
            return;
        }

        var sceneObject = KVObject.Collection();
        sceneObject.Add("m_renderableModel", Source1Paths.Normalize(modelName));
        sceneObject.Add("m_skin", "skin0");
        sceneObject.Add("m_vTransform", Source1WorldConverter.ToColumns(transform));
        sceneObject.Add("m_nObjectTypeFlags", 0L);
        sceneObject.Add("m_vTintColor", CreateTint(1f, 1f, 1f));
        sceneObject.Add("m_vLightingOrigin", CreateVector(transform.Translation));
        sceneObjects.Add(sceneObject);
    }

    private static KVObject CreateTint(float red, float green, float blue)
    {
        var tint = KVObject.Array();
        tint.Add(red);
        tint.Add(green);
        tint.Add(blue);
        tint.Add(1f);

        return tint;
    }

    private static KVObject CreateSceneObject(BspStaticProp prop, Source1Model source)
    {
        var scale = prop.Scale > 0f ? prop.Scale : 1f;
        var transform = Matrix4x4.CreateScale(scale) * EntityTransformHelper.ToRigidTransformationMatrix(prop.Angles, prop.Origin);
        var color = new Color32(prop.ColorModulation);
        var flags = 0L;

        if ((prop.Flags & StaticPropFlags.NoShadow) != 0)
        {
            flags |= (long)ObjectTypeFlags.NoShadows;
        }

        var sceneObject = KVObject.Collection();
        sceneObject.Add("m_renderableModel", Source1Paths.Normalize(prop.ModelName));
        sceneObject.Add("m_skin", GetSkinName(prop.Skin, source));
        sceneObject.Add("m_vTransform", Source1WorldConverter.ToColumns(transform));
        sceneObject.Add("m_nObjectTypeFlags", flags);
        sceneObject.Add("m_vTintColor", CreateTint(color.R / 255f, color.G / 255f, color.B / 255f));

        if ((prop.Flags & StaticPropFlags.Fades) != 0)
        {
            var (fadeStart, fadeEnd) = GetFadeDistances(prop.FadeMinDist, prop.FadeMaxDist);

            if (fadeEnd > 0f)
            {
                sceneObject.Add("m_flFadeStartDistance", fadeStart);
                sceneObject.Add("m_flFadeEndDistance", fadeEnd);
            }
        }

        var useLightingOrigin = (prop.Flags & StaticPropFlags.UseLightingOrigin) != 0;
        sceneObject.Add("m_vLightingOrigin", CreateVector(useLightingOrigin ? prop.LightingOrigin : prop.Origin));

        return sceneObject;
    }

    // A negative start is an offset from the end
    private static (float Start, float End) GetFadeDistances(float start, float end)
    {
        if (start > end)
        {
            (start, end) = (end, start);
        }

        if (start < 0f)
        {
            start = Math.Max(end + start, 0f);
        }

        return (start, end);
    }

    private static KVObject CreateVector(Vector3 value)
    {
        var vector = KVObject.Array();
        vector.Add(value.X);
        vector.Add(value.Y);
        vector.Add(value.Z);

        return vector;
    }

    private static string GetSkinName(int skin, Source1Model source)
    {
        var skinCount = Math.Max(1, source.SkinFamilies.Length);
        var index = (uint)skin < (uint)skinCount ? skin : 0;

        return string.Create(CultureInfo.InvariantCulture, $"skin{index}");
    }

    private List<LodStream> LoadLodStreams(int propIndex, Source1Model source)
    {
        var lighting = LoadVertexLighting(propIndex);

        if (lighting == null || lighting.Checksum != source.Checksum)
        {
            return [];
        }

        // The converted model has one mesh for each level of detail that has geometry of each sub-model, in this order.
        // The meshes of a level of detail share one vertex buffer, so their colors follow one another.
        var streams = new List<LodStream>();
        var colors = new List<Vector3>();
        var meshIndex = 0;

        foreach (var part in source.BodyParts)
        {
            foreach (var subModel in part.Models)
            {
                foreach (var lod in subModel.Lods)
                {
                    if (lod.Length == 0)
                    {
                        continue;
                    }

                    colors.Clear();

                    foreach (var mesh in lod)
                    {
                        var meshColors = lighting.GetVertexLighting(mesh);

                        if (meshColors == null)
                        {
                            return [];
                        }

                        colors.AddRange(meshColors);
                    }

                    streams.Add(new LodStream(meshIndex, lod.Length, [.. colors]));
                    meshIndex++;
                }
            }
        }

        return streams;
    }

    private VhvFile? LoadVertexLighting(int propIndex)
    {
        var hdrName = string.Create(CultureInfo.InvariantCulture, $"sp_hdr_{propIndex}.vhv");
        var ldrName = string.Create(CultureInfo.InvariantCulture, $"sp_{propIndex}.vhv");
        using var stream = (bsp.HasHdrLighting ? openFile(hdrName) : null) ?? openFile(ldrName);

        if (stream == null)
        {
            return null;
        }

        return VhvFile.Parse(stream);
    }

    private void AddStream(int sceneObjectIndex, LodStream stream)
    {
        var streamIndex = ExtraVertexStreams.Count;
        ExtraVertexStreams.Add(CreateColorStream(stream.Colors));

        for (var drawCall = 0; drawCall < stream.DrawCallCount; drawCall++)
        {
            var binding = KVObject.Collection();
            binding.Add("m_hBuffer", streamIndex);

            var streamOverride = KVObject.Collection();
            streamOverride.Add("m_nSceneObjectIndex", (long)sceneObjectIndex);
            streamOverride.Add("m_nSubSceneObject", (long)stream.MeshIndex);
            streamOverride.Add("m_nDrawCallIndex", (long)drawCall);
            streamOverride.Add("m_extraBufferBinding", binding);
            streamOverride.Add("m_nAdditionalMeshDrawPrimitiveFlags", (long)RenderMeshDrawPrimitiveFlags.HasBakedLightingFromVertexStream);
            ExtraVertexStreamOverrides.Add(streamOverride);
        }
    }

    // Read as COLOR1, which the Source 1 model shaders take the baked light from
    private static KVObject CreateColorStream(Vector3[] colors)
    {
        var field = KVObject.Collection();
        field.Add("m_pSemanticName", "COLOR");
        field.Add("m_nSemanticIndex", 1);
        field.Add("m_Format", (long)DXGI_FORMAT.R32G32B32_FLOAT);
        field.Add("m_nOffset", 0);
        field.Add("m_nSlot", 0);
        field.Add("m_nSlotType", 0L);
        field.Add("m_nInstanceStepRate", 0);
        field.Add("m_szShaderSemantic", string.Empty);

        var fields = KVObject.Array();
        fields.Add(field);

        var stream = KVObject.Collection();
        stream.Add("m_nElementCount", (long)colors.Length);
        stream.Add("m_nElementSizeInBytes", (long)Marshal.SizeOf<Vector3>());
        stream.Add("m_inputLayoutFields", fields);
        stream.Add("m_pData", KVObject.Blob(MemoryMarshal.AsBytes(colors.AsSpan()).ToArray()));

        return stream;
    }
}
