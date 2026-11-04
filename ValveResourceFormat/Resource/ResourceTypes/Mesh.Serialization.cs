using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes.ModelData;
using static ValveResourceFormat.IO.KVHelpers;

namespace ValveResourceFormat.ResourceTypes
{
    public partial class Mesh
    {
        internal static KVObject CreateData(ModelMeshSource source)
        {
            var drawCalls = KVObject.Array();

            foreach (var drawCall in source.DrawCalls)
            {
                drawCalls.Add(CreateDrawCallData(drawCall, source.Buffers));
            }

            var sceneObject = KVObject.Collection();
            sceneObject.Add("m_vMinBounds", ToKVArray(source.MinBounds));
            sceneObject.Add("m_vMaxBounds", ToKVArray(source.MaxBounds));
            sceneObject.Add("m_drawCalls", drawCalls);
            sceneObject.Add("m_drawBounds", KVObject.Array());
            sceneObject.Add("m_meshlets", KVObject.Array());
            sceneObject.Add("m_vTintColor", ToKVArray(Vector4.Zero));

            var sceneObjects = KVObject.Array();
            sceneObjects.Add(sceneObject);

            var data = KVObject.Collection();
            data.Add("_class", "CRenderMesh");
            data.Add("m_sceneObjects", sceneObjects);
            data.Add("m_constraints", KVObject.Array());
            data.Add("m_skeleton", CreateSkeletonData(source));

            return data;
        }

        private static KVObject CreateDrawCallData(MeshDrawCall drawCall, VBIB buffers)
        {
            var indexBuffer = CreateBufferReference(drawCall.IndexBufferIndex);
            var vertexBuffers = KVObject.Array();
            vertexBuffers.Add(CreateBufferReference(drawCall.VertexBufferIndex));

            var data = KVObject.Collection();
            data.Add("m_nPrimitiveType", "RENDER_PRIM_TRIANGLES");
            data.Add("m_nBaseVertex", drawCall.BaseVertex);
            data.Add("m_nVertexCount", drawCall.VertexCount);
            data.Add("m_nStartIndex", drawCall.StartIndex);
            data.Add("m_nIndexCount", drawCall.IndexCount);
            data.Add("m_vTintColor", ToKVArray(Vector3.One));
            data.Add("m_flAlpha", 1f);
            data.Add("m_nFirstMeshlet", 0);
            data.Add("m_nNumMeshlets", 0);
            data.Add("m_indexBuffer", indexBuffer);
            data.Add("m_material", Model.CreateResourceReference(drawCall.Material));
            data.Add("m_vertexBuffers", vertexBuffers);
            data.Add("m_bUseCompressedNormalTangent", HasCompressedNormalTangent(buffers.VertexBuffers[drawCall.VertexBufferIndex]));

            return data;
        }

        private static KVObject CreateBufferReference(int bufferIndex)
        {
            var reference = KVObject.Collection();
            reference.Add("m_hBuffer", bufferIndex);
            reference.Add("m_nBindOffsetBytes", 0);

            return reference;
        }

        private static bool HasCompressedNormalTangent(VBIB.OnDiskBufferData vertexBuffer)
        {
            foreach (var field in vertexBuffer.InputLayoutFields)
            {
                if (field.SemanticName == "NORMAL")
                {
                    return field.Format is DXGI_FORMAT.R32_UINT or DXGI_FORMAT.R8G8B8A8_UNORM;
                }
            }

            return false;
        }

        private static KVObject CreateSkeletonData(ModelMeshSource source)
        {
            var bones = KVObject.Array();
            var boneParents = KVObject.Array();
            var boneIndices = new Dictionary<string, int>(source.Bones.Count);

            for (var i = 0; i < source.Bones.Count; i++)
            {
                // The first bone of a name is the one a parent name refers to
                boneIndices.TryAdd(source.Bones[i].Name, i);
            }

            foreach (var meshBone in source.Bones)
            {
                if (!boneIndices.TryGetValue(meshBone.ParentName, out var parentIndex))
                {
                    parentIndex = -1;
                }

                var bone = KVObject.Collection();
                bone.Add("m_boneName", meshBone.Name);
                bone.Add("m_parentName", meshBone.ParentName);
                bone.Add("m_invBindPose", ToKVArray(meshBone.InverseBindPose));
                bones.Add(bone);
                boneParents.Add(parentIndex);
            }

            var skeleton = KVObject.Collection();
            skeleton.Add("m_bones", bones);
            skeleton.Add("m_boneParents", boneParents);
            skeleton.Add("m_nBoneWeightCount", source.BoneWeightCount);

            return skeleton;
        }
    }
}
