using System.IO;
using ValveResourceFormat.IO.Fgd;
using ValveResourceFormat.Renderer.Editor.Helpers;
using ValveResourceFormat.Source1;

namespace ValveResourceFormat.Renderer.Source1;

/// <summary>
/// The icon an entity of a Source 1 game is placed as: the base texture of a material of the game, drawn
/// the way <see cref="IconHelper"/> draws the icons of a compiled map.
/// </summary>
internal sealed class Source1IconHelper : IconHelper
{
    private Source1IconHelper(IHelperOwner owner, HelperDeclaration declaration, string materialName)
        : base(owner, declaration, materialName, isDrawnOverScene: false)
    {
    }

    /// <summary>Creates the icon a class declares.</summary>
    /// <returns>The icon, or <see langword="null"/> when its material does not exist, so that the entity is drawn some other way.</returns>
    internal static IconHelper? CreateSource1(IHelperOwner owner, HelperDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(declaration);

        if (declaration.Arguments is not [{ Length: > 0 } image, ..])
        {
            return null;
        }

        var materialName = Source1Paths.ToMaterialPath(Path.ChangeExtension(image, null));

        return Check(owner, new Source1IconHelper(owner, declaration, materialName));
    }

    private protected override bool MaterialExists()
        => Owner.Scene.RendererContext.Source1FileSystem?.Exists(MaterialName) == true;

    private protected override RenderMaterial? LoadMaterial(Dictionary<string, byte> shaderArguments)
        => Owner.Scene.RendererContext.MaterialLoader.CreateSource1ToolSprite(MaterialName, shaderArguments);

    // The border is a material of the tools of a Source 2 game
    private protected override RenderMaterial? LoadBorderMaterial(Dictionary<string, byte> shaderArguments) => null;
}
