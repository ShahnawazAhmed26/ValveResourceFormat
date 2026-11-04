using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes.ModelData;
using ValveResourceFormat.Source1.FileSystem;
using ValveResourceFormat.Source1.Material;
using S2Model = ValveResourceFormat.ResourceTypes.Model;

namespace ValveResourceFormat.Source1.Model;

/// <summary>Converts a Source 1 model to an in-memory Source 2 model with one embedded mesh per sub-model and level of detail.</summary>
internal static class Source1ModelConverter
{
    private const int MaxMeshGroups = 64;

    // Attachment flag that keeps the attachment from turning with its bone
    private const int WorldAlignAttachmentFlag = 0x10000;

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

    /// <summary>Converts a model.</summary>
    /// <param name="source">The model to convert.</param>
    /// <param name="name">Name of the resulting model.</param>
    /// <param name="fileSystem">The files of the game, to pick between the search paths of a material and to read its shader.</param>
    public static S2Model Convert(Source1Model source, string name, Source1FileSystem? fileSystem)
    {
        var bindToRest = GetBindToRest(source);
        var materialPaths = new ModelMaterialPaths(source, ResolveMaterialPaths(source, path => fileSystem?.Exists(path) ?? false), bindToRest, fileSystem);
        var flexConverter = new Source1FlexConverter(source);
        var meshGroupNames = new List<string>();
        var meshes = new List<ModelMeshSource>();
        var sourceMeshes = new List<Source1Mesh>();
        var defaultMask = 0UL;

        for (var partIndex = 0; partIndex < source.BodyParts.Length; partIndex++)
        {
            var part = source.BodyParts[partIndex];

            for (var subIndex = 0; subIndex < part.Models.Length; subIndex++)
            {
                var subModel = part.Models[subIndex];
                var groupIndex = meshGroupNames.Count;
                var choiceName = Path.GetFileNameWithoutExtension(subModel.Name);
                var groupName = string.Create(CultureInfo.InvariantCulture, $"{part.Name}_@{subIndex}_#&{choiceName}");
                meshGroupNames.Add(groupName);

                var inGroups = groupIndex < MaxMeshGroups;
                var groupBit = inGroups ? 1UL << groupIndex : 0UL;

                if (subIndex == 0)
                {
                    defaultMask |= groupBit;
                }

                for (var lod = 0; lod < subModel.Lods.Length; lod++)
                {
                    var lodMeshes = subModel.Lods[lod];

                    if (lodMeshes.Length == 0)
                    {
                        continue;
                    }

                    var meshName = string.Create(CultureInfo.InvariantCulture, $"{part.Name}.{subIndex}.lod{lod}");

                    meshes.Add(CreateMesh(source, meshName, lodMeshes, groupBit, bindToRest, materialPaths, flexConverter));
                    sourceMeshes.AddRange(lodMeshes);
                }
            }
        }

        var modelData = KVObject.Collection();
        modelData.Add("m_lodGroupSwitchDistances", CreateSwitchDistances(source));
        modelData.Add("m_attachments", CreateAttachments(source));
        modelData.Add("m_hitboxsets", CreateHitboxSets(source));

        var bones = CreateBones(source, bindToRest);
        var materialGroups = CreateMaterialGroups(source, sourceMeshes, materialPaths);
        var animations = Source1AnimationBaker.Bake(source);
#pragma warning disable CA2000 // The model keeps its resource, which holds no unmanaged state
        var model = (S2Model)S2Model.Create(name, meshes, bones, materialGroups, meshGroupNames, defaultMask, modelData, animations).DataBlock!;
#pragma warning restore CA2000
        model.Resource.FileName = name;

        return model;
    }

