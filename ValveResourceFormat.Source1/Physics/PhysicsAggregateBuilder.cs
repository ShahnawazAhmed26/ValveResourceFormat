using System.Linq;
using System.Runtime.InteropServices;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Utils;
using static ValveResourceFormat.IO.KVHelpers;
using RnMesh = ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Mesh;

namespace ValveResourceFormat.Source1.Physics;

/// <summary>
/// Builds Rubikon physics, as a compiled Source 2 aggregate stores it, from convex point sets and triangle meshes,
/// each tagged with a collision group, the layers it interacts as, and a surface property.
/// </summary>
internal sealed class PhysicsAggregateBuilder
{
    // Hull vertices, half edges and faces are addressed by bytes
    private const int MaxHullElements = 255;

    private const int MaxTrianglesPerLeaf = 8;

    // Points closer than this to a plane lie on it, in inches
    private const float PlaneEpsilon = 0.02f;

    // Adjacent triangles whose normals are closer than this cosine, about half a degree, can share a face
    private const float CoplanarCosine = 0.99996f;

    private readonly KVObject collisionAttributes = KVObject.Array();
    private readonly Dictionary<string, int> collisionAttributeIndices = [];
    private readonly KVObject surfacePropertyHashes = KVObject.Array();
    private readonly Dictionary<string, int> surfacePropertyIndices = new(StringComparer.OrdinalIgnoreCase);
    private readonly KVObject hulls = KVObject.Array();
    private readonly KVObject meshes = KVObject.Array();
    private readonly HashSet<int> usedCollisionAttributes = [];

    /// <summary>Gets whether no shape was added.</summary>
    public bool IsEmpty => hulls.Count == 0 && meshes.Count == 0;

    /// <summary>Gets the number of convex point sets that did not make a closed hull and were left out.</summary>
    public int SkippedHullCount { get; private set; }

    /// <summary>Gets the index of the collision attributes of a group and its layers, adding them when new.</summary>
    /// <param name="collisionGroup">The collision group, such as <c>default</c> or <c>conditionallysolid</c>.</param>
    /// <param name="interactAs">The layers the shapes interact as.</param>
    public int GetCollisionAttributes(string collisionGroup, IReadOnlyList<string> interactAs)
    {
        var key = $"{collisionGroup}:{string.Join(',', interactAs)}";

        if (collisionAttributeIndices.TryGetValue(key, out var index))
        {
            return index;
        }

        var layers = KVObject.Array();

        foreach (var layer in interactAs)
        {
            layers.Add(layer);
        }

        var attributes = KVObject.Collection();
        attributes.Add("m_CollisionGroupString", collisionGroup);
        attributes.Add("m_InteractAsStrings", layers);
        attributes.Add("m_InteractWithStrings", KVObject.Array());
        attributes.Add("m_InteractExcludeStrings", KVObject.Array());

        index = collisionAttributes.Count;
        collisionAttributes.Add(attributes);
        collisionAttributeIndices.Add(key, index);

        return index;
    }

    /// <summary>Gets the index of a surface property, adding it when new.</summary>
    /// <param name="name">The name of the surface property.</param>
    public int GetSurfaceProperty(string name)
    {
        if (surfacePropertyIndices.TryGetValue(name, out var index))
        {
            return index;
        }

        index = surfacePropertyHashes.Count;
        surfacePropertyHashes.Add(StringToken.Store(name));
        surfacePropertyIndices.Add(name, index);

        return index;
    }

    /// <summary>Adds a convex polyhedron given as a closed triangle surface.</summary>
    /// <param name="points">The corners of the polyhedron.</param>
    /// <param name="triangles">Three indices into <paramref name="points"/> per triangle, whose planes are the face planes.</param>
    /// <param name="collisionAttributeIndex">The collision attributes, see <see cref="GetCollisionAttributes"/>.</param>
    /// <param name="surfacePropertyIndex">The surface property, see <see cref="GetSurfaceProperty"/>.</param>
    /// <returns>Whether the triangles made a closed hull, which is left out otherwise.</returns>
    public bool AddHull(IReadOnlyList<Vector3> points, IReadOnlyList<int> triangles, int collisionAttributeIndex, int surfacePropertyIndex)
    {
        var hull = CreateHull(points, triangles);

        if (hull == null)
        {
            SkippedHullCount++;
            return false;
        }

        hulls.Add(CreateDescriptor("m_Hull", hull, collisionAttributeIndex, surfacePropertyIndex));
        return true;
    }

