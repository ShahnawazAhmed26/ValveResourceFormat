using ValveResourceFormat.ResourceTypes.ModelAnimation;

namespace ValveResourceFormat.GoldSrc;

/// <summary>A GoldSrc sequence sampled at its first animation with the bone controllers at their defaults, as an animation of a converted model.</summary>
internal sealed class GoldSrcSequenceAnimation : AnimationBase
{
    private readonly GoldSrcMdlFile model;
    private readonly GoldSrcSequence sequence;
    private readonly Vector3[] positions;
    private readonly Quaternion[] rotations;

    private GoldSrcSequenceAnimation(GoldSrcMdlFile model, GoldSrcSequence sequence)
    {
        this.model = model;
        this.sequence = sequence;

        positions = new Vector3[model.Bones.Length];
        rotations = new Quaternion[model.Bones.Length];

        Name = sequence.Label;
        Fps = sequence.Fps;
        FrameCount = sequence.FrameCount;
        HoldsLastFrame = !sequence.IsLooping;
    }

    /// <summary>Creates an animation for each sequence of the model whose animations are loaded.</summary>
    public static List<AnimationBase> CreateAll(GoldSrcMdlFile model)
    {
        var animations = new List<AnimationBase>();

        foreach (var sequence in model.Sequences)
        {
            if (model.HasAnimationData(sequence))
            {
                animations.Add(new GoldSrcSequenceAnimation(model, sequence));
            }
        }

        return animations;
    }

    /// <inheritdoc/>
    public override void DecodeFrame(Frame outFrame)
    {
        model.GetPose(sequence, outFrame.FrameIndex, 0f, [], positions, rotations);

        for (var i = 0; i < outFrame.Bones.Length; i++)
        {
            outFrame.Bones[i] = new FrameBone(positions[i], 1f, rotations[i]);
        }
    }

    /// <inheritdoc/>
    public override FrameBone GetAdditiveDelta(int boneIndex, FrameBone bone) => bone;

    /// <inheritdoc/>
    public override bool HasMovementData() => false;

    /// <inheritdoc/>
    public override AnimationMovement.MovementData GetMovementOffsetData(float time) => new();

    /// <inheritdoc/>
    public override AnimationMovement.MovementData GetMovementOffsetData(int frame) => new();
}
