namespace ValveResourceFormat.Source1.Material;

/// <summary>What the conversion of a map needs to know about a material to size the sprites and ropes drawn with it.</summary>
/// <param name="TextureSize">The size in texels of the base texture.</param>
/// <param name="SpriteOrientation">The <c>$spriteorientation</c> of the material, or null when it sets none.</param>
/// <param name="SpriteOrigin">The <c>$spriteorigin</c> of the material.</param>
internal sealed record Source1MaterialInfo(Vector2 TextureSize, string? SpriteOrientation, Vector2 SpriteOrigin);