    /// <summary>Adds a triangle mesh.</summary>
    /// <param name="vertices">The vertex positions.</param>
    /// <param name="indices">Three vertex indices per triangle.</param>
    /// <param name="surfaceProperties">The surface property of each triangle, see <see cref="GetSurfaceProperty"/>.</param>
    /// <param name="collisionAttributeIndex">The collision attributes, see <see cref="GetCollisionAttributes"/>.</param>
    public void AddMesh(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, IReadOnlyList<int> surfaceProperties, int collisionAttributeIndex)
    {
        if (indices.Count < 3)
        {
            return;
        }

        var mesh = CreateMesh(vertices, indices, surfaceProperties);
        meshes.Add(CreateDescriptor("m_Mesh", mesh, collisionAttributeIndex, surfaceProperties[0]));
    }

    /// <summary>Creates the aggregate of every shape added, as one static part.</summary>
    /// <param name="resource">The resource the aggregate is a block of.</param>
    public PhysAggregateData Build(Resource resource)
    {
        var attributeIndices = KVObject.Array();

        foreach (var index in usedCollisionAttributes.Order())
        {
            attributeIndices.Add(index);
        }

        var shape = KVObject.Collection();
        shape.Add("m_spheres", KVObject.Array());
        shape.Add("m_capsules", KVObject.Array());
        shape.Add("m_hulls", hulls);
        shape.Add("m_meshes", meshes);
        shape.Add("m_CollisionAttributeIndices", attributeIndices);

        var part = KVObject.Collection();
        part.Add("m_nFlags", 0);
        part.Add("m_flMass", 0f);
        part.Add("m_rnShape", shape);
        part.Add("m_nCollisionAttributeIndex", 0);

        var data = KVObject.Collection();
        data.Add("m_nFlags", 0);
        data.Add("m_bindPose", KVObject.Array());
        data.Add("m_parts", MakeArray(part));
        data.Add("m_joints", KVObject.Array());
        data.Add("m_boneNames", KVObject.Array());
        data.Add("m_boneParents", KVObject.Array());
        data.Add("m_surfacePropertyHashes", surfacePropertyHashes);
        data.Add("m_collisionAttributes", collisionAttributes);

        return new PhysAggregateData(BlockType.DATA) { Resource = resource, Data = data };
    }

    /// <summary>Creates a physics resource of every shape added.</summary>
    public Resource BuildResource()
    {
        var resource = new Resource(ResourceType.PhysicsCollisionMesh);
        resource.AddBlock(Build(resource));

        return resource;
    }

    private KVObject CreateDescriptor(string shapeName, KVObject shape, int collisionAttributeIndex, int surfacePropertyIndex)
    {
        usedCollisionAttributes.Add(collisionAttributeIndex);

        var descriptor = KVObject.Collection();
        descriptor.Add("m_nCollisionAttributeIndex", collisionAttributeIndex);
        descriptor.Add("m_nSurfacePropertyIndex", surfacePropertyIndex);
        descriptor.Add("m_UserFriendlyName", string.Empty);
        descriptor.Add(shapeName, shape);

        return descriptor;
    }

