using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using ValveResourceFormat.IO;
using ValveResourceFormat.Source1.Material;
using ValveResourceFormat.Source1.Model;
using ValveResourceFormat.Source1.Physics;
using S2Model = ValveResourceFormat.ResourceTypes.Model;

namespace ValveResourceFormat.Source1.Bsp;

/// <summary>
/// Converts the collision the compiler stored for each model of a map to Source 2 physics: the world's to the world
/// physics of the map, and each brush model's to a physics file its model references.
/// </summary>
internal static class Source1WorldPhysicsConverter
{
    private const int ContentsSolid = 0x1;
    private const int ContentsPlayerClip = 0x10000;
    private const int ContentsMonsterClip = 0x20000;

    // A displacement whose minimum tesselation has the top bit set stores flags in the rest of it
    private const uint DisplacementFlagsMagic = 0x80000000;
    private const int DisplacementNoPhysicsCollision = 0x2;
    private const int DisplacementNoHullCollision = 0x4;

    private const int ModelHeaderSize = 16;

    /// <summary>Adds the physics of every model of the map that has collision.</summary>
    /// <param name="map">The converted map, whose brush models are already added.</param>
    /// <param name="bsp">The map.</param>
    /// <param name="getMaterialInfo">Reads a material, for the surface property of displacements.</param>
    public static void Convert(Source1ConvertedMap map, BspFile bsp, Func<string, Source1MaterialInfo?> getMaterialInfo)
    {
        var lump = bsp.GetLumpData(LumpType.PhysCollide).ToArray();
        var offset = 0;

        while (offset + ModelHeaderSize <= lump.Length)
        {
            var header = lump.AsSpan(offset);
            var modelIndex = BinaryPrimitives.ReadInt32LittleEndian(header);
            var solidsLength = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            var keyValuesLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            var solidCount = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            offset += ModelHeaderSize;

            if (modelIndex < 0)
            {
                break;
            }

            var collision = new PhyFile(lump, offset, solidCount, solidsLength, keyValuesLength);
            offset += solidsLength + keyValuesLength;

            var sections = ParseKeyValues(collision.KeyValuesText);
            var builder = new PhysicsAggregateBuilder();
            AddSolids(builder, collision, sections);

            // Without virtual terrain the displacements were compiled into the solids of the world
            if (modelIndex == 0 && sections.Exists(static section => section.Name == "virtualterrain"))
            {
                AddDisplacements(builder, bsp, getMaterialInfo);
            }

            if (builder.IsEmpty)
            {
                continue;
            }

            if (modelIndex == 0)
            {
                Source1WorldConverter.AddFile(map, $"{map.MapPath}/world_physics.vphys", builder.BuildResource());
                continue;
            }

            AddBrushModelPhysics(map, modelIndex, builder);
        }
    }

    private static void AddBrushModelPhysics(Source1ConvertedMap map, int modelIndex, PhysicsAggregateBuilder builder)
    {
        var modelPath = map.GetBrushModelPath(modelIndex);

        if (!map.Files.TryGetValue(modelPath + GameFileLoader.CompiledFileSuffix, out var modelResource)
            || modelResource.DataBlock is not S2Model model)
        {
            return;
        }

        var physicsPath = Path.ChangeExtension(modelPath, ".vphys");
        Source1WorldConverter.AddFile(map, physicsPath, builder.BuildResource());
        model.Data["m_refPhysicsData"].Add(S2Model.CreateResourceReference(physicsPath));
    }

    private sealed record Section(string Name, Dictionary<string, string> Values);

    private static void AddSolids(PhysicsAggregateBuilder builder, PhyFile collision, List<Section> sections)
    {
        var materialNames = new Dictionary<int, string>();
        var solidSections = new Dictionary<int, Section>();

        foreach (var section in sections)
        {
            if (section.Name == "materialtable")
            {
                foreach (var (name, index) in section.Values)
                {
                    materialNames[int.Parse(index, CultureInfo.InvariantCulture)] = name;
                }

                continue;
            }

            if (section.Values.TryGetValue("index", out var solidIndex))
            {
                solidSections[int.Parse(solidIndex, CultureInfo.InvariantCulture)] = section;
            }
        }

        foreach (var solid in collision.Solids)
        {
            solidSections.TryGetValue(solid.Index, out var section);

            var collisionAttributes = GetCollisionAttributes(builder, section);
            var solidSurface = section?.Values.GetValueOrDefault("surfaceprop") ?? "default";
            var meshVertices = new List<Vector3>();
            var meshIndices = new List<int>();
            var meshSurfaces = new List<int>();

            for (var i = 0; i < solid.Convexes.Count; i++)
            {
                var surface = materialNames.GetValueOrDefault(solid.ConvexMaterials[i], solidSurface);
                var surfaceProperty = builder.GetSurfaceProperty(surface);
                var points = solid.Convexes[i];
                var triangles = solid.ConvexTriangles[i];

                // Triangle meshes, such as displacements, are compiled into one flat convex per triangle, which
                // stores the back of the triangle first and then its front
                if (points.Length == 3)
                {
                    meshIndices.Add(meshVertices.Count + triangles[3]);
                    meshIndices.Add(meshVertices.Count + triangles[4]);
                    meshIndices.Add(meshVertices.Count + triangles[5]);
                    meshVertices.AddRange(points);
                    meshSurfaces.Add(surfaceProperty);
                    continue;
                }

                builder.AddHull(points, triangles, collisionAttributes, surfaceProperty);
            }

            builder.AddMesh(meshVertices, meshIndices, meshSurfaces, collisionAttributes);
        }
    }

