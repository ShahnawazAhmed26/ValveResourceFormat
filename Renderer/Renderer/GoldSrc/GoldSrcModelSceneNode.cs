using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.GoldSrc;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>
    /// Scene node that renders a GoldSrc studio model: body groups, skins and sequence playback.
    /// </summary>
    /// <remarks>
    /// Vertices stay in the space of the bone they belong to, so the node always skins on the GPU from the
    /// bone matrices of the current pose, which are built from the rest values of the bones when nothing plays.
    /// Geometry is uploaded once per model and shared between the nodes of that model.
    /// </remarks>
    internal sealed class GoldSrcModelSceneNode : MeshCollectionNode, ISkinnedSceneNode
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            [VertexAttribute(VertexSlot.Position)] public Vector3 Position;
            [VertexAttribute(VertexSlot.Normal)] public Vector3 Normal;

            // In texels of the texture, which the shader divides by its size
            [VertexAttribute(VertexSlot.TexCoord)] public Vector2 TexCoord;

            // The bone of the position, then the bone of the normal
            [VertexAttribute(VertexSlot.BlendIndices, DXGI_FORMAT.R8G8B8A8_UINT)] public uint BoneIndices;

            public static readonly VertexInputLayout InputLayout = VertexInputLayout.FromStruct<Vertex>();
        }

        private sealed class MeshGeometry
        {
            public required string Name { get; init; }
            public required VBIB Buffers { get; init; }
        }

        private sealed class MeshEntry
        {
            public required GoldSrcMdlMesh Source { get; init; }
            public required int BodyPart { get; init; }
            public required int SubModel { get; init; }
            public required RenderableMesh Renderable { get; init; }
            public required DrawCall DrawCall { get; init; }
        }

        private sealed class GeometryCache
        {
            public Dictionary<GoldSrcMdlMesh, MeshGeometry> Meshes { get; } = [];
            public int Id { get; } = Interlocked.Increment(ref lastId);
        }

        private static int lastId;
        private static readonly ConditionalWeakTable<GoldSrcMdlFile, GeometryCache> GeometryByModel = [];

        private const float DefaultHalfExtent = 32f;

        /// <summary>Gets the model this node renders.</summary>
        public GoldSrcMdlFile Model { get; }

        /// <summary>Gets the number of skin families the model has.</summary>
        public int SkinCount => Math.Max(1, textureSource.SkinFamilies.Length);

        /// <summary>Gets the skin family that is shown.</summary>
        public int Skin { get; private set; }

        /// <summary>Gets how the model is drawn.</summary>
        public GoldSrcRenderSettings RenderSettings { get; private set; } = GoldSrcRenderSettings.Default;

        /// <summary>Gets the light the model receives.</summary>
        public GoldSrcStudioLight Light { get; private set; } = GoldSrcStudioLight.Default;

        /// <summary>Gets the sequences the node can play: the ones whose animations are loaded.</summary>
        public IReadOnlyList<GoldSrcSequence> PlayableSequences => playableSequences;

        /// <summary>Gets the sequence that plays, or <see langword="null"/> when the model shows its rest pose.</summary>
        public GoldSrcSequence? Sequence { get; private set; }

        /// <summary>Gets or sets whether the sequence stops advancing.</summary>
        public bool Paused { get; set; }

        /// <summary>Gets or sets the playback rate, where 1 is the speed of the sequence.</summary>
        public float PlaybackSpeed { get; set; } = 1f;

        /// <summary>Gets or sets whether a sequence that is not marked as looping wraps around at its end instead of holding the last pose.</summary>
        public bool Loop { get; set; }

        /// <summary>Gets or sets the position in the sequence from 0 to 1. Setting it poses the node at that cycle.</summary>
        public float Cycle
        {
            get
            {
                var span = GetFrameSpan(Sequence);
                return span > 0f ? frame / span : 0f;
            }
            set
            {
                frame = value * GetFrameSpan(Sequence);
                poseDirty = true;
            }
        }

        /// <summary>Gets or sets how far a sequence with two animations has moved from the first to the second, from 0 to 1.</summary>
        public float Blend
        {
            get;
            set
            {
                field = value;
                poseDirty = true;
            }
        }

        /// <inheritdoc/>
        public int BoneCount => Model.Bones.Length;

        /// <inheritdoc/>
        public uint TransformSlot { get; set; }

        internal override List<RenderableMesh> AllRenderableMeshes => allMeshes;

        private readonly GoldSrcMdlFile textureSource;
        private readonly List<RenderableMesh> allMeshes = [];
        private readonly List<MeshEntry> entries = [];
        private readonly int[] bodyGroupSelection;
        private readonly GoldSrcSequence[] playableSequences;
        private readonly GoldSrcModelCache cache;
        private readonly Vector3[] localPositions;
        private readonly Quaternion[] localRotations;
        private readonly Matrix4x4[] boneToModel;
        private readonly float[] controllerValues;
        private float frame;
        private bool poseDirty;

        /// <summary>
        /// Creates a GoldSrc model node that keeps its own resources, as for a model that is shown alone.
        /// </summary>
        /// <param name="scene">The scene this node belongs to.</param>
        /// <param name="model">The model to render, with the files it keeps its textures and animations in loaded.</param>
        public GoldSrcModelSceneNode(Scene scene, GoldSrcMdlFile model)
            : this(scene, model, new GoldSrcModelCache(scene, null))
        {
        }

        /// <summary>
        /// Creates a GoldSrc model node.
        /// </summary>
        /// <param name="scene">The scene this node belongs to.</param>
        /// <param name="model">The model to render, with the files it keeps its textures and animations in loaded.</param>
        /// <param name="cache">The resources shared with the other models of the scene.</param>
        /// <param name="skin">The skin family to show.</param>
        /// <param name="renderSettings">How the model is drawn, or <see langword="null"/> to draw it as it is.</param>
        /// <param name="light">The light the model receives, or <see langword="null"/> for the light of a model that is shown alone.</param>
        /// <param name="sequence">The sequence to play from its start, or <see langword="null"/> for the first one the node can play.</param>
        public GoldSrcModelSceneNode(Scene scene, GoldSrcMdlFile model, GoldSrcModelCache cache, int skin = 0, GoldSrcRenderSettings? renderSettings = null, GoldSrcStudioLight? light = null, GoldSrcSequence? sequence = null)
            : base(scene)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(cache);

            Model = model;
            textureSource = model.HasTextures ? model : model.TextureFile ?? model;
            this.cache = cache;
            this.cache.AddUser();
            RenderSettings = renderSettings ?? GoldSrcRenderSettings.Default;
            Light = light ?? GoldSrcStudioLight.Default;
            bodyGroupSelection = new int[model.BodyParts.Length];
            playableSequences = [.. model.Sequences.Where(model.HasAnimationData)];
            localPositions = new Vector3[model.Bones.Length];
            localRotations = new Quaternion[model.Bones.Length];
            boneToModel = new Matrix4x4[model.Bones.Length];
            controllerValues = new float[model.BoneControllers.Length];
            Skin = (uint)skin < (uint)SkinCount ? skin : 0;

            for (var i = 0; i < controllerValues.Length; i++)
            {
                controllerValues[i] = model.GetDefaultControllerValue(i);
            }

            BuildMeshes();
            RebuildRenderableMeshes();
            PlaySequence(sequence ?? playableSequences.FirstOrDefault());
        }

        private static float GetFrameSpan(GoldSrcSequence? sequence)
            => sequence == null ? 0f : Math.Max(sequence.FrameCount - 1, 0);

        private void BuildMeshes()
        {
            var geometryCache = GeometryByModel.GetOrCreateValue(Model);

            for (var partIndex = 0; partIndex < Model.BodyParts.Length; partIndex++)
            {
                var part = Model.BodyParts[partIndex];

                for (var subIndex = 0; subIndex < part.Models.Length; subIndex++)
                {
                    var subModel = part.Models[subIndex];

                    for (var meshIndex = 0; meshIndex < subModel.Meshes.Length; meshIndex++)
                    {
                        var source = subModel.Meshes[meshIndex];

                        if (source.Indices.Length == 0)
                        {
                            continue;
                        }

                        if (!geometryCache.Meshes.TryGetValue(source, out var geometry))
                        {
                            var name = string.Create(CultureInfo.InvariantCulture, $"goldsrc:{geometryCache.Id}:{partIndex}.{subIndex}.{meshIndex}");
                            geometry = CreateGeometry(name, subModel, source);
                            geometryCache.Meshes.Add(source, geometry);
                        }

                        entries.Add(BuildMesh(geometry, partIndex, subIndex, source));
                    }
                }
            }
        }

        private static MeshGeometry CreateGeometry(string name, GoldSrcMdlSubmodel subModel, GoldSrcMdlMesh source)
        {
            var vertices = new Vertex[source.Vertices.Length];

            for (var i = 0; i < vertices.Length; i++)
            {
                var sourceVertex = source.Vertices[i];
                var positionBone = subModel.PositionBones[sourceVertex.Position];
                var normalBone = subModel.NormalBones[sourceVertex.Normal];

                vertices[i] = new Vertex
                {
                    Position = subModel.Positions[sourceVertex.Position],
                    Normal = subModel.Normals[sourceVertex.Normal],
                    TexCoord = new Vector2(sourceVertex.S, sourceVertex.T),
                    BoneIndices = positionBone | ((uint)normalBone << 8),
                };
            }

            return new MeshGeometry
            {
                Name = name,
                Buffers = Vertex.InputLayout.CreateBuffers<Vertex, int>(vertices, source.Indices),
            };
        }

        private MeshEntry BuildMesh(MeshGeometry geometry, int partIndex, int subIndex, GoldSrcMdlMesh source)
        {
            var material = cache.GetMaterial(textureSource, GetTextureIndex(source), RenderSettings, Light);
            var bounds = new AABB(Model.BoundingBoxMin, Model.BoundingBoxMax);
            var mesh = RenderableMesh.CreateMesh(geometry.Name, material, geometry.Buffers, bounds, Scene.RendererContext);
            var drawCall = mesh.DrawCalls.First();

            mesh.ConfigureSkinning(MeshSkinning.OneBone, Model.Bones.Length);
            mesh.SetSkinningActive(true);
            mesh.PlaceDrawCall(drawCall);

            return new MeshEntry
            {
                Source = source,
                BodyPart = partIndex,
                SubModel = subIndex,
                Renderable = mesh,
                DrawCall = drawCall,
            };
        }

        private int GetTextureIndex(GoldSrcMdlMesh mesh)
        {
            var families = textureSource.SkinFamilies;

            if (Skin < families.Length && mesh.SkinRef < families[Skin].Length)
            {
                return families[Skin][mesh.SkinRef];
            }

            return mesh.SkinRef;
        }

        /// <summary>Shows a skin family.</summary>
        /// <param name="skin">The index of the skin family.</param>
        public void SetSkin(int skin)
        {
            skin = (uint)skin < (uint)SkinCount ? skin : 0;

            if (skin == Skin)
            {
                return;
            }

            Skin = skin;
            ApplyMaterials();
        }

        private void ApplyMaterials()
        {
            foreach (var entry in entries)
            {
                entry.DrawCall.SetNewMaterial(cache.GetMaterial(textureSource, GetTextureIndex(entry.Source), RenderSettings, Light));
                entry.Renderable.PlaceDrawCall(entry.DrawCall);
            }
        }

        /// <summary>Gets the sub-model that is shown of a body group.</summary>
        /// <param name="bodyPart">The index of the body group.</param>
        public int GetBodyGroup(int bodyPart) => bodyGroupSelection[bodyPart];

        /// <summary>Chooses the sub-model that is shown of a body group.</summary>
        /// <param name="bodyPart">The index of the body group.</param>
        /// <param name="subModel">The index of the sub-model.</param>
        public void SetBodyGroup(int bodyPart, int subModel)
        {
            bodyGroupSelection[bodyPart] = subModel;
            RebuildRenderableMeshes();
            Scene.MarkParentSetDirty(this);
        }

        /// <summary>Chooses the sub-model of every body group from the body value of an entity.</summary>
        /// <param name="body">The body value, which holds the sub-model of each group as a mixed radix number in the order of <see cref="GoldSrcBodyPart.Base"/>.</param>
        public void SetBody(int body)
        {
            for (var i = 0; i < bodyGroupSelection.Length; i++)
            {
                var part = Model.BodyParts[i];

                if (part.Base <= 0 || part.Models.Length == 0)
                {
                    continue;
                }

                bodyGroupSelection[i] = Math.Max((body / part.Base) % part.Models.Length, 0);
            }

            RebuildRenderableMeshes();
            Scene.MarkParentSetDirty(this);
        }

        /// <summary>Sets the value of a bone controller. The pose updates on the next update.</summary>
        /// <param name="controller">The index of the controller in <see cref="GoldSrcMdlFile.BoneControllers"/>.</param>
        /// <param name="value">The value in the units of the controller.</param>
        public void SetController(int controller, float value)
        {
            controllerValues[controller] = value;
            poseDirty = true;
        }

        /// <summary>Plays a sequence from its start.</summary>
        /// <param name="sequence">The sequence, or <see langword="null"/> to show the rest pose.</param>
        public void PlaySequence(GoldSrcSequence? sequence)
        {
            Sequence = sequence;
            frame = 0f;
            UpdateBoundingBox();
            EvaluatePose();
            Scene.UpdateSkinningTransforms(this);
        }

        private void RebuildRenderableMeshes()
        {
            RenderableMeshes.Clear();
            allMeshes.Clear();

            foreach (var entry in entries)
            {
                allMeshes.Add(entry.Renderable);

                if (bodyGroupSelection[entry.BodyPart] == entry.SubModel)
                {
                    RenderableMeshes.Add(entry.Renderable);
                }
            }
        }

        // Vertices are skinned past the rest hull, so a sequence's own box is used when it has one
        private void UpdateBoundingBox()
        {
            var min = Sequence?.Min ?? Vector3.Zero;
            var max = Sequence?.Max ?? Vector3.Zero;

            if (min == max)
            {
                min = Model.BoundingBoxMin;
                max = Model.BoundingBoxMax;
            }

            if (min == max)
            {
                min = Model.Min;
                max = Model.Max;
            }

            if (min == max)
            {
                min = new Vector3(-DefaultHalfExtent);
                max = new Vector3(DefaultHalfExtent);
            }

            LocalBoundingBox = new AABB(min, max);
        }

        /// <inheritdoc/>
        public override void Update(Scene.UpdateContext context)
        {
            var sequence = Sequence;

            if (sequence != null && !Paused && AdvanceFrame(sequence, context.Timestep))
            {
                poseDirty = true;
            }

            if (poseDirty)
            {
                EvaluatePose();
                Scene.UpdateSkinningTransforms(this);
            }
        }

        // Returns whether the frame moved, which it does not for a single frame sequence or a held last pose
        private bool AdvanceFrame(GoldSrcSequence sequence, float timestep)
        {
            var span = GetFrameSpan(sequence);

            if (span <= 0f)
            {
                return false;
            }

            var next = frame + (timestep * sequence.Fps * PlaybackSpeed);

            if (next >= span || next < 0f)
            {
                next = Loop || sequence.IsLooping ? next - (span * MathF.Floor(next / span)) : Math.Clamp(next, 0f, span);
            }

            if (next == frame)
            {
                return false;
            }

            frame = next;
            return true;
        }

        private void EvaluatePose()
        {
            poseDirty = false;

            var bones = Model.Bones;

            if (Sequence is { } sequence)
            {
                Model.GetPose(sequence, frame, Blend, controllerValues, localPositions, localRotations);
            }
            else
            {
                for (var i = 0; i < bones.Length; i++)
                {
                    var value = bones[i].Value;
                    localPositions[i] = new Vector3(value[0], value[1], value[2]);
                    localRotations[i] = GoldSrcMdlFile.EulerToQuaternion(value[3], value[4], value[5]);
                }
            }

            for (var i = 0; i < bones.Length; i++)
            {
                var local = Matrix4x4.CreateFromQuaternion(localRotations[i]);
                local.Translation = localPositions[i];

                var parent = bones[i].Parent;
                boneToModel[i] = (uint)parent < (uint)i ? local * boneToModel[parent] : local;
            }
        }

        /// <inheritdoc/>
        public void WriteBoneTransforms(Span<OpenTK.Mathematics.Matrix3x4> boneTransforms)
        {
            var count = Math.Min(boneTransforms.Length, boneToModel.Length);

            for (var i = 0; i < count; i++)
            {
                boneTransforms[i] = boneToModel[i].To3x4();
            }
        }

        /// <inheritdoc/>
        public override void Delete() => cache.RemoveUser();

        /// <inheritdoc/>
        public override IEnumerable<string> GetSupportedRenderModes()
            => allMeshes.SelectMany(static mesh => mesh.GetSupportedRenderModes());

#if DEBUG
        /// <inheritdoc/>
        public override void UpdateVertexArrayObjects()
        {
            foreach (var mesh in allMeshes)
            {
                mesh.UpdateVertexArrayObjects();
            }
        }
#endif
    }
}
