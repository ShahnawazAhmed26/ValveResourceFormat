using ValveResourceFormat.Renderer.Entities;
using ValveResourceFormat.ResourceTypes;
using Entity = ValveResourceFormat.ResourceTypes.EntityLump.Entity;

namespace ValveResourceFormat.Renderer.Source1.Entities;

/// <summary>
/// The tool visuals of a Source 1 map. The map viewer draws the models, brushes and sprites of its entities
/// itself, so an entity it drew is not given a stand-in.
/// </summary>
/// <param name="visuals">The visuals everything else is left to.</param>
/// <param name="drawnEntities">The entities the map viewer drew something for.</param>
internal sealed class Source1ToolVisuals(IEntityToolVisuals visuals, IReadOnlySet<Entity> drawnEntities) : IEntityToolVisuals
{
    public IEnumerable<string> GetResourcesToPreload(string classname) => visuals.GetResourcesToPreload(classname);

    public SceneNode? CreateStandIn(BaseEntity entity, ObjectTypeFlags flags)
    {
        if (entity.Data != null && drawnEntities.Contains(entity.Data))
        {
            return null;
        }

        return visuals.CreateStandIn(entity, flags);
    }

    public IEnumerable<SceneNode> CreateCollisionVisuals(Scene scene, PhysAggregateData physics, string name, BaseEntity? entity)
        => visuals.CreateCollisionVisuals(scene, physics, name, entity);

    public void AddEntityRelations(Scene scene, IReadOnlyList<BaseEntity> entities) => visuals.AddEntityRelations(scene, entities);
}
