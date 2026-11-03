using System.Linq;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Source1.Bsp;

namespace GUI.Types.GLViewers
{
    /// <summary>Where the camera of a Source 1 or GoldSrc map viewer starts.</summary>
    internal readonly record struct MapStartPoint(Vector3 Location, Vector3 Angles)
    {
        private const string PlayerSpawnPrefix = "info_player";

        /// <summary>
        /// Chooses a player spawn, preferring the class named, or a point above the world model when the map has none.
        /// </summary>
        /// <param name="entities">The entities of the map.</param>
        /// <param name="worldBounds">The bounds of the world model.</param>
        /// <param name="getAngles">Reads the angles of a spawn.</param>
        /// <param name="preferredClassName">The class of the spawn to choose when the map has one, or null.</param>
        public static MapStartPoint Find(IReadOnlyList<BspEntity> entities, AABB worldBounds, Func<BspEntity, Vector3> getAngles, string? preferredClassName = null)
        {
            var spawn = entities.FirstOrDefault(entity => entity.ClassName == preferredClassName)
                ?? entities.FirstOrDefault(static entity => entity.ClassName.StartsWith(PlayerSpawnPrefix, StringComparison.Ordinal));

            if (spawn != null)
            {
                return new MapStartPoint(spawn.Origin + new Vector3(0, 0, 64), getAngles(spawn));
            }

            return new MapStartPoint(worldBounds.Center + new Vector3(0, 0, worldBounds.Size.Z * 0.25f), new Vector3(15, 45, 0));
        }

        public void Apply(Camera camera)
        {
            camera.SetLocation(Location);
            camera.SetFromQAngle(Angles);
        }
    }
}
