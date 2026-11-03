using Microsoft.Extensions.Logging;
using ValveResourceFormat.IO.Fgd;
using ValveResourceFormat.Renderer.Editor.Entities;
using ValveResourceFormat.Renderer.Editor.Helpers;
using ValveResourceFormat.Source1;
using ValveResourceFormat.Source1.Model;

namespace ValveResourceFormat.Renderer.Source1;

/// <summary>
/// The model an entity of a Source 1 game is placed as, which its class names with <c>studio</c>. It stands in for
/// the entity in the editor, and is drawn in its reference pose.
/// </summary>
internal sealed class Source1ModelHelper : EntityHelper
{
    private const string ModelExtension = ".mdl";

    private readonly string path;
    private readonly Source1Model model;

    /// <inheritdoc/>
    public override bool IsStandIn => true;

    /// <inheritdoc/>
    public override bool HiddenInGameObjectsOnly => true;

    private Source1ModelHelper(IHelperOwner owner, HelperDeclaration declaration, string path, Source1Model model) : base(owner, declaration)
    {
        this.path = path;
        this.model = model;
    }

    /// <summary>Creates the model a class declares.</summary>
    /// <returns>
    /// The model, or <see langword="null"/> when the class names none or it does not load, so that the entity is
    /// drawn some other way.
    /// </returns>
    internal static Source1ModelHelper? Create(IHelperOwner owner, HelperDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(declaration);

        if (declaration.Arguments is not [{ Length: > 0 } name, ..])
        {
            return null;
        }

        var path = Source1Paths.Normalize(name);

        if (!path.EndsWith(ModelExtension, StringComparison.Ordinal))
        {
            path += ModelExtension;
        }

        var rendererContext = owner.Scene.RendererContext;

        if (rendererContext.LoadSource1Model(path) is not { } model)
        {
            rendererContext.Logger.LogDebug("Model \"{Model}\" of a '{Classname}' does not exist", path, owner.Classname);
            return null;
        }

        return new Source1ModelHelper(owner, declaration, path, model);
    }

    /// <inheritdoc/>
    public override void AddToScene()
    {
        var node = new Source1ModelSceneNode(Owner.Scene, model)
        {
            Name = path,
            Transform = Owner.Transform,
            Layers = [HammerEntityVisuals.MarkerLayer],
            Flags = ObjectTypeFlags.NoShadows,

            // A stand in is as large in a 3D sky as in the map of the sky
            KeepsMarkerSize = true,
        };

        Owner.AddNode(node, followsEntity: true);
    }
}
