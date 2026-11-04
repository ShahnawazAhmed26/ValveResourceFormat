using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using ValveResourceFormat.IO.Fgd;
using ValveResourceFormat.Renderer.Editor.Entities;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Utils;

namespace ValveResourceFormat.Renderer.Editor.Helpers;

/// <summary>
/// The icon an entity is placed as: a material drawn at the entity, facing the camera, with a border around
/// it while the entity is selected.
/// </summary>
public class IconHelper : EntityHelper
{
    /// <inheritdoc/>
    public override bool IsStandIn => true;

    /// <inheritdoc/>
    public override bool HiddenInGameObjectsOnly => true;

    private const float MinimumTintValue = 0.3f;

    private const string SelectionBorderMaterialName = "materials/editor/iconhelper_border.vmat";

    // A yellow frame two pixels wide around a selected icon, over the scene
    private static readonly SpriteBorder SelectionBorder = new(new Vector4(1f, 1f, 0f, 1f), 2f);

    private SpriteSceneNode? selectionBorder;

    /// <summary>Gets the material of the icon.</summary>
    private string MaterialName { get; }

    /// <summary>Gets the key of the entity that tints the icon, or <see langword="null"/> when none does.</summary>
    private string? TintKey { get; }

    /// <inheritdoc/>
    /// <remarks>Only where the icon is placed counts, not the size it is drawn at.</remarks>
    public override AABB? ObjectBounds => new(Owner.Transform.Translation, Owner.Transform.Translation);

    private IconHelper(IHelperOwner owner, HelperDeclaration declaration) : base(owner, declaration)
    {
        MaterialName = GetDeclaredMaterialName(declaration) ?? GetMaterialName(string.Empty);
        TintKey = GetMetadata("tintKey");
    }

    /// <summary>Creates the icon a class declares.</summary>
    /// <returns>The icon, or <see langword="null"/> when its material does not exist, so that the entity is drawn some other way.</returns>
    internal static IconHelper? Create(IHelperOwner owner, HelperDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var icon = new IconHelper(owner, declaration);

        if (!CompiledFileExists(owner, icon.MaterialName))
        {
            owner.Scene.RendererContext.Logger.LogDebug("Icon \"{Material}\" of class '{Classname}' does not exist", icon.MaterialName, owner.Classname);
            return null;
        }

        return icon;
    }

    private RenderMaterial? LoadMaterial(string name, Dictionary<string, byte> shaderArguments)
    {
        var rendererContext = Owner.Scene.RendererContext;

        return rendererContext.FileLoader.LoadFileCompiled(name) is { } resource
            ? rendererContext.MaterialLoader.LoadMaterial(resource, shaderArguments)
            : null;
    }

    /// <summary>Gets the material a class names in the declaration of an icon.</summary>
    /// <param name="declaration">The helper as a class declares it.</param>
    /// <returns>The name of the material, or <see langword="null"/> when the declaration names none.</returns>
    public static string? GetDeclaredMaterialName(HelperDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var image = declaration.Metadata is { } keys && keys.ContainsKey("image")
            ? keys.GetStringProperty("image")
            : declaration.Arguments is [var first, ..] ? first : null;

        return string.IsNullOrEmpty(image) ? null : GetMaterialName(image);
    }

    /// <summary>Gets the material an icon is declared with: in lower case, under materials, as a vmat.</summary>
    /// <param name="image">The icon as a class declares it.</param>
    /// <returns>The name of the material.</returns>
    private static string GetMaterialName(string image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var name = image.ToLowerInvariant();

        if (name.StartsWith("editor/", StringComparison.Ordinal))
        {
            name = "materials/" + name;
        }

        return Path.ChangeExtension(name, ".vmat").Replace('\\', '/');
    }

    /// <inheritdoc/>
    public override void AddToScene()
    {
        var scene = Owner.Scene;

        if (LoadMaterial(MaterialName, SpriteSceneNode.CreateShaderArguments(scene, null)) is not { } material)
        {
            return;
        }

        var tint = GetTint();

        if (CompiledFileExists(Owner, SelectionBorderMaterialName)
            && LoadMaterial(SelectionBorderMaterialName, SpriteSceneNode.CreateShaderArguments(scene, SelectionBorder)) is { } borderMaterial)
        {
            selectionBorder = CreateSprite(borderMaterial, SelectionBorderMaterialName, tint, SelectionBorder);
            selectionBorder.Visible = false;
            Owner.AddNode(selectionBorder, followsEntity: true);
        }

        Owner.AddNode(CreateSprite(material, MaterialName, tint), followsEntity: true);
    }

    private protected override void OnSelectionChanged(bool isSelected)
    {
        if (selectionBorder != null)
        {
            selectionBorder.Visible = isSelected;
        }
    }

    private SpriteSceneNode CreateSprite(RenderMaterial material, string name, Vector3 tint, SpriteBorder? border = null)
    {
        return new SpriteSceneNode(Owner.Scene, material, name, Owner.Transform.Translation, border)
        {
            Layers = [HammerEntityVisuals.MarkerLayer],
            Name = name,
            Flags = ObjectTypeFlags.NoShadows,
            KeepsMarkerSize = true,
            IsDrawnOverScene = border != null,
            TintAlpha = new Vector4(tint, 1f),
        };
    }

    // A dark tint is brightened to a value of at least 0.3, keeping its hue and saturation, so the icon still shows
    private Vector3 GetTint()
    {
        if (string.IsNullOrEmpty(TintKey) || !Owner.KeyValues.ContainsKey(TintKey))
        {
            return Vector3.One;
        }

        var tint = GetVector3Key(TintKey) / byte.MaxValue;
        var value = tint.MaxComponent();

        return value > 0f ? tint * (MathF.Max(MinimumTintValue, value) / value) : new Vector3(MinimumTintValue);
    }
}
