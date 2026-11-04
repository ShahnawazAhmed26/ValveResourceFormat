namespace ValveResourceFormat.Source1.Bsp;

/// <summary>
/// Tells where the surfaces of the world model are seen from, which decides how its geometry is split into models.
/// </summary>
internal interface IBrushLightingLocator
{
    /// <summary>Gets whether a point at world coordinates is in the area of the 3D sky box.</summary>
    /// <param name="position">The point.</param>
    bool IsInSky(Vector3 position);
}