    private static ModelMeshSource CreateMesh(
        Source1Model source,
        string name,
        Source1Mesh[] lodMeshes,
        ulong groupMask,
        Matrix4x4 bindToRest,
        ModelMaterialPaths materialPaths,
        Source1FlexConverter flexConverter)
    {
        var vertices = new List<ConvertedVertex>();
        var indices = new List<ushort>();
        var drawCalls = new List<MeshDrawCall>();
        var baseVertices = new List<int>();
        var boneCount = 0;

        // The meshes of a level of detail share one vertex buffer and one index buffer, each drawn from its own range
        foreach (var sourceMesh in lodMeshes)
        {
            var materialPath = materialPaths.Get(sourceMesh, 0);
            var drawCall = new MeshDrawCall(materialPath, vertices.Count, sourceMesh.Vertices.Length, indices.Count, sourceMesh.Indices.Length);

            drawCalls.Add(drawCall);
            baseVertices.Add(vertices.Count);
            vertices.AddRange(CreateVertices(sourceMesh, bindToRest));
            indices.AddRange(sourceMesh.Indices);
            boneCount = Math.Max(boneCount, GetBoneCount(source, sourceMesh));

            // The shader of an eyeball skins the origin of the eye by its bone
            if (sourceMesh.Eyeball is { } eyeball)
            {
                boneCount = Math.Max(boneCount, Math.Min(eyeball.Bone + 1, source.Bones.Length));
            }
        }

        var vertexArray = vertices.ToArray();
        var (min, max) = Source1VertexBuffers.GetBounds(vertexArray, vertex => vertex.Position);

        var restToBind = Matrix4x4.Invert(bindToRest, out var inverse) ? inverse : Matrix4x4.Identity;
        var meshBones = new List<MeshBone>(boneCount);
        var boneRemap = new int[boneCount];

        for (var i = 0; i < boneCount; i++)
        {
            var bone = source.Bones[i];
            var parentName = (uint)bone.ParentIndex < (uint)source.Bones.Length ? source.Bones[bone.ParentIndex].Name : string.Empty;

            meshBones.Add(new MeshBone(bone.Name, parentName, restToBind * bone.PoseToBone));
            boneRemap[i] = i;
        }

        var buffers = Source1VertexBuffers.Create(vertexArray, VertexFields, indices.ToArray());

        return new ModelMeshSource(name, buffers, drawCalls, min, max)
        {
            MeshGroupMask = groupMask,
            LodMask = 1L << lodMeshes[0].Lod,
            Bones = meshBones,
            BoneRemap = boneRemap,
            Morph = flexConverter.CreateMorph(lodMeshes, baseVertices, vertexArray.Length, bindToRest),
        };
    }

    /// <summary>
    /// Gets the rotation from the space the vertices and PoseToBone are in to the space the sequences pose the bones in.
    /// The two differ by a rotation of the whole model, which leaves the reference pose lying down unless the vertices are rotated with it.
    /// It is taken from the root bone in the first frame of the "ref" sequence, or of the first sequence that can be played.
    /// </summary>
    private static Matrix4x4 GetBindToRest(Source1Model source)
    {
        if (source.Bones.Length == 0)
        {
            return Matrix4x4.Identity;
        }

        var restSequence = FindRestSequence(source);

        if (restSequence == null)
        {
            return Matrix4x4.Identity;
        }

        var positions = new Vector3[source.Bones.Length];
        var rotations = new Quaternion[source.Bones.Length];
        var boneToModel = new Matrix4x4[source.Bones.Length];

        restSequence.Blends[0].GetFrame(0, positions, rotations);
        source.BuildBoneTransforms(positions, rotations, boneToModel);

        var bindToRest = source.Bones[0].PoseToBone * boneToModel[0];

        // Only the orientation is wanted, the model stays where its vertices are
        bindToRest.Translation = Vector3.Zero;

        return bindToRest;
    }

    private static Source1Sequence? FindRestSequence(Source1Model source)
    {
        Source1Sequence? firstPlayable = null;

        foreach (var sequence in source.Sequences)
        {
            if (sequence.IsDelta || !sequence.IsPlayable)
            {
                continue;
            }

            if (string.Equals(sequence.Name, "ref", StringComparison.OrdinalIgnoreCase))
            {
                return sequence;
            }

            firstPlayable ??= sequence;
        }

        return firstPlayable;
    }

    private static string[] ResolveMaterialPaths(Source1Model source, Func<string, bool> materialExists)
    {
        var paths = new string[source.MaterialNames.Length];

        for (var i = 0; i < paths.Length; i++)
        {
            paths[i] = $"materials/{source.MaterialNames[i]}.vmt";

            var first = true;

            foreach (var path in source.GetMaterialPaths(i))
            {
                if (first)
                {
                    paths[i] = path;
                    first = false;
                }

                if (materialExists(path))
                {
                    paths[i] = path;
                    break;
                }
            }
        }

        return paths;
    }