    /// <summary>
    /// Builds the hull of a closed convex triangle surface: adjacent triangles in one plane merge into a face, and the
    /// faces that share an edge are linked by twin half edges.
    /// </summary>
    internal static KVObject? CreateHull(IReadOnlyList<Vector3> points, IReadOnlyList<int> triangles)
    {
        if (points.Count < 4 || points.Count > MaxHullElements || triangles.Count < 12)
        {
            return null;
        }

        var corners = GetOutwardTriangles(points, triangles);
        var triangleFaces = MergeCoplanarTriangles(points, corners, out var faceCount);

        // Each face is bounded by the edges of its triangles that no other triangle of it shares, as a map from the
        // start of each edge to its end
        var triangleEdges = new Dictionary<(int From, int To), int>();

        for (var i = 0; i < corners.Length; i++)
        {
            var from = corners[i];
            var to = corners[i % 3 == 2 ? i - 2 : i + 1];

            if (!triangleEdges.TryAdd((from, to), triangleFaces[i / 3]))
            {
                return null;
            }
        }

        var boundaries = new Dictionary<int, int>[faceCount];

        for (var face = 0; face < faceCount; face++)
        {
            boundaries[face] = [];
        }

        foreach (var ((from, to), face) in triangleEdges)
        {
            if (!triangleEdges.TryGetValue((to, from), out var otherFace))
            {
                return null;
            }

            if (otherFace != face && !boundaries[face].TryAdd(from, to))
            {
                return null;
            }
        }

        // A point on fewer than three faces lies inside an edge and is not a corner
        var vertexFaceCounts = new int[points.Count];

        foreach (var boundary in boundaries)
        {
            foreach (var from in boundary.Keys)
            {
                vertexFaceCounts[from]++;
            }
        }

        var vertexIndices = new int[points.Count];
        var vertices = new List<Vector3>();

        for (var i = 0; i < points.Count; i++)
        {
            vertexIndices[i] = -1;

            if (vertexFaceCounts[i] >= 3)
            {
                vertexIndices[i] = vertices.Count;
                vertices.Add(points[i]);
            }
        }

        var loops = new List<List<int>>();

        foreach (var boundary in boundaries)
        {
            var loop = GetFaceLoop(boundary, vertexIndices);

            if (loop == null)
            {
                return null;
            }

            loops.Add(loop);
        }

        var planes = GetFacePlanes(points, corners, triangleFaces, faceCount, vertices, loops);
        var edges = CreateHalfEdges(loops, out var faceEdges);

        if (planes == null || edges == null || vertices.Count > MaxHullElements || edges.Count > MaxHullElements || loops.Count > MaxHullElements)
        {
            return null;
        }

        return CreateHullData(vertices, edges, faceEdges, planes, loops);
    }

    // The triangle corners, wound counterclockwise as seen from outside
    private static int[] GetOutwardTriangles(IReadOnlyList<Vector3> points, IReadOnlyList<int> triangles)
    {
        var corners = new int[triangles.Count - (triangles.Count % 3)];
        var center = Vector3.Zero;

        foreach (var point in points)
        {
            center += point;
        }

        center /= points.Count;

        var volume = 0f;

        for (var i = 0; i < corners.Length; i += 3)
        {
            var first = points[triangles[i]] - center;
            volume += Vector3.Dot(first, Vector3.Cross(points[triangles[i + 1]] - center, points[triangles[i + 2]] - center));
        }

        var isInward = volume < 0f;

        for (var i = 0; i < corners.Length; i += 3)
        {
            corners[i] = triangles[i];
            corners[i + 1] = isInward ? triangles[i + 2] : triangles[i + 1];
            corners[i + 2] = isInward ? triangles[i + 1] : triangles[i + 2];
        }

        return corners;
    }

