using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes.ModelData;

namespace ValveResourceFormat.Source1.Model;

/// <summary>
/// Bakes the sequences of a Source 1 model into the embedded animation data of a Source 2 model.
/// Each animation of a sequence is decoded to full precision frames of bone-local transforms, and the sequence
/// keeps its blend grid, pose parameters, autolayers, events, activity and bone weights as a legacy sequence.
/// </summary>
internal sealed class Source1AnimationBaker
{
    private const int PositionDecoder = 0;
    private const int QuaternionDecoder = 1;
    private const int PositionChannel = 0;
    private const int AngleChannel = 1;

    private readonly Source1Model source;
    private readonly List<string> names = [];
    private readonly Dictionary<string, int> nameIndices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> sequenceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Source1Animation, int> firstSegments = [];
    private readonly Dictionary<string, int> boneMaskIndices = [];
    private readonly KVObject animations = KVObject.Array();
    private readonly KVObject segments = KVObject.Array();
    private readonly KVObject sequences = KVObject.Array();
    private readonly KVObject boneMasks = KVObject.Array();

    private Source1AnimationBaker(Source1Model source)
    {
        this.source = source;
    }

    /// <summary>Bakes the sequences of a model that have all their data.</summary>
    /// <param name="source">The model to bake the sequences of.</param>
    /// <returns>The animation data, or <see langword="null"/> when the model has no sequence that can be played.</returns>
    public static ModelAnimationSource? Bake(Source1Model source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new Source1AnimationBaker(source).Bake();
    }

    private ModelAnimationSource? Bake()
    {
        var playable = new List<Source1Sequence>();

        foreach (var sequence in source.Sequences)
        {
            if (sequence.IsPlayable)
            {
                playable.Add(sequence);
                sequenceNames.Add(sequence.Name);
                AddName(sequence.Name);
            }
        }

        if (playable.Count == 0)
        {
            return null;
        }

        // The mask at index zero stands for a sequence that has no weight list
        boneMasks.Add(CreateBoneMask(string.Empty, []));

        foreach (var sequence in playable)
        {
            AddSequence(sequence);
        }

        return new ModelAnimationSource(CreateAnimationGroup(), CreateAnimationData(), CreateSequenceGroup());
    }

    private int AddName(string name)
    {
        if (nameIndices.TryGetValue(name, out var index))
        {
            return index;
        }

        index = names.Count;
        names.Add(name);
        nameIndices[name] = index;

        return index;
    }

    private void AddSequence(Source1Sequence sequence)
    {
        var blendCount = sequence.Blends.Length;
        var fetch = CreateFetch(sequence);
        var references = KVObject.Array();
        var referencedCount = fetch.Count;

        for (var i = 0; i < referencedCount; i++)
        {
            var animationName = blendCount == 1
                ? sequence.Name
                : string.Create(CultureInfo.InvariantCulture, $"{sequence.Name}$blend{i}");

            references.Add(AddName(animationName));
            AddAnimation(animationName, sequence, sequence.Blends[i], i);
        }

        fetch.Data.Add("m_localReferenceArray", references);

        var seqDesc = KVObject.Collection();
        seqDesc.Add("m_sName", sequence.Name);
        seqDesc.Add("m_flags", CreateSequenceFlags(sequence));
        seqDesc.Add("m_activityArray", CreateActivities(sequence));
        seqDesc.Add("m_transition", CreateTransition(sequence));
        seqDesc.Add("m_autoLayerArray", CreateAutoLayers(sequence));
        seqDesc.Add("m_fetch", fetch.Data);
        seqDesc.Add("m_nLocalWeightlist", GetBoneMaskIndex(sequence));

        sequences.Add(seqDesc);
    }