    private static int GetBoneCount(Source1Model source, Source1Mesh mesh)
    {
        var highest = 0;

        foreach (var vertex in mesh.Vertices)
        {
            highest = Math.Max(highest, vertex.Bone0);

            if (vertex.NumBones > 1)
            {
                highest = Math.Max(highest, vertex.Bone1);
            }

            if (vertex.NumBones > 2)
            {
                highest = Math.Max(highest, vertex.Bone2);
            }
        }

        return Math.Min(highest + 1, source.Bones.Length);
    }

    private static int GetMaterialIndex(Source1Model source, Source1Mesh mesh, int skin)
    {
        var families = source.SkinFamilies;

        if (skin < families.Length && mesh.SkinRefIndex < families[skin].Length)
        {
            return families[skin][mesh.SkinRefIndex];
        }

        return mesh.MaterialIndex;
    }

    private static KVObject CreateSwitchDistances(Source1Model source)
    {
        var switchDistances = KVObject.Array();

        foreach (var switchPoint in source.LodSwitchPoints)
        {
            switchDistances.Add(switchPoint);
        }

        return switchDistances;
    }

    // Attachments and hitboxes are expressed in the space of their bone, which the animated pose of a bone carries into model space
    private static KVObject CreateAttachments(Source1Model source)
    {
        var attachments = KVObject.Array();
        var names = new HashSet<string>();

        foreach (var sourceAttachment in source.Attachments)
        {
            if ((uint)sourceAttachment.BoneIndex >= (uint)source.Bones.Length || !names.Add(sourceAttachment.Name))
            {
                continue;
            }

            var boneName = KVHelpers.MakeArray(source.Bones[sourceAttachment.BoneIndex].Name);
            var rotation = KVHelpers.MakeArray(KVHelpers.ToKVArray(Quaternion.CreateFromRotationMatrix(sourceAttachment.Local)));
            var offset = KVHelpers.MakeArray(KVHelpers.ToKVArray(sourceAttachment.Local.Translation));
            var weight = KVHelpers.MakeArray(1f);
            var rootTransform = KVHelpers.MakeArray(false);

            var attachment = KVObject.Collection();
            attachment.Add("m_name", sourceAttachment.Name);
            attachment.Add("m_bIgnoreRotation", (sourceAttachment.Flags & WorldAlignAttachmentFlag) != 0);
            attachment.Add("m_nInfluences", 1);
            attachment.Add("m_influenceNames", boneName);
            attachment.Add("m_vInfluenceRotations", rotation);
            attachment.Add("m_vInfluenceOffsets", offset);
            attachment.Add("m_influenceWeights", weight);
            attachment.Add("m_bInfluenceRootTransform", rootTransform);

            attachments.Add(attachment);
        }

        return attachments;
    }

    private static KVObject CreateHitboxSets(Source1Model source)
    {
        var sets = KVObject.Array();
        var names = new HashSet<string>();

        foreach (var sourceSet in source.HitboxSets)
        {
            if (!names.Add(sourceSet.Name))
            {
                continue;
            }

            var hitboxes = KVObject.Array();

            foreach (var sourceHitbox in sourceSet.Hitboxes)
            {
                if ((uint)sourceHitbox.BoneIndex >= (uint)source.Bones.Length)
                {
                    continue;
                }

                var bone = source.Bones[sourceHitbox.BoneIndex];

                var hitbox = KVObject.Collection();
                hitbox.Add("m_name", sourceHitbox.Name);
                hitbox.Add("m_sSurfaceProperty", bone.SurfaceProperty);
                hitbox.Add("m_sBoneName", bone.Name);
                hitbox.Add("m_vMinBounds", KVHelpers.ToKVArray(sourceHitbox.Min));
                hitbox.Add("m_vMaxBounds", KVHelpers.ToKVArray(sourceHitbox.Max));
                hitbox.Add("m_flShapeRadius", 0f);
                hitbox.Add("m_nGroupId", sourceHitbox.Group);
                hitbox.Add("m_nShapeType", (int)Hitbox.HitboxShape.Box);
                hitbox.Add("m_bTranslationOnly", false);

                hitboxes.Add(hitbox);
            }

            var set = KVObject.Collection();
            set.Add("m_name", sourceSet.Name);
            set.Add("m_hitboxes", hitboxes);
            sets.Add(set);
        }

        return sets;
    }

