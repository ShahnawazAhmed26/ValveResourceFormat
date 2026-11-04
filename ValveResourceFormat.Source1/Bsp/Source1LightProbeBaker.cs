using System.Globalization;
using System.Runtime.InteropServices;
using ValveResourceFormat.ResourceTypes;
using VrfTexture = ValveResourceFormat.ResourceTypes.Texture;

namespace ValveResourceFormat.Source1.Bsp;

/// <summary>
/// Turns the ambient cubes the map compiler stored in the leaves of a Source 1 map into <c>env_light_probe_volume</c>
/// entities with a probe grid texture each, which is what Source 2 maps light models with.
/// </summary>
internal static class Source1LightProbeBaker
{
    // The probe grid in a volume has this many cells at most along each axis
    private const int TileCells = 32;

    // Cells around a tile that are filled in the same way as the tile, so the filling is the same on both sides of a tile edge
    private const int Margin = 4;

    private const int FillPasses = Margin;

    // The scene lights at most this many volumes
    private const int MaxVolumes = 256;

    private const long MaxTextureBytes = 32L * 1024 * 1024;

    private const int BytesPerCell = BspLeafAmbient.FaceCount * 4 * 2;

    private static readonly float[] CellSizes = [16f, 32f, 64f, 128f, 256f, 512f];

    // The texture holds the faces in the order +X, +Y, +Z, -X, -Y, -Z, the leaf samples hold +X, -X, +Y, -Y, +Z, -Z
    private static readonly int[] LeafFaceOfSlice = [0, 2, 4, 1, 3, 5];

    private readonly record struct Grid(Vector3 Origin, float CellSize, int CountX, int CountY, int CountZ);

    private readonly record struct Tile(int X, int Y, int Z, int SizeX, int SizeY, int SizeZ);

    /// <summary>Adds the probe volumes of a map and their textures to a converted map.</summary>
    /// <param name="map">The converted map, which receives the textures and the entities of the volumes outside the 3D sky area.</param>
    /// <param name="bsp">The map.</param>
    /// <param name="lump">The entity lump the entities belong to.</param>
    /// <param name="locator">Finds the 3D sky area, or null when the map has none.</param>
    /// <param name="skyVolumes">Receives the entities of the volumes inside the 3D sky area, which light only what is drawn there.</param>
    public static void Add(Source1ConvertedMap map, BspFile bsp, EntityLump lump, IBrushLightingLocator? locator, List<EntityLump.Entity> skyVolumes)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(lump);
        ArgumentNullException.ThrowIfNull(skyVolumes);

        if (bsp.Models.Length == 0)
        {
            return;
        }

        var leaves = new BspLeafAmbient(bsp);

        if (locator == null)
        {
            AddRegion(map, leaves, lump, bsp.Models[0], static _ => true, "probe", map.Entities);
            return;
        }