    // Clip brushes are compiled into solids of their own, which block players or NPCs only; water into fluids
    private static int GetCollisionAttributes(PhysicsAggregateBuilder builder, Section? section)
    {
        if (section?.Name == "fluid")
        {
            return builder.GetCollisionAttributes("conditionallysolid", ["water"]);
        }

        var contents = ContentsSolid;

        if (section != null && section.Values.TryGetValue("contents", out var contentsValue))
        {
            contents = int.Parse(contentsValue, CultureInfo.InvariantCulture);
        }

        if ((contents & ContentsSolid) != 0)
        {
            return builder.GetCollisionAttributes("default", []);
        }

        var layers = new List<string>();

        if ((contents & ContentsMonsterClip) != 0)
        {
            layers.Add("npcclip");
        }

        if ((contents & ContentsPlayerClip) != 0)
        {
            layers.Add("playerclip");
        }

        if (layers.Count == 0)
        {
            return builder.GetCollisionAttributes("default", []);
        }

        return builder.GetCollisionAttributes("conditionallysolid", layers);
    }

    private static void AddDisplacements(PhysicsAggregateBuilder builder, BspFile bsp, Func<string, Source1MaterialInfo?> getMaterialInfo)
    {
        var infos = bsp.DisplacementInfos;
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var surfaces = new List<int>();

        for (var index = 0; index < infos.Length; index++)
        {
            var flags = (uint)infos[index].MinTess;
            var hasFlags = (flags & DisplacementFlagsMagic) != 0;
            var noPhysics = hasFlags && (flags & DisplacementNoPhysicsCollision) != 0;
            var noHull = hasFlags && (flags & DisplacementNoHullCollision) != 0;

            // The physics stop both props and movement, so only a displacement that collides with neither is left out
            if (noPhysics && noHull)
            {
                continue;
            }

            var face = bsp.Faces[infos[index].MapFace];
            var textureData = bsp.TextureData[bsp.TextureInfos[face.TexInfo].TexData];
            var material = bsp.GetTextureName(textureData.NameStringTableId);
            var surfaceProperty = builder.GetSurfaceProperty(getMaterialInfo(material)?.SurfaceProperty ?? "default");

            var displacement = bsp.DisplacementManager[index];
            var size = displacement.Size;
            var first = vertices.Count;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    vertices.Add(displacement.GetPosition(x, y));
                }
            }

            for (var y = 0; y < size - 1; y++)
            {
                for (var x = 0; x < size - 1; x++)
                {
                    var i00 = first + x + (y * size);
                    var i10 = i00 + 1;
                    var i01 = i00 + size;
                    var i11 = i01 + 1;

                    if (((x + y) & 1) == 0)
                    {
                        indices.AddRange([i00, i10, i11, i00, i11, i01]);
                    }
                    else
                    {
                        indices.AddRange([i00, i10, i01, i10, i11, i01]);
                    }

                    surfaces.Add(surfaceProperty);
                    surfaces.Add(surfaceProperty);
                }
            }
        }

        builder.AddMesh(vertices, indices, surfaces, builder.GetCollisionAttributes("default", []));
    }

    // The key values are a list of named blocks of quoted key and value pairs, such as solid { "index" "0" }
    private static List<Section> ParseKeyValues(string text)
    {
        var sections = new List<Section>();
        var tokens = Tokenize(text);
        var position = 0;

        while (position + 1 < tokens.Count)
        {
            var name = tokens[position];
            position += 2;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (position + 1 < tokens.Count && tokens[position] != "}")
            {
                values[tokens[position]] = tokens[position + 1];
                position += 2;
            }

            position++;
            sections.Add(new Section(name, values));
        }

        return sections;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var position = 0;

        while (position < text.Length)
        {
            var character = text[position];

            if (char.IsWhiteSpace(character) || character == '\0')
            {
                position++;
                continue;
            }

            if (character is '{' or '}')
            {
                tokens.Add(character.ToString());
                position++;
                continue;
            }

            if (character == '"')
            {
                var end = text.IndexOf('"', position + 1);

                if (end < 0)
                {
                    end = text.Length;
                }

                tokens.Add(text[(position + 1)..end]);
                position = end + 1;
                continue;
            }

            var wordEnd = position;

            while (wordEnd < text.Length && !char.IsWhiteSpace(text[wordEnd]) && text[wordEnd] is not '{' and not '}' and not '"')
            {
                wordEnd++;
            }

            tokens.Add(text[position..wordEnd]);
            position = wordEnd;
        }

        return tokens;
    }
}