    private static List<ModelBone> CreateBones(Source1Model source, Matrix4x4 bindToRest)
    {
        var bones = new List<ModelBone>(source.Bones.Length);

        // The vertices are bound to the inverse of PoseToBone, and carried into rest space by the same rotation.
        // The local rest pose in the bone data is not in rest space, so it is derived from the bind transforms.
        var bindToModel = new Matrix4x4[source.Bones.Length];

        for (var i = 0; i < bindToModel.Length; i++)
        {
            var bind = Matrix4x4.Invert(source.Bones[i].PoseToBone, out var inverse) ? inverse : Matrix4x4.Identity;
            bindToModel[i] = bind * bindToRest;
        }

        for (var i = 0; i < source.Bones.Length; i++)
        {
            var bone = source.Bones[i];
            var local = bindToModel[i];

            if ((uint)bone.ParentIndex < (uint)i && Matrix4x4.Invert(bindToModel[bone.ParentIndex], out var parentInverse))
            {
                local = bindToModel[i] * parentInverse;
            }

            Matrix4x4.Decompose(local, out _, out var rotation, out var translation);

            bones.Add(new ModelBone(bone.Name, bone.ParentIndex, translation, rotation, 1f));
        }

        return bones;
    }

    private static List<ModelMaterialGroup> CreateMaterialGroups(Source1Model source, List<Source1Mesh> meshes, ModelMaterialPaths materialPaths)
    {
        var defaultMaterials = new List<string>();
        var firstMeshOfMaterial = new Dictionary<string, Source1Mesh>(StringComparer.OrdinalIgnoreCase);

        foreach (var mesh in meshes)
        {
            var path = materialPaths.Get(mesh, 0);

            if (firstMeshOfMaterial.TryAdd(path, mesh))
            {
                defaultMaterials.Add(path);
            }
        }

        var skinCount = Math.Max(1, source.SkinFamilies.Length);
        var groups = new List<ModelMaterialGroup>(skinCount);

        for (var skin = 0; skin < skinCount; skin++)
        {
            var materials = new List<string>(defaultMaterials.Count);

            foreach (var defaultMaterial in defaultMaterials)
            {
                var mesh = firstMeshOfMaterial[defaultMaterial];
                materials.Add(materialPaths.Get(mesh, skin));
            }

            groups.Add(new ModelMaterialGroup(GetSkinName(skin), materials));
        }

        return groups;
    }

    // The path of the material of a mesh. The material of an eyeball or of teeth is a variant, which carries what the
    // model knows about the eyeball or the mouth to its shader.
    private sealed class ModelMaterialPaths(Source1Model source, string[] paths, Matrix4x4 bindToRest, Source1FileSystem? fileSystem)
    {
        private readonly Dictionary<string, bool> isTeeth = new(StringComparer.OrdinalIgnoreCase);

        public string Get(Source1Mesh mesh, int skin)
        {
            var index = GetMaterialIndex(source, mesh, skin);

            if ((uint)index >= (uint)paths.Length)
            {
                return string.Empty;
            }

            var path = paths[index];

            if (mesh.Eyeball is { } eyeball && (uint)eyeball.Bone < (uint)source.Bones.Length)
            {
                return Source1Paths.AddVariant(path, Source1ModelMaterials.GetEyeballVariant(ToBindSpace(eyeball)));
            }

            if (source.Mouth is { } mouth && (uint)mouth.Bone < (uint)source.Bones.Length && IsTeeth(path))
            {
                return Source1Paths.AddVariant(path, Source1ModelMaterials.GetMouthVariant(mouth with { Forward = ToBindDirection(mouth.Bone, mouth.Forward) }));
            }

            return path;
        }

