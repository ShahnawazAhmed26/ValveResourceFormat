using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ValveResourceFormat.Source1.Bsp;

internal sealed partial class BspFile
{
    private Vector3[]? vertexNormals;
    private ushort[]? vertexNormalIndices;
    private int[]? faceVertexNormalStarts;
    private ushort[]? displacementTriangleFlags;
    private BspLeafWaterData[]? leafWaterData;
    private ushort[]? leafMinDistToWater;
    private BspOcclusion? occlusion;
    private BspArea[]? areas;
    private BspAreaPortal[]? areaPortals;
    private Vector3[]? clipPortalVertices;
    private BspFace[]? originalFaces;
    private ushort[]? faceIds;
    private BspWorldLight[]? worldLights;
    private BspWorldLight[]? worldLightsHdr;
    private BspDispMultiblend[]? displacementMultiblend;
    private BspVisibility? visibility;

    /// <summary>The smoothed vertex normals that the vertex normal indices point to.</summary>
    public Vector3[] VertexNormals => vertexNormals ??= ReadLump<Vector3>(LumpType.VertNormals);

    /// <summary>For every face in lump order, one index into <see cref="VertexNormals"/> per edge of the face.</summary>
    public ushort[] VertexNormalIndices => vertexNormalIndices ??= ReadLump<ushort>(LumpType.VertNormalIndices);

    /// <summary>The per displacement triangle flags.</summary>
    public ushort[] DisplacementTriangleFlags => displacementTriangleFlags ??= ReadLump<ushort>(LumpType.DispTris);

    /// <summary>The map flags, such as which lighting kinds the map was compiled with.</summary>
    public uint MapFlags
    {
        get
        {
            var span = GetVersionedLumpData(LumpType.MapFlags, 0).Span;
            return span.Length >= sizeof(uint) ? BinaryPrimitives.ReadUInt32LittleEndian(span) : 0;
        }
    }

    public BspLeafWaterData[] LeafWaterData => leafWaterData ??= ReadLump<BspLeafWaterData>(LumpType.LeafWaterData);

    /// <summary>Per leaf, the smallest distance to water.</summary>
    public ushort[] LeafMinDistToWater => leafMinDistToWater ??= ReadLump<ushort>(LumpType.LeafMinDistToWater);

    public BspOcclusion Occlusion => occlusion ??= ReadOcclusion();

    public BspArea[] Areas => areas ??= ReadLump<BspArea>(LumpType.Areas);
    public BspAreaPortal[] AreaPortals => areaPortals ??= ReadLump<BspAreaPortal>(LumpType.AreaPortals);
    public Vector3[] ClipPortalVertices => clipPortalVertices ??= ReadLump<Vector3>(LumpType.ClipPortalVerts);

    /// <summary>The faces as the map compiler made them, before they were split.</summary>
    public BspFace[] OriginalFaces => originalFaces ??= ToArray<BspFace>(GetVersionedLumpData(LumpType.OriginalFaces, 0, 1).Span);

    /// <summary>The editor face id of every face.</summary>
    public ushort[] FaceIds => faceIds ??= ReadLump<ushort>(LumpType.FaceIds);

    /// <summary>The LDR world lights.</summary>
    public BspWorldLight[] WorldLights => worldLights ??= ReadWorldLights(LumpType.WorldLights);

    /// <summary>The HDR world lights.</summary>
    public BspWorldLight[] WorldLightsHdr => worldLightsHdr ??= ReadWorldLights(LumpType.WorldLightsHdr);

    /// <summary>The displacement multiblend records. Which displacement vertex each record belongs to is not known.</summary>
    public BspDispMultiblend[] DisplacementMultiblend => displacementMultiblend ??= ReadLump<BspDispMultiblend>(LumpType.DispMultiblend);

    public BspVisibility Visibility => visibility ??= new BspVisibility(GetVersionedLumpData(LumpType.Visibility, 0));

    /// <summary>Looks up the smoothed normal of a corner of a face.</summary>
    /// <param name="faceIndex">The index into <see cref="Faces"/>.</param>
    /// <param name="corner">The index of the edge of the face, in surf edge order.</param>
    /// <param name="normal">The normal of the vertex.</param>
    /// <returns>False when the map has no vertex normal for the corner.</returns>
    internal bool TryGetFaceVertexNormal(int faceIndex, int corner, out Vector3 normal)
    {
        normal = default;

        var starts = FaceVertexNormalStarts;

        if (faceIndex < 0 || faceIndex >= Faces.Length || corner < 0 || corner >= Faces[faceIndex].NumEdges)
        {
            return false;
        }

        var indexPosition = starts[faceIndex] + corner;

        if (indexPosition >= VertexNormalIndices.Length)
        {
            return false;
        }

        var normalIndex = VertexNormalIndices[indexPosition];

        if (normalIndex >= VertexNormals.Length)
        {
            return false;
        }

        normal = VertexNormals[normalIndex];
        return true;
    }

    // The vertex normal indices of the faces follow each other in face order
    private int[] FaceVertexNormalStarts => faceVertexNormalStarts ??= ReadFaceVertexNormalStarts();

    private int[] ReadFaceVertexNormalStarts()
    {
        var faceList = Faces;
        var starts = new int[faceList.Length];
        var position = 0;

        for (var i = 0; i < starts.Length; i++)
        {
            starts[i] = position;
            position += Math.Max((int)faceList[i].NumEdges, 0);
        }

        return starts;
    }

    private BspOcclusion ReadOcclusion()
    {
        var span = GetVersionedLumpData(LumpType.Occlusion, 2).Span;

        if (span.Length == 0)
        {
            return new BspOcclusion([], [], []);
        }

        var occluders = ReadCountedArray<BspOccluder>(ref span);
        var polys = ReadCountedArray<BspOccluderPoly>(ref span);
        var vertexIndices = ReadCountedArray<int>(ref span);
        return new BspOcclusion(occluders, polys, vertexIndices);
    }

    // Reads an int count followed by that many items and advances past them
    private static T[] ReadCountedArray<T>(ref ReadOnlySpan<byte> span) where T : unmanaged
    {
        if (span.Length < sizeof(int))
        {
            throw new InvalidDataException("Lump ends before an item count.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(span);
        var size = Unsafe.SizeOf<T>();

        if (count < 0 || (long)count * size > span.Length - sizeof(int))
        {
            throw new InvalidDataException("Lump ends before the end of its items.");
        }

        var items = MemoryMarshal.Cast<byte, T>(span.Slice(sizeof(int), count * size)).ToArray();
        span = span[(sizeof(int) + (count * size))..];
        return items;
    }

    private BspWorldLight[] ReadWorldLights(LumpType type)
    {
        var info = GetLumpInfo(type);
        var span = GetVersionedLumpData(type, 0, 1).Span;

        if (info.Version == 1)
        {
            return ToArray<BspWorldLight>(span);
        }

        var legacy = ToArray<BspWorldLightV0>(span);
        var lights = new BspWorldLight[legacy.Length];

        for (var i = 0; i < lights.Length; i++)
        {
            lights[i] = new BspWorldLight(legacy[i]);
        }

        return lights;
    }
}