    private void AddAnimation(string name, Source1Sequence sequence, Source1Animation animation, int blendIndex)
    {
        var firstSegment = GetFirstSegment(animation);
        var lastFrame = animation.FrameCount - 1;

        var segmentIndices = KVObject.Array();
        segmentIndices.Add(firstSegment + PositionChannel);
        segmentIndices.Add(firstSegment + AngleChannel);

        var frameBlock = KVObject.Collection();
        frameBlock.Add("m_nStartFrame", 0);
        frameBlock.Add("m_nEndFrame", lastFrame);
        frameBlock.Add("m_segmentIndexArray", segmentIndices);

        var frameBlocks = KVObject.Array();
        frameBlocks.Add(frameBlock);

        var data = KVObject.Collection();
        data.Add("m_nFrames", animation.FrameCount);
        data.Add("m_frameblockArray", frameBlocks);

        var flags = KVObject.Collection();
        flags.Add("m_bLooping", sequence.IsLooping);
        flags.Add("m_bHidden", blendIndex > 0 || sequence.Blends.Length > 1);
        flags.Add("m_bDelta", animation.IsDelta);

        var movements = KVObject.Array();

        foreach (var movement in animation.Movements)
        {
            movements.Add(CreateMovement(movement));
        }

        var events = KVObject.Array();

        if (blendIndex == 0)
        {
            foreach (var sequenceEvent in sequence.Events)
            {
                events.Add(CreateEvent(sequenceEvent, lastFrame));
            }
        }

        var animDesc = KVObject.Collection();
        animDesc.Add("m_name", name);
        animDesc.Add("fps", animation.Fps);
        animDesc.Add("m_flags", flags);
        animDesc.Add("m_pData", data);
        animDesc.Add("m_movementArray", movements);
        animDesc.Add("m_eventArray", events);
        animDesc.Add("m_activityArray", KVObject.Array());

        animations.Add(animDesc);
    }

    private int GetFirstSegment(Source1Animation animation)
    {
        if (firstSegments.TryGetValue(animation, out var first))
        {
            return first;
        }

        var boneCount = source.Bones.Length;
        var frameCount = animation.FrameCount;
        var positions = new Vector3[frameCount * boneCount];
        var rotations = new Quaternion[frameCount * boneCount];
        var framePositions = new Vector3[boneCount];
        var frameRotations = new Quaternion[boneCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            animation.GetFrame(frame, framePositions, frameRotations);
            framePositions.CopyTo(positions, frame * boneCount);
            frameRotations.CopyTo(rotations, frame * boneCount);
        }

        first = segments.Count;
        segments.Add(CreateSegment(PositionChannel, PositionDecoder, MemoryMarshal.AsBytes(positions.AsSpan())));
        segments.Add(CreateSegment(AngleChannel, QuaternionDecoder, MemoryMarshal.AsBytes(rotations.AsSpan())));
        firstSegments[animation] = first;

        return first;
    }