        private Source1Eyeball ToBindSpace(Source1Eyeball eyeball)
        {
            var bind = GetBoneToBind(eyeball.Bone);

            return eyeball with
            {
                Origin = Vector3.Transform(eyeball.Origin, bind),
                Up = Vector3.Normalize(Vector3.TransformNormal(eyeball.Up, bind)),
                Forward = Vector3.Normalize(Vector3.TransformNormal(eyeball.Forward, bind)),
            };
        }

        private Vector3 ToBindDirection(int bone, Vector3 direction)
        {
            return Vector3.Normalize(Vector3.TransformNormal(direction, GetBoneToBind(bone)));
        }

        // Takes a position in the space of a bone to the space the converted vertices are in
        private Matrix4x4 GetBoneToBind(int bone)
        {
            var boneToPose = Matrix4x4.Invert(source.Bones[bone].PoseToBone, out var inverse) ? inverse : Matrix4x4.Identity;

            return boneToPose * bindToRest;
        }

        private bool IsTeeth(string path)
        {
            if (fileSystem == null)
            {
                return false;
            }

            if (isTeeth.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var teeth = false;

            try
            {
                teeth = VmtFile.Load(path, fileSystem.OpenFile).NormalizedShaderName == "teeth";
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
            }

            isTeeth[path] = teeth;

            return teeth;
        }
    }

    private static ConvertedVertex[] CreateVertices(Source1Mesh mesh, Matrix4x4 bindToRest)
    {
        var vertices = new ConvertedVertex[mesh.Vertices.Length];

        for (var i = 0; i < vertices.Length; i++)
        {
            var source = mesh.Vertices[i];
            var tangent = Vector3.TransformNormal(new Vector3(source.Tangent.X, source.Tangent.Y, source.Tangent.Z), bindToRest);

            vertices[i] = new ConvertedVertex
            {
                Position = Vector3.Transform(source.Position, bindToRest),
                Normal = Vector3.TransformNormal(source.Normal, bindToRest),
                Tangent = new Vector4(tangent, -source.Tangent.W),
                TexCoord = source.TexCoord,
                BoneIndices = PackBoneIndices(source),
                BoneWeights = PackBoneWeights(source),
            };
        }

        return vertices;
    }

    private static uint PackBoneIndices(in Source1Vertex vertex)
    {
        var bone1 = vertex.NumBones > 1 ? vertex.Bone1 : (byte)0;
        var bone2 = vertex.NumBones > 2 ? vertex.Bone2 : (byte)0;

        return vertex.Bone0 | ((uint)bone1 << 8) | ((uint)bone2 << 16);
    }

    private static uint PackBoneWeights(in Source1Vertex vertex)
    {
        if (vertex.NumBones <= 1)
        {
            return 255;
        }

        var weight1 = MathUtils.ToUnorm8(vertex.Weight1);
        var weight2 = vertex.NumBones > 2 ? MathUtils.ToUnorm8(vertex.Weight2) : (byte)0;

        // The first weight takes what the others leave so the weights keep summing to one
        var weight0 = (byte)Math.Max(255 - weight1 - weight2, 0);

        return weight0 | ((uint)weight1 << 8) | ((uint)weight2 << 16);
    }

    /// <summary>Gets the name of the material group that a converted model has for a skin family.</summary>
    internal static string GetSkinName(int skin) => string.Create(CultureInfo.InvariantCulture, $"skin{skin}");

    // A skin index the model does not have shows the first skin family
    internal static string GetSkinName(Source1Model source, int skin)
    {
        var skinCount = Math.Max(1, source.SkinFamilies.Length);
        var index = (uint)skin < (uint)skinCount ? skin : 0;

        return GetSkinName(index);
    }

    // The layout of a matrix in a mesh's inverse bind pose: columns one after another
    internal static KVObject ToArray(Matrix4x4 value)
    {
        var array = KVObject.Array();
        array.Add(value.M11);
        array.Add(value.M21);
        array.Add(value.M31);
        array.Add(value.M41);
        array.Add(value.M12);
        array.Add(value.M22);
        array.Add(value.M32);
        array.Add(value.M42);
        array.Add(value.M13);
        array.Add(value.M23);
        array.Add(value.M33);
        array.Add(value.M43);
        array.Add(value.M14);
        array.Add(value.M24);
        array.Add(value.M34);
        array.Add(value.M44);
        return array;
    }
}