        AddRegion(map, leaves, lump, bsp.Models[0], position => !locator.IsInSky(position), "probe", map.Entities);
        AddRegion(map, leaves, lump, bsp.Models[0], locator.IsInSky, "skyprobe", skyVolumes);
    }

    // Bakes the cells that contain() accepts, so that the volumes of one area never carry the light of another
    private static void AddRegion(
        Source1ConvertedMap map,
        BspLeafAmbient leaves,
        EntityLump lump,
        BspModel world,
        Func<Vector3, bool> contains,
        string name,
        List<EntityLump.Entity> volumes)
    {
        foreach (var cellSize in CellSizes)
        {
            var grid = CreateGrid(world.Min, world.Max, cellSize);
            var tiles = FindTiles(leaves, grid, contains);

            if (tiles == null)
            {
                continue;
            }

            for (var i = 0; i < tiles.Count; i++)
            {
                volumes.Add(AddVolume(map, leaves, lump, grid, tiles[i], contains, name, i));
            }

            return;
        }
    }

    private static Grid CreateGrid(Vector3 min, Vector3 max, float cellSize)
    {
        var size = max - min;

        return new Grid(
            min,
            cellSize,
            (int)MathF.Ceiling(size.X / cellSize),
            (int)MathF.Ceiling(size.Y / cellSize),
            (int)MathF.Ceiling(size.Z / cellSize));
    }

    private static Vector3 GetCellCenter(Grid grid, int x, int y, int z)
        => grid.Origin + (new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * grid.CellSize);

    // The tiles of the grid that have a cell in a leaf with ambient samples, or null when they exceed the volume or texture limits
    private static List<Tile>? FindTiles(BspLeafAmbient leaves, Grid grid, Func<Vector3, bool> contains)
    {
        var tiles = new List<Tile>();
        var bytes = 0L;

        for (var z = 0; z < grid.CountZ; z += TileCells)
        {
            for (var y = 0; y < grid.CountY; y += TileCells)
            {
                for (var x = 0; x < grid.CountX; x += TileCells)
                {
                    var tile = new Tile(
                        x,
                        y,
                        z,
                        Math.Min(TileCells, grid.CountX - x),
                        Math.Min(TileCells, grid.CountY - y),
                        Math.Min(TileCells, grid.CountZ - z));

                    if (!HasAmbient(leaves, grid, tile, contains))
                    {
                        continue;
                    }

                    tiles.Add(tile);
                    bytes += (long)tile.SizeX * tile.SizeY * tile.SizeZ * BytesPerCell;

                    if (tiles.Count > MaxVolumes || bytes > MaxTextureBytes)
                    {
                        return null;
                    }
                }
            }
        }

        return tiles;
    }

    private static bool HasAmbient(BspLeafAmbient leaves, Grid grid, Tile tile, Func<Vector3, bool> contains)
    {
        var firstCenter = GetCellCenter(grid, tile.X, tile.Y, tile.Z);
        var lastCenter = GetCellCenter(grid, tile.X + tile.SizeX - 1, tile.Y + tile.SizeY - 1, tile.Z + tile.SizeZ - 1);

        if (!leaves.MayHaveAmbient(firstCenter, lastCenter))
        {
            return false;
        }

        for (var z = 0; z < tile.SizeZ; z++)
        {
            for (var y = 0; y < tile.SizeY; y++)
            {
                for (var x = 0; x < tile.SizeX; x++)
                {
                    var center = GetCellCenter(grid, tile.X + x, tile.Y + y, tile.Z + z);

                    if (contains(center) && leaves.HasAmbient(leaves.FindLeaf(center)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static EntityLump.Entity AddVolume(
        Source1ConvertedMap map,
        BspLeafAmbient leaves,
        EntityLump lump,
        Grid grid,
        Tile tile,
        Func<Vector3, bool> contains,
        string name,
        int index)
    {
        var pixels = BakeTile(leaves, grid, tile, contains);
        var path = string.Create(CultureInfo.InvariantCulture, $"{map.MapPath}/lightprobes/{name}{index}.vtex");

        var texture = VrfTexture.Create(
            (ushort)tile.SizeX,
            (ushort)tile.SizeY,
            VTexFormat.RGBA16161616F,
            VTexFlags.VOLUME_TEXTURE,
            Vector4.One,
            [pixels],
            depth: (ushort)(tile.SizeZ * BspLeafAmbient.FaceCount));

        Source1WorldConverter.AddFile(map, path, texture);

        var mins = grid.Origin + (new Vector3(tile.X, tile.Y, tile.Z) * grid.CellSize);
        var size = new Vector3(tile.SizeX, tile.SizeY, tile.SizeZ) * grid.CellSize;
        var center = mins + (size * 0.5f);

        var entity = new EntityLump.Entity { ParentLump = lump };
        entity["classname"] = "env_light_probe_volume";
        entity["origin"] = FormatVector(center);
        entity["box_mins"] = FormatVector(-size * 0.5f);
        entity["box_maxs"] = FormatVector(size * 0.5f);
        entity["edge_fade_dists"] = FormatVector(Vector3.Zero);
        entity["voxel_size"] = grid.CellSize;
        entity["lightprobetexture"] = path;

        return entity;
    }

    private static string FormatVector(Vector3 value)
        => string.Create(CultureInfo.InvariantCulture, $"{value.X} {value.Y} {value.Z}");

    // The cells of the tile and the margin around it, each with the six faces of its ambient cube
    private sealed class CellBlock
    {
        public CellBlock(int sizeX, int sizeY, int sizeZ)
        {
            SizeX = sizeX;
            SizeY = sizeY;
            SizeZ = sizeZ;
            Cubes = new Vector3[sizeX * sizeY * sizeZ * BspLeafAmbient.FaceCount];
            Valid = new bool[sizeX * sizeY * sizeZ];
        }

        public int SizeX { get; }

        public int SizeY { get; }

        public int SizeZ { get; }

        public Vector3[] Cubes { get; }

        public bool[] Valid { get; }

        public int GetIndex(int x, int y, int z) => (((z * SizeY) + y) * SizeX) + x;
    }

    private static byte[] BakeTile(BspLeafAmbient leaves, Grid grid, Tile tile, Func<Vector3, bool> contains)
    {
        var block = new CellBlock(tile.SizeX + (2 * Margin), tile.SizeY + (2 * Margin), tile.SizeZ + (2 * Margin));

        for (var z = 0; z < block.SizeZ; z++)
        {
            for (var y = 0; y < block.SizeY; y++)
            {
                for (var x = 0; x < block.SizeX; x++)
                {
                    var center = GetCellCenter(grid, tile.X + x - Margin, tile.Y + y - Margin, tile.Z + z - Margin);
                    var leaf = leaves.FindLeaf(center);

                    if (!contains(center) || !leaves.HasAmbient(leaf))
                    {
                        continue;
                    }

                    if (leaves.BlendAmbientCube(leaf, center) is not { } cube)
                    {
                        continue;
                    }

                    var cell = block.GetIndex(x, y, z);
                    cube.CopyTo(block.Cubes, cell * BspLeafAmbient.FaceCount);
                    block.Valid[cell] = true;
                }
            }
        }

        for (var pass = 0; pass < FillPasses; pass++)
        {
            FillFromNeighbors(block);
        }

        return WriteTexels(block, tile);
    }

    // Gives every empty cell next to filled cells the average of them. Cells filled by this pass are only used by the next one.
    private static void FillFromNeighbors(CellBlock block)
    {
        var filled = new List<int>();

        for (var z = 0; z < block.SizeZ; z++)
        {
            for (var y = 0; y < block.SizeY; y++)
            {
                for (var x = 0; x < block.SizeX; x++)
                {
                    var cell = block.GetIndex(x, y, z);

                    if (block.Valid[cell])
                    {
                        continue;
                    }

                    if (AverageNeighbors(block, x, y, z, cell))
                    {
                        filled.Add(cell);
                    }
                }
            }
        }

        foreach (var cell in filled)
        {
            block.Valid[cell] = true;
        }
    }

    private static bool AverageNeighbors(CellBlock block, int x, int y, int z, int cell)
    {
        var count = 0;
        Span<Vector3> sum = stackalloc Vector3[BspLeafAmbient.FaceCount];

        for (var direction = 0; direction < 6; direction++)
        {
            var step = direction % 2 == 0 ? 1 : -1;
            var axis = direction / 2;
            var nx = axis == 0 ? x + step : x;
            var ny = axis == 1 ? y + step : y;
            var nz = axis == 2 ? z + step : z;

            if ((uint)nx >= (uint)block.SizeX || (uint)ny >= (uint)block.SizeY || (uint)nz >= (uint)block.SizeZ)
            {
                continue;
            }

            var neighbor = block.GetIndex(nx, ny, nz);

            if (!block.Valid[neighbor])
            {
                continue;
            }

            for (var face = 0; face < BspLeafAmbient.FaceCount; face++)
            {
                sum[face] += block.Cubes[(neighbor * BspLeafAmbient.FaceCount) + face];
            }

            count++;
        }

        if (count == 0)
        {
            return false;
        }

        for (var face = 0; face < BspLeafAmbient.FaceCount; face++)
        {
            block.Cubes[(cell * BspLeafAmbient.FaceCount) + face] = sum[face] / count;
        }

        return true;
    }

    // The faces are one slice stack each, one after another, which is the layout the shaders read
    private static byte[] WriteTexels(CellBlock block, Tile tile)
    {
        var texels = new Half[tile.SizeX * tile.SizeY * tile.SizeZ * BspLeafAmbient.FaceCount * 4];
        var texel = 0;

        for (var slice = 0; slice < BspLeafAmbient.FaceCount; slice++)
        {
            var face = LeafFaceOfSlice[slice];

            for (var z = 0; z < tile.SizeZ; z++)
            {
                for (var y = 0; y < tile.SizeY; y++)
                {
                    for (var x = 0; x < tile.SizeX; x++)
                    {
                        var cell = block.GetIndex(x + Margin, y + Margin, z + Margin);
                        var color = block.Cubes[(cell * BspLeafAmbient.FaceCount) + face];

                        texels[texel++] = (Half)color.X;
                        texels[texel++] = (Half)color.Y;
                        texels[texel++] = (Half)color.Z;
                        texels[texel++] = Half.One;
                    }
                }
            }
        }

        return MemoryMarshal.AsBytes(texels.AsSpan()).ToArray();
    }
}
