using System.Linq;
using ValveResourceFormat.IO.Fgd;
using ValveResourceFormat.Source1.Bsp;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>
    /// Reads the orientation of a GoldSrc entity as the game gives it to the renderer.
    /// </summary>
    internal static class GoldSrcEntityAngles
    {
        private const string AnglesDirectionHelper = "angles_direction";

        /// <summary>
        /// Gets the (pitch, yaw, roll) of an entity in degrees, pitch positive downwards. The lone angle key of the
        /// editor is a yaw, and -1 and -2 name straight up and straight down.
        /// </summary>
        public static Vector3 Get(BspEntity entity)
        {
            if (entity.Properties.ContainsKey("angles"))
            {
                return entity.Angles;
            }

            if (!entity.Properties.ContainsKey("angle"))
            {
                return Vector3.Zero;
            }

            var angle = entity.GetFloat("angle");

            if (angle < 0f)
            {
                return new Vector3(MathF.Truncate(angle) == -1f ? -90f : 90f, 0f, 0f);
            }

            return new Vector3(0f, angle, 0f);
        }

        /// <summary>
        /// Gets whether the game reads the angles of an entity of a class as the direction it moves in and
        /// then clears them, which leaves nothing to rotate the entity by.
        /// </summary>
        public static bool IsDirection(EntityClass? entityClass)
            => entityClass?.Helpers.Any(static helper => helper.Name.Equals(AnglesDirectionHelper, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