    // The container is the header of the segment, the channel elements it stores, then the data of every frame in turn
    private KVObject CreateSegment(int channel, int decoder, ReadOnlySpan<byte> frames)
    {
        var boneCount = source.Bones.Length;
        const int headerSize = 8;
        var elementsSize = boneCount * sizeof(short);
        var container = new byte[headerSize + elementsSize + frames.Length];

        BinaryPrimitives.WriteInt16LittleEndian(container.AsSpan(0), (short)decoder);
        BinaryPrimitives.WriteInt16LittleEndian(container.AsSpan(2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(container.AsSpan(4), (short)boneCount);

        for (var i = 0; i < boneCount; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(container.AsSpan(headerSize + (i * sizeof(short))), (short)i);
        }

        frames.CopyTo(container.AsSpan(headerSize + elementsSize));

        var segment = KVObject.Collection();
        segment.Add("m_nLocalChannel", channel);
        segment.Add("m_container", KVObject.Blob(container));

        return segment;
    }

    private (KVObject Data, int Count) CreateFetch(Source1Sequence sequence)
    {
        var countX = sequence.BlendCountX;
        var countY = sequence.BlendCountY;
        var poseCount = source.PoseParameters.Length;
        var isGrid = countX > 1 && countY > 1;
        var isLine = !isGrid && sequence.Blends.Length > 1;
        var axis = countX > 1 ? 0 : 1;
        var axisPoseIndex = axis == 0 ? sequence.BlendIndexX : sequence.BlendIndexY;
        var isDriven = isGrid
            ? (uint)sequence.BlendIndexX < (uint)poseCount && (uint)sequence.BlendIndexY < (uint)poseCount
            : isLine && (uint)axisPoseIndex < (uint)poseCount;

        var flags = KVObject.Collection();
        var localPose = KVObject.Array();
        var keys0 = KVObject.Array();
        var keys1 = KVObject.Array();
        var groupSize = KVObject.Array();
        var referencedCount = 1;

        if (isDriven && isGrid)
        {
            flags.Add("m_b2D", true);
            flags.Add("m_b2D_TRI", true);
            localPose.Add(sequence.BlendIndexX);
            localPose.Add(sequence.BlendIndexY);
            groupSize.Add(countX);
            groupSize.Add(countY);

            foreach (var key in GetBlendKeys(sequence, 0))
            {
                keys0.Add(key);
            }

            var rowKeys = GetBlendKeys(sequence, 1);

            for (var i = 0; i < sequence.Blends.Length; i++)
            {
                keys1.Add(rowKeys[i / countX]);
            }

            referencedCount = sequence.Blends.Length;
        }
        else if (isDriven)
        {
            flags.Add("m_b1D", true);
            localPose.Add(axisPoseIndex);
            groupSize.Add(sequence.Blends.Length);

            foreach (var key in GetBlendKeys(sequence, axis))
            {
                keys0.Add(key);
            }

            referencedCount = sequence.Blends.Length;
        }

        var fetch = KVObject.Collection();
        fetch.Add("m_flags", flags);
        fetch.Add("m_nLocalPose", localPose);
        fetch.Add("m_poseKeyArray0", keys0);
        fetch.Add("m_poseKeyArray1", keys1);
        fetch.Add("m_nGroupSize", groupSize);
        fetch.Add("m_nLocalCyclePoseParameter", (sequence.Flags & Source1AnimationFlags.CyclePose) != 0 ? sequence.CyclePoseIndex : -1);

        return (fetch, referencedCount);
    }

    // The pose parameter value each animation of one blend axis sits at
    private static float[] GetBlendKeys(Source1Sequence sequence, int axis)
    {
        var count = axis == 0 ? sequence.BlendCountX : sequence.BlendCountY;
        var keys = new float[count];

        if (sequence.PoseKeys.Length > 0)
        {
            var offset = axis == 0 ? 0 : sequence.BlendCountX;
            Array.Copy(sequence.PoseKeys, offset, keys, 0, count);

            return keys;
        }

        var start = axis == 0 ? sequence.BlendStartX : sequence.BlendStartY;
        var end = axis == 0 ? sequence.BlendEndX : sequence.BlendEndY;

        for (var i = 0; i < count; i++)
        {
            var fraction = count > 1 ? (float)i / (count - 1) : 0f;
            keys[i] = start + ((end - start) * fraction);
        }

        return keys;
    }

    private static KVObject CreateSequenceFlags(Source1Sequence sequence)
    {
        var flags = KVObject.Collection();
        flags.Add("m_bLooping", sequence.IsLooping);
        flags.Add("m_bHidden", (sequence.Flags & Source1AnimationFlags.Hidden) != 0);
        flags.Add("m_bLegacyDelta", sequence.IsDelta);
        flags.Add("m_bLegacyWorldspace", (sequence.Flags & Source1AnimationFlags.World) != 0);
        flags.Add("m_bLegacyRealtime", (sequence.Flags & Source1AnimationFlags.Realtime) != 0);
        flags.Add("m_bAutoplay", (sequence.Flags & Source1AnimationFlags.AutoPlay) != 0);

        return flags;
    }

    private static KVObject CreateActivities(Source1Sequence sequence)
    {
        var activities = KVObject.Array();

        if (sequence.ActivityName.Length == 0)
        {
            return activities;
        }

        var activity = KVObject.Collection();
        activity.Add("m_name", sequence.ActivityName);
        activity.Add("m_nActivity", sequence.Activity);
        activity.Add("m_nFlags", 0);
        activity.Add("m_nWeight", sequence.ActivityWeight);
        activities.Add(activity);

        return activities;
    }

    private static KVObject CreateTransition(Source1Sequence sequence)
    {
        var transition = KVObject.Collection();
        transition.Add("m_flFadeInTime", sequence.FadeInTime);
        transition.Add("m_flFadeOutTime", sequence.FadeOutTime);

        return transition;
    }

    private KVObject CreateAutoLayers(Source1Sequence sequence)
    {
        var layers = KVObject.Array();
        var hasLocalContext = (sequence.Flags & Source1AnimationFlags.Local) != 0;

        foreach (var sourceLayer in sequence.AutoLayers)
        {
            if (sourceLayer.Sequence == null || !sequenceNames.Contains(sourceLayer.Sequence.Name))
            {
                continue;
            }

            var isLocal = (sourceLayer.Flags & Source1AutoLayerFlags.Local) != 0;

            if (isLocal && !hasLocalContext)
            {
                continue;
            }

            var flags = KVObject.Collection();
            flags.Add("m_bPost", (sourceLayer.Flags & Source1AutoLayerFlags.Post) != 0);
            flags.Add("m_bSpline", (sourceLayer.Flags & Source1AutoLayerFlags.Spline) != 0);
            flags.Add("m_bXFade", (sourceLayer.Flags & Source1AutoLayerFlags.XFade) != 0);
            flags.Add("m_bNoBlend", (sourceLayer.Flags & Source1AutoLayerFlags.NoBlend) != 0);
            flags.Add("m_bLocal", isLocal);
            flags.Add("m_bPose", (sourceLayer.Flags & Source1AutoLayerFlags.Pose) != 0);

            var layer = KVObject.Collection();
            layer.Add("m_nLocalReference", AddName(sourceLayer.Sequence.Name));
            layer.Add("m_nLocalPose", sourceLayer.PoseParameterIndex);
            layer.Add("m_flags", flags);
            layer.Add("m_start", sourceLayer.Start);
            layer.Add("m_peak", sourceLayer.Peak);
            layer.Add("m_tail", sourceLayer.Tail);
            layer.Add("m_end", sourceLayer.End);
            layers.Add(layer);
        }

        return layers;
    }

    private int GetBoneMaskIndex(Source1Sequence sequence)
    {
        var weights = sequence.BoneWeights;
        var drivesEveryBone = true;
        var drivesAnyBone = false;

        foreach (var weight in weights)
        {
            drivesEveryBone &= weight == 1f;
            drivesAnyBone |= weight > 0f;
        }

        if (drivesEveryBone || !drivesAnyBone)
        {
            return 0;
        }

        var key = string.Join(',', Array.ConvertAll(weights, static weight => weight.ToString("R", CultureInfo.InvariantCulture)));

        if (boneMaskIndices.TryGetValue(key, out var index))
        {
            return index;
        }

        index = boneMasks.Count;
        boneMaskIndices[key] = index;
        boneMasks.Add(CreateBoneMask(sequence.Name, weights));

        return index;
    }

    private static KVObject CreateBoneMask(string name, float[] weights)
    {
        var bones = KVObject.Array();
        var boneWeights = KVObject.Array();

        for (var i = 0; i < weights.Length; i++)
        {
            if (weights[i] > 0f)
            {
                bones.Add(i);
                boneWeights.Add(weights[i]);
            }
        }

        var mask = KVObject.Collection();
        mask.Add("m_sName", name);
        mask.Add("m_nLocalBoneArray", bones);
        mask.Add("m_flBoneWeightArray", boneWeights);

        return mask;
    }

    private static KVObject CreateMovement(Source1Movement movement)
    {
        var data = KVObject.Collection();
        data.Add("endframe", movement.EndFrame);
        data.Add("motionflags", movement.Flags);
        data.Add("v0", movement.StartVelocity);
        data.Add("v1", movement.EndVelocity);
        data.Add("angle", movement.Angle);
        data.Add("vector", Source1ModelConverter.ToArray(movement.Vector));
        data.Add("position", Source1ModelConverter.ToArray(movement.Position));

        return data;
    }

    private static KVObject CreateEvent(Source1AnimationEvent sequenceEvent, int lastFrame)
    {
        var name = sequenceEvent.Name.Length > 0
            ? sequenceEvent.Name
            : sequenceEvent.EventId.ToString(CultureInfo.InvariantCulture);

        var data = KVObject.Collection();
        data.Add("m_sEventName", name);
        data.Add("m_nFrame", (int)MathF.Round(sequenceEvent.Cycle * lastFrame));
        data.Add("m_flCycle", sequenceEvent.Cycle);
        data.Add("m_sOptions", sequenceEvent.Options);

        return data;
    }

    private KVObject CreateAnimationGroup()
    {
        var channels = KVObject.Array();
        channels.Add(CreateChannel("Position"));
        channels.Add(CreateChannel("Angle"));

        var decodeKey = KVObject.Collection();
        decodeKey.Add("m_dataChannelArray", channels);
        decodeKey.Add("m_userArray", KVObject.Array());

        var group = KVObject.Collection();
        group.Add("m_decodeKey", decodeKey);

        return group;
    }

    private KVObject CreateChannel(string variableName)
    {
        var elementNames = KVObject.Array();
        var elementIndices = KVObject.Array();

        for (var i = 0; i < source.Bones.Length; i++)
        {
            elementNames.Add(source.Bones[i].Name);
            elementIndices.Add(i);
        }

        var channel = KVObject.Collection();
        channel.Add("m_szElementNameArray", elementNames);
        channel.Add("m_nElementIndexArray", elementIndices);
        channel.Add("m_szChannelClass", "BoneChannel");
        channel.Add("m_szVariableName", variableName);

        return channel;
    }

    private KVObject CreateAnimationData()
    {
        var decoders = KVObject.Array();
        decoders.Add(CreateDecoder("CCompressedFullVector3"));
        decoders.Add(CreateDecoder("CCompressedFullQuaternion"));

        var data = KVObject.Collection();
        data.Add("m_animArray", animations);
        data.Add("m_decoderArray", decoders);
        data.Add("m_segmentArray", segments);

        return data;
    }

    private static KVObject CreateDecoder(string name)
    {
        var decoder = KVObject.Collection();
        decoder.Add("m_szName", name);

        return decoder;
    }

    private KVObject CreateSequenceGroup()
    {
        var sequenceNameArray = KVObject.Array();

        foreach (var name in names)
        {
            sequenceNameArray.Add(name);
        }

        var boneNames = KVObject.Array();

        foreach (var bone in source.Bones)
        {
            boneNames.Add(bone.Name);
        }

        var poseParameters = KVObject.Array();

        foreach (var parameter in source.PoseParameters)
        {
            var poseParameter = KVObject.Collection();
            poseParameter.Add("m_sName", parameter.Name);
            poseParameter.Add("m_flStart", MathF.Min(parameter.Start, parameter.End));
            poseParameter.Add("m_flEnd", MathF.Max(parameter.Start, parameter.End));
            poseParameter.Add("m_flLoop", parameter.Loop);
            poseParameter.Add("m_bLooping", parameter.Loop != 0f);
            poseParameters.Add(poseParameter);
        }

        var group = KVObject.Collection();
        group.Add("m_localS1SeqDescArray", sequences);
        group.Add("m_localSequenceNameArray", sequenceNameArray);
        group.Add("m_localBoneMaskArray", boneMasks);
        group.Add("m_localBoneNameArray", boneNames);
        group.Add("m_localPoseParamArray", poseParameters);

        return group;
    }
}
