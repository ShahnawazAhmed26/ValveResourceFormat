using System.Globalization;
using System.Runtime.InteropServices;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Source1;
using ValveResourceFormat.Source1.Model;
using S2Model = ValveResourceFormat.ResourceTypes.Model;

namespace ValveResourceFormat.GoldSrc;

/// <summary>Converts a GoldSrc model to an in-memory Source 2 model resource with one embedded mesh per studio mesh.</summary>
/// <remarks>
/// Studio vertices are in the space of their bone, so they are baked to model space at the rest pose
/// and skinned with the inverse of the rest transform of their bone.
/// </remarks>
internal static class GoldSrcModelConverter
{
    private const int MaxMeshGroups = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct ConvertedVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 TexCoord;
        public uint BoneIndices;
        public uint BoneWeights;
    }

    private static readonly VertexField[] VertexFields =
    [
        new(nameof(ConvertedVertex.Position), "POSITION", 0, DXGI_FORMAT.R32G32B32_FLOAT),
        new(nameof(ConvertedVertex.Normal), "NORMAL", 0, DXGI_FORMAT.R32G32B32_FLOAT),
        new(nameof(ConvertedVertex.Tangent), "TANGENT", 0, DXGI_FORMAT.R32G32B32A32_FLOAT),
        new(nameof(ConvertedVertex.TexCoord), "TEXCOORD", 0, DXGI_FORMAT.R32G32_FLOAT),
        new(nameof(ConvertedVertex.BoneIndices), "BLENDINDICES", 0, DXGI_FORMAT.R8G8B8A8_UINT),
        new(nameof(ConvertedVertex.BoneWeights), "BLENDWEIGHT", 0, DXGI_FORMAT.R8G8B8A8_UNORM),
    ];

    private sealed class MeshEntry
    {
        public required GoldSrcMdlSubmodel SubModel { get; init; }
        public required GoldSrcMdlMesh Source { get; init; }
        public required int BoneCount { get; init; }
    }

    /// <summary>Converts a model.</summary>
    /// <param name="source">The model to convert, with the files it keeps its textures in loaded.</param>
    /// <param name="name">Name of the resulting model.</param>
    /// <param name="normalizeTexCoords">Whether to divide texture coordinates by the size of the texture, or keep them in texels.</param>
    public static S2Model Convert(GoldSrcMdlFile source, string name, bool normalizeTexCoords)
    {
        ArgumentNullException.ThrowIfNull(source);

        var textureSource = GetTextureSource(source);
        var boneToModel = BuildRestTransforms(source);

        var resource = new Resource(ResourceType.Model) { FileName = name };
        var model = new S2Model { Resource = resource };
        var controlData = KVObject.Collection();
        var control = new BinaryKV3(controlData, KV3IDLookup.Get("generic"), BlockType.CTRL) { Resource = resource };

        resource.AddBlock(model);
        resource.AddBlock(control);

        var meshGroupNames = new List<string>();
        var meshGroupMasks = new List<ulong>();
        var meshes = new List<MeshEntry>();
        var defaultMask = 0UL;
        var embeddedMeshes = KVObject.Array();

        for (var partIndex = 0; partIndex < source.BodyParts.Length; partIndex++)
        {
            var part = source.BodyParts[partIndex];

            for (var subIndex = 0; subIndex < part.Models.Length; subIndex++)
            {
                var subModel = part.Models[subIndex];
                var groupIndex = meshGroupNames.Count;
                var groupName = string.Create(CultureInfo.InvariantCulture, $"{part.Name}_@{subIndex}_#&{subModel.Name}");
                meshGroupNames.Add(groupName);

                var inGroups = groupIndex < MaxMeshGroups;
                var groupBit = inGroups ? 1UL << groupIndex : 0UL;

                if (subIndex == 0)
                {
                    defaultMask |= groupBit;
                }

                for (var i = 0; i < subModel.Meshes.Length; i++)
                {
                    var sourceMesh = subModel.Meshes[i];

                    if (sourceMesh.Indices.Length == 0)
                    {
                        continue;
                    }

                    var meshIndex = meshes.Count;
                    var boneCount = GetBoneCount(subModel, sourceMesh);
                    var entry = new MeshEntry { SubModel = subModel, Source = sourceMesh, BoneCount = boneCount };

                    meshes.Add(entry);
                    meshGroupMasks.Add(groupBit);

                    var meshName = string.Create(CultureInfo.InvariantCulture, $"{part.Name}.{subIndex}.{i}");

                    var vertices = CreateVertices(entry, boneToModel, textureSource, normalizeTexCoords);
                    var bounds = Source1VertexBuffers.GetBounds(vertices, vertex => vertex.Position);
                    var mesh = new Mesh(BlockType.MDAT) { Resource = resource, Data = CreateMeshData(source, name, entry, boneToModel, bounds, textureSource) };
                    var dataBlockIndex = resource.AddBlock(mesh);
                    var vbibBlockIndex = resource.AddBlock(CreateVbib(entry, vertices));

                    var embedded = KVObject.Collection();
                    embedded.Add("name", meshName);
                    embedded.Add("mesh_index", meshIndex);
                    embedded.Add("data_block", dataBlockIndex);
                    embedded.Add("vbib_block", vbibBlockIndex);
                    embedded.Add("morph_block", -1);
                    embeddedMeshes.Add(embedded);
                }
            }
        }

        controlData.Add("embedded_meshes", embeddedMeshes);

        model.Data = CreateModelData(source, textureSource, name, meshes, meshGroupNames, meshGroupMasks, defaultMask);

        return model;
    }

    /// <summary>
    /// Gets the material path a texture of a converted model is referenced by.
    /// The model name keeps textures of the same name in different models apart.
    /// </summary>
    public static string GetMaterialPath(string modelName, GoldSrcMdlFile textureSource, int textureIndex)
    {
        if ((uint)textureIndex >= (uint)textureSource.Textures.Length)
        {
            return string.Empty;
        }

        var texture = textureSource.Textures[textureIndex];

        return string.Create(CultureInfo.InvariantCulture, $"{modelName}/{textureIndex}_{texture.Name}.vmat");
    }

    /// <summary>Gets the model that holds the textures of a model.</summary>
    public static GoldSrcMdlFile GetTextureSource(GoldSrcMdlFile source)
        => source.HasTextures ? source : source.TextureFile ?? source;

    private static Matrix4x4[] BuildRestTransforms(GoldSrcMdlFile source)
    {
        var bones = source.Bones;
        var boneToModel = new Matrix4x4[bones.Length];

        for (var i = 0; i < bones.Length; i++)
        {
            var value = bones[i].Value;
            var local = Matrix4x4.CreateFromQuaternion(GetRestRotation(bones[i]));
            local.Translation = new Vector3(value[0], value[1], value[2]);

            var parent = bones[i].Parent;
            boneToModel[i] = (uint)parent < (uint)i ? local * boneToModel[parent] : local;
        }

        return boneToModel;
    }

    private static Quaternion GetRestRotation(GoldSrcBone bone)
        => GoldSrcMdlFile.EulerToQuaternion(bone.Value[3], bone.Value[4], bone.Value[5]);

    private static int GetBoneCount(GoldSrcMdlSubmodel subModel, GoldSrcMdlMesh mesh)
    {
        var highest = 0;

        foreach (var vertex in mesh.Vertices)
        {
            highest = Math.Max(highest, subModel.PositionBones[vertex.Position]);
        }

        return highest + 1;
    }

    private static int GetTextureIndex(GoldSrcMdlFile textureSource, GoldSrcMdlMesh mesh, int skin)
    {
        var families = textureSource.SkinFamilies;

        if (skin < families.Length && mesh.SkinRef < families[skin].Length)
        {
            return families[skin][mesh.SkinRef];
        }

        return mesh.SkinRef;
    }

    private static KVObject CreateModelData(
        GoldSrcMdlFile source,
        GoldSrcMdlFile textureSource,
        string name,
        List<MeshEntry> meshes,
        List<string> meshGroupNames,
        List<ulong> meshGroupMasks,
        ulong defaultMask)
    {
        var data = KVObject.Collection();
        data.Add("m_name", name);
        data.Add("m_modelInfo", KVObject.Collection());
        data.Add("m_modelSkeleton", CreateSkeletonData(source));
        data.Add("m_materialGroups", CreateMaterialGroups(name, textureSource, meshes));

        var meshGroups = KVObject.Array();

        foreach (var meshGroupName in meshGroupNames)
        {
            meshGroups.Add(meshGroupName);
        }

        data.Add("m_meshGroups", meshGroups);

        var groupMasks = KVObject.Array();

        foreach (var mask in meshGroupMasks)
        {
            groupMasks.Add(mask);
        }

        data.Add("m_refMeshGroupMasks", groupMasks);
        data.Add("m_nDefaultMeshGroupMask", defaultMask);

        var lodMasks = KVObject.Array();
        var remappingTable = KVObject.Array();
        var remappingTableStarts = KVObject.Array();
        var tableStart = 0;

        foreach (var mesh in meshes)
        {
            lodMasks.Add(1L);
            remappingTableStarts.Add(tableStart);

            for (var bone = 0; bone < mesh.BoneCount; bone++)
            {
                remappingTable.Add(bone);
            }

            tableStart += mesh.BoneCount;
        }

        data.Add("m_refLODGroupMasks", lodMasks);
        data.Add("m_lodGroupSwitchDistances", KVObject.Array());
        data.Add("m_refPhysicsData", KVObject.Array());
        data.Add("m_refAnimGroups", KVObject.Array());
        data.Add("m_remappingTable", remappingTable);
        data.Add("m_remappingTableStarts", remappingTableStarts);

        return data;
    }

    private static KVObject CreateSkeletonData(GoldSrcMdlFile source)
    {
        var names = KVObject.Array();
        var parents = KVObject.Array();
        var flags = KVObject.Array();
        var positions = KVObject.Array();
        var rotations = KVObject.Array();
        var scales = KVObject.Array();

        foreach (var bone in source.Bones)
        {
            var value = bone.Value;

            names.Add(bone.Name);
            parents.Add(bone.Parent);
            flags.Add(0);
            positions.Add(KVHelpers.ToKVArray(new Vector3(value[0], value[1], value[2])));
            rotations.Add(KVHelpers.ToKVArray(GetRestRotation(bone)));
            scales.Add(1f);
        }

        var skeleton = KVObject.Collection();
        skeleton.Add("m_boneName", names);
        skeleton.Add("m_nParent", parents);
        skeleton.Add("m_nFlag", flags);
        skeleton.Add("m_bonePosParent", positions);
        skeleton.Add("m_boneRotParent", rotations);
        skeleton.Add("m_boneScaleParent", scales);

        return skeleton;
    }

    private static KVObject CreateMaterialGroups(string modelName, GoldSrcMdlFile textureSource, List<MeshEntry> meshes)
    {
        var defaultMaterials = new List<string>();
        var firstMeshOfMaterial = new Dictionary<string, GoldSrcMdlMesh>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in meshes)
        {
            var path = GetMaterialPath(modelName, textureSource, GetTextureIndex(textureSource, entry.Source, 0));

            if (firstMeshOfMaterial.TryAdd(path, entry.Source))
            {
                defaultMaterials.Add(path);
            }
        }

        var skinCount = Math.Max(1, textureSource.SkinFamilies.Length);
        var groups = KVObject.Array();

        for (var skin = 0; skin < skinCount; skin++)
        {
            var materials = KVObject.Array();

            foreach (var defaultMaterial in defaultMaterials)
            {
                var mesh = firstMeshOfMaterial[defaultMaterial];
                materials.Add(GetMaterialPath(modelName, textureSource, GetTextureIndex(textureSource, mesh, skin)));
            }

            var group = KVObject.Collection();
            group.Add("m_name", Source1ModelConverter.GetSkinName(skin));
            group.Add("m_materials", materials);
            groups.Add(group);
        }

        return groups;
    }

    private static KVObject CreateMeshData(GoldSrcMdlFile source, string modelName, MeshEntry entry, Matrix4x4[] boneToModel, (Vector3 Min, Vector3 Max) bounds, GoldSrcMdlFile textureSource)
    {
        var mesh = entry.Source;
        var (min, max) = bounds;

        var indexBuffer = KVObject.Collection();
        indexBuffer.Add("m_hBuffer", 0);
        indexBuffer.Add("m_nBindOffsetBytes", 0);

        var vertexBufferReference = KVObject.Collection();
        vertexBufferReference.Add("m_hBuffer", 0);
        vertexBufferReference.Add("m_nBindOffsetBytes", 0);

        var vertexBuffers = KVObject.Array();
        vertexBuffers.Add(vertexBufferReference);

        var drawCall = KVObject.Collection();
        drawCall.Add("m_nPrimitiveType", "RENDER_PRIM_TRIANGLES");
        drawCall.Add("m_nBaseVertex", 0);
        drawCall.Add("m_nVertexCount", mesh.Vertices.Length);
        drawCall.Add("m_nStartIndex", 0);
        drawCall.Add("m_nIndexCount", mesh.Indices.Length);
        drawCall.Add("m_indexBuffer", indexBuffer);
        drawCall.Add("m_vertexBuffers", vertexBuffers);
        drawCall.Add("m_material", GetMaterialPath(modelName, textureSource, GetTextureIndex(textureSource, mesh, 0)));

        var drawCalls = KVObject.Array();
        drawCalls.Add(drawCall);

        var drawBound = KVObject.Collection();
        drawBound.Add("m_vMinBounds", KVHelpers.ToKVArray(min));
        drawBound.Add("m_vMaxBounds", KVHelpers.ToKVArray(max));

        var drawBounds = KVObject.Array();
        drawBounds.Add(drawBound);

        var sceneObject = KVObject.Collection();
        sceneObject.Add("m_vMinBounds", KVHelpers.ToKVArray(min));
        sceneObject.Add("m_vMaxBounds", KVHelpers.ToKVArray(max));
        sceneObject.Add("m_drawCalls", drawCalls);
        sceneObject.Add("m_drawBounds", drawBounds);

        var sceneObjects = KVObject.Array();
        sceneObjects.Add(sceneObject);

        var bones = KVObject.Array();

        for (var i = 0; i < entry.BoneCount; i++)
        {
            Matrix4x4.Invert(boneToModel[i], out var inverse);

            var bone = KVObject.Collection();
            bone.Add("m_boneName", source.Bones[i].Name);
            bone.Add("m_invBindPose", Source1ModelConverter.ToArray(inverse));
            bones.Add(bone);
        }

        var skeleton = KVObject.Collection();
        skeleton.Add("m_nBoneWeightCount", 4);
        skeleton.Add("m_bones", bones);

        var data = KVObject.Collection();
        data.Add("m_sceneObjects", sceneObjects);
        data.Add("m_skeleton", skeleton);

        return data;
    }

    private static ConvertedVertex[] CreateVertices(MeshEntry entry, Matrix4x4[] boneToModel, GoldSrcMdlFile textureSource, bool normalizeTexCoords)
    {
        var subModel = entry.SubModel;
        var mesh = entry.Source;
        var vertices = new ConvertedVertex[mesh.Vertices.Length];

        var scale = Vector2.One;

        if (normalizeTexCoords)
        {
            scale = GetTexCoordScale(textureSource, GetTextureIndex(textureSource, mesh, 0));
        }

        for (var i = 0; i < vertices.Length; i++)
        {
            var source = mesh.Vertices[i];
            var positionBone = subModel.PositionBones[source.Position];
            var normalBone = subModel.NormalBones[source.Normal];

            var position = Vector3.Transform(subModel.Positions[source.Position], boneToModel[positionBone]);
            var normal = Vector3.Normalize(Vector3.TransformNormal(subModel.Normals[source.Normal], boneToModel[normalBone]));

            vertices[i] = new ConvertedVertex
            {
                Position = position,
                Normal = normal,
                Tangent = new Vector4(1f, 0f, 0f, -1f),
                TexCoord = new Vector2(source.S, source.T) * scale,
                BoneIndices = positionBone,
                BoneWeights = 255,
            };
        }

        return vertices;
    }

    private static Vector2 GetTexCoordScale(GoldSrcMdlFile textureSource, int textureIndex)
    {
        if ((uint)textureIndex >= (uint)textureSource.Textures.Length)
        {
            return Vector2.One;
        }

        var texture = textureSource.Textures[textureIndex];

        return new Vector2(1f / Math.Max(texture.Width, 1), 1f / Math.Max(texture.Height, 1));
    }

    private static VBIB CreateVbib(MeshEntry entry, ConvertedVertex[] vertices)
    {
        var indices = entry.Source.Indices;
        var indexData = new ushort[indices.Length];

        for (var i = 0; i < indexData.Length; i++)
        {
            indexData[i] = (ushort)indices[i];
        }

        return Source1VertexBuffers.Create(vertices, VertexFields, indexData);
    }
}