    // Groups the triangles that share an edge and lie in one plane, returning the face of each triangle
    private static int[] MergeCoplanarTriangles(IReadOnlyList<Vector3> points, int[] corners, out int faceCount)
    {
        var triangleCount = corners.Length / 3;
        var normals = new Vector3[triangleCount];
        var edgeTriangles = new Dictionary<(int From, int To), int>();
        var groups = new int[triangleCount];

        for (var triangle = 0; triangle < triangleCount; triangle++)
        {
            normals[triangle] = Vector3.Normalize(GetTriangleCross(points, corners, triangle));
            groups[triangle] = triangle;

            for (var corner = 0; corner < 3; corner++)
            {
                var from = corners[(triangle * 3) + corner];
                var to = corners[(triangle * 3) + ((corner + 1) % 3)];
                edgeTriangles[(from, to)] = triangle;
            }
        }

        foreach (var ((from, to), triangle) in edgeTriangles)
        {
            if (!edgeTriangles.TryGetValue((to, from), out var neighbour))
            {
                continue;
            }

            var isParallel = Vector3.Dot(normals[triangle], normals[neighbour]) > CoplanarCosine;

            // The corner of the neighbour that is not on the shared edge must lie in the plane of the triangle
            var neighbourCorner = corners[neighbour * 3] + corners[(neighbour * 3) + 1] + corners[(neighbour * 3) + 2] - from - to;
            var distance = Vector3.Dot(normals[triangle], points[neighbourCorner] - points[from]);

            if (isParallel && MathF.Abs(distance) <= PlaneEpsilon)
            {
                groups[FindGroup(groups, triangle)] = FindGroup(groups, neighbour);
            }
        }

        var faceIndices = new Dictionary<int, int>();
        var triangleFaces = new int[triangleCount];

        for (var triangle = 0; triangle < triangleCount; triangle++)
        {
            var group = FindGroup(groups, triangle);

            if (!faceIndices.TryGetValue(group, out var face))
            {
                face = faceIndices.Count;
                faceIndices.Add(group, face);
            }

            triangleFaces[triangle] = face;
        }

        faceCount = faceIndices.Count;
        return triangleFaces;
    }

    private static int FindGroup(int[] groups, int triangle)
    {
        while (groups[triangle] != triangle)
        {
            groups[triangle] = groups[groups[triangle]];
            triangle = groups[triangle];
        }

        return triangle;
    }

    private static Vector3 GetTriangleCross(IReadOnlyList<Vector3> points, int[] corners, int triangle)
    {
        var first = points[corners[triangle * 3]];
        return Vector3.Cross(points[corners[(triangle * 3) + 1]] - first, points[corners[(triangle * 3) + 2]] - first);
    }

