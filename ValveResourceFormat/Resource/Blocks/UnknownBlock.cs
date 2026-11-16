namespace ValveResourceFormat.Blocks;

/// <summary>
/// Represents a known-but-opaque resource block whose payload is not required by the viewer.
/// </summary>
public sealed class UnknownBlock(BlockType blockType) : RawBinary
{
    /// <inheritdoc/>
    public override BlockType Type => blockType;
}
