namespace ValveResourceFormat.Blocks;

/// <summary>
/// Opaque resource version/update data.
/// </summary>
public sealed class ResourceVersionUpdate : RawBinary
{
    /// <inheritdoc/>
    public override BlockType Type => BlockType.RVUP;
}