    // Walks the boundary of a face once around, keeping its corners, or returns null when it is not one loop
    private static List<int>? GetFaceLoop(Dictionary<int, int> boundary, int[] vertexIndices)
    {
        var loop = new List<int>();
        var start = -1;

        foreach (var from in boundary.Keys)
        {
            if (vertexIndices[from] >= 0)
            {
                start = from;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var current = start;
        var length = 0;

        do
        {
            if (length == boundary.Count || !boundary.TryGetValue(current, out var next))
            {
                return null;
            }

            if (vertexIndices[current] >= 0)
            {
                loop.Add(vertexIndices[current]);
            }

            current = next;
            length++;
        }
        while (current != start);

        // Every boundary edge must be on the loop, or the face has a hole or is split
        if (length != boundary.Count || loop.Count < 3)
        {
            return null;
        }

        return loop;
    }

    // The plane of each face, with the area weighted normal of its triangles, through the average of its corners
    private static List<Vector4>? GetFacePlanes(IReadOnlyList<Vector3> points, int[] corners, int[] triangleFaces, int faceCount,
        List<Vector3> vertices, List<List<int>> loops)
    {
        var normals = new Vector3[faceCount];

        for (var triangle = 0; triangle < triangleFaces.Length; triangle++)
        {
            normals[triangleFaces[triangle]] += GetTriangleCross(points, corners, triangle);
        }

        var planes = new List<Vector4>(faceCount);

        for (var face = 0; face < faceCount; face++)
        {
            if (normals[face].LengthSquared() < PlaneEpsilon * PlaneEpsilon)
            {
                return null;
            }

            var normal = Vector3.Normalize(normals[face]);
            var distance = 0f;

            foreach (var corner in loops[face])
            {
                distance += Vector3.Dot(normal, vertices[corner]);
            }

            planes.Add(new Vector4(normal, distance / loops[face].Count));
        }

        return planes;
    }

    // Half edges as Rubikon stores them, four bytes each: next, twin, origin and face, every edge followed by its twin
    private static List<byte[]>? CreateHalfEdges(List<List<int>> loops, out int[] faceEdges)
    {
        var edges = new List<byte[]>();
        var edgeByVertices = new Dictionary<(int From, int To), int>();
        var assigned = new List<bool>();
        faceEdges = new int[loops.Count];

        for (var face = 0; face < loops.Count; face++)
        {
            var loop = loops[face];
            var loopEdges = new int[loop.Count];

            for (var i = 0; i < loop.Count; i++)
            {
                var from = loop[i];
                var to = loop[(i + 1) % loop.Count];

                if (!edgeByVertices.TryGetValue((from, to), out var edge))
                {
                    edge = edges.Count;
                    edges.Add([0, (byte)(edge + 1), (byte)from, 0]);
                    edges.Add([0, (byte)edge, (byte)to, 0]);
                    assigned.Add(false);
                    assigned.Add(false);
                    edgeByVertices.Add((from, to), edge);
                    edgeByVertices.Add((to, from), edge + 1);
                }

                if (assigned[edge])
                {
                    return null;
                }

                assigned[edge] = true;
                edges[edge][3] = (byte)face;
                loopEdges[i] = edge;
            }

            for (var i = 0; i < loopEdges.Length; i++)
            {
                edges[loopEdges[i]][0] = (byte)loopEdges[(i + 1) % loopEdges.Length];
            }

            faceEdges[face] = loopEdges[0];
        }

        // Every half edge must have a face, or the hull is not closed
        foreach (var isAssigned in assigned)
        {
            if (!isAssigned)
            {
                return null;
            }
        }

        return edges;
    }

    private static KVObject CreateHullData(List<Vector3> vertices, List<byte[]> edges, int[] faceEdges, List<Vector4> planes, List<List<int>> loops)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var vertex in vertices)
        {
            min = Vector3.Min(min, vertex);
            max = Vector3.Max(max, vertex);
        }

        // Volume, centroid and area from a fan of tetrahedra over each face, apexed at a point inside
        var inside = (min + max) * 0.5f;
        var volume = 0f;
        var surfaceArea = 0f;
        var weightedCentroid = Vector3.Zero;

        foreach (var loop in loops)
        {
            var first = vertices[loop[0]];

            for (var i = 1; i < loop.Count - 1; i++)
            {
                var second = vertices[loop[i]];
                var third = vertices[loop[i + 1]];
                var cross = Vector3.Cross(second - first, third - first);
                var tetrahedronVolume = Vector3.Dot(first - inside, cross) / 6f;

                surfaceArea += cross.Length() * 0.5f;
                volume += tetrahedronVolume;
                weightedCentroid += tetrahedronVolume * (inside + first + second + third) / 4f;
            }
        }

        var centroid = volume > 0f ? weightedCentroid / volume : inside;
        var maxRadius = 0f;

        foreach (var vertex in vertices)
        {
            maxRadius = MathF.Max(maxRadius, Vector3.Distance(vertex, centroid));
        }

        var vertexIndices = new byte[vertices.Count];

        for (var i = 0; i < vertexIndices.Length; i++)
        {
            vertexIndices[i] = (byte)i;
        }

        var edgeBytes = new byte[edges.Count * 4];

        for (var i = 0; i < edges.Count; i++)
        {
            edges[i].CopyTo(edgeBytes, i * 4);
        }

        var faceBytes = new byte[faceEdges.Length];

        for (var i = 0; i < faceEdges.Length; i++)
        {
            faceBytes[i] = (byte)faceEdges[i];
        }

        var bounds = KVObject.Collection();
        bounds.Add("m_vMinBounds", ToKVArray(min));
        bounds.Add("m_vMaxBounds", ToKVArray(max));

        var hull = KVObject.Collection();
        hull.Add("m_vCentroid", ToKVArray(centroid));
        hull.Add("m_flMaxAngularRadius", maxRadius);
        hull.Add("m_vOrthographicAreas", ToKVArray(Vector3.Zero));
        hull.Add("m_flVolume", volume);
        hull.Add("m_flSurfaceArea", surfaceArea);
        hull.Add("m_nFlags", 0);
        hull.Add("m_Bounds", bounds);
        hull.Add("m_VertexPositions", KVObject.Blob(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(vertices)).ToArray()));
        hull.Add("m_Vertices", KVObject.Blob(vertexIndices));
        hull.Add("m_Edges", KVObject.Blob(edgeBytes));
        hull.Add("m_Faces", KVObject.Blob(faceBytes));
        hull.Add("m_Planes", KVObject.Blob(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(planes)).ToArray()));

        return hull;
    }

    private static KVObject CreateMesh(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, IReadOnlyList<int> surfaceProperties)
    {
        var triangleCount = indices.Count / 3;
        var order = new int[triangleCount];
        var centers = new Vector3[triangleCount];

        for (var i = 0; i < triangleCount; i++)
        {
            order[i] = i;
            centers[i] = (vertices[indices[i * 3]] + vertices[indices[(i * 3) + 1]] + vertices[indices[(i * 3) + 2]]) / 3f;
        }

        var nodes = new List<RnMesh.Node>();
        AddMeshNode(nodes, vertices, indices, centers, order, 0, triangleCount);

        // The tree addresses triangles in the order its leaves were built
        var triangles = new int[triangleCount * 3];
        var materials = KVObject.Array();
        var hasMixedMaterials = false;

        for (var i = 0; i < triangleCount; i++)
        {
            triangles[i * 3] = indices[order[i] * 3];
            triangles[(i * 3) + 1] = indices[(order[i] * 3) + 1];
            triangles[(i * 3) + 2] = indices[(order[i] * 3) + 2];
            materials.Add(surfaceProperties[order[i]]);
            hasMixedMaterials |= surfaceProperties[i] != surfaceProperties[0];
        }

        // A mesh of one surface property leaves it to its descriptor
        if (!hasMixedMaterials)
        {
            materials = KVObject.Array();
        }

        var vertexArray = vertices.ToArray();

        var mesh = KVObject.Collection();
        mesh.Add("m_vMin", ToKVArray(nodes[0].Min));
        mesh.Add("m_vMax", ToKVArray(nodes[0].Max));
        mesh.Add("m_Materials", materials);
        mesh.Add("m_vOrthographicAreas", ToKVArray(Vector3.Zero));
        mesh.Add("m_nFlags", 0);
        mesh.Add("m_nDebugFlags", 0);
        mesh.Add("m_Vertices", KVObject.Blob(MemoryMarshal.AsBytes(vertexArray.AsSpan()).ToArray()));
        mesh.Add("m_Triangles", KVObject.Blob(MemoryMarshal.AsBytes(triangles.AsSpan()).ToArray()));
        mesh.Add("m_Nodes", KVObject.Blob(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(nodes)).ToArray()));

        return mesh;
    }

    // Adds the subtree of triangles [start, start + count) depth first: a split node is followed by its left child,
    // and stores how far after it its right child is
    private static void AddMeshNode(List<RnMesh.Node> nodes, IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices,
        Vector3[] centers, int[] order, int start, int count)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (var i = start; i < start + count; i++)
        {
            for (var corner = 0; corner < 3; corner++)
            {
                var vertex = vertices[indices[(order[i] * 3) + corner]];
                min = Vector3.Min(min, vertex);
                max = Vector3.Max(max, vertex);
            }
        }

        var nodeIndex = nodes.Count;

        if (count <= MaxTrianglesPerLeaf)
        {
            nodes.Add(new RnMesh.Node(min, max, RnMesh.NodeType.Leaf, (uint)count, (uint)start));
            return;
        }

        var extent = max - min;
        var axis = RnMesh.NodeType.SplitX;

        if (extent.Y > extent.X && extent.Y >= extent.Z)
        {
            axis = RnMesh.NodeType.SplitY;
        }
        else if (extent.Z > extent.X && extent.Z > extent.Y)
        {
            axis = RnMesh.NodeType.SplitZ;
        }

        var axisIndex = (int)axis;
        Array.Sort(order, start, count, Comparer<int>.Create((a, b) => centers[a][axisIndex].CompareTo(centers[b][axisIndex])));

        nodes.Add(default);

        var leftCount = count / 2;
        AddMeshNode(nodes, vertices, indices, centers, order, start, leftCount);

        var rightIndex = nodes.Count;
        AddMeshNode(nodes, vertices, indices, centers, order, start + leftCount, count - leftCount);

        nodes[nodeIndex] = new RnMesh.Node(min, max, axis, (uint)(rightIndex - nodeIndex), 0);
    }
}
