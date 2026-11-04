using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ValveResourceFormat.Compression;
using ValveResourceFormat.Source1.FileSystem;

namespace ValveResourceFormat.Source1.Bsp;

internal sealed class BspFile
{
    public const int HeaderLumpCount = 64;

    private const int MinVersion = 19;
    private const int MaxVersion = 21;
    private const int Identifier = 0x50534256;
    private const int LzmaIdentifier = 0x414D5A4C;
    private const int LzmaHeaderSize = 17;
    private const int HeaderSize = 8 + (HeaderLumpCount * 16) + 4;
    private const int LumpFileHeaderSize = 20;
    private const int MaxLumpFiles = 128;

    private readonly byte[] data;
    private readonly LumpInfo[] lumps;
    private readonly Dictionary<LumpType, byte[]> decompressedLumps = [];
    private readonly Dictionary<LumpType, byte[]> lumpFileData = [];

    private BspModel[]? models;
    private BspPlane[]? planes;
    private Vector3[]? vertices;
    private BspEdge[]? edges;
    private int[]? surfEdges;
    private BspFace[]? faces;
    private BspFace[]? facesHdr;
    private BspPrimitive[]? primitives;
    private ushort[]? primitiveIndices;
    private Vector3[]? primitiveVertices;
    private BspTextureInfo[]? textureInfos;
    private BspTextureData[]? textureData;
    private BspDispInfo[]? displacementInfos;
    private BspDispVert[]? displacementVerts;
    private CubemapSample[]? cubemaps;
    private LeafAmbientIndex[]? leafAmbientIndices;
    private LeafAmbientIndex[]? leafAmbientIndicesHdr;
    private LeafAmbientLighting[]? leafAmbientLighting;
    private LeafAmbientLighting[]? leafAmbientLightingHdr;
    private string[]? textureNames;
    private List<BspEntity>? entities;
    private BspGameLump? gameLump;
    private BspStaticProps? staticProps;
    private DisplacementManager? displacementManager;
    private LightmapLayout? hdrLightmapLayout;
    private LightmapLayout? ldrLightmapLayout;
    private BspLeaf[]? leafs;
    private BspOverlayFade[]? overlayFades;
    private BspOverlay[]? overlays;
    private BspDetailProps? detailProps;

    public string Name { get; }
    public int Version { get; }
    public IReadOnlyList<LumpInfo> Lumps => lumps;

    public BspFile(byte[] data, string name)
    {
        this.data = data;
        Name = name;

        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException("File is too small to be a BSP.");
        }

        var span = data.AsSpan();

        var identifier = BinaryPrimitives.ReadInt32LittleEndian(span);

        if (identifier != Identifier)
        {
            throw new UnexpectedMagicException("Not a VBSP file", identifier, nameof(identifier));
        }

        Version = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);

        if (Version < MinVersion || Version > MaxVersion)
        {
            throw new UnexpectedMagicException($"Unsupported BSP version, supported versions are {MinVersion} to {MaxVersion}", Version, nameof(Version));
        }

        lumps = ReadLumpInfos(span, l4d2Layout: false);

        if (!AreLumpsInsideFile(lumps, data.Length))
        {
            var l4d2Lumps = ReadLumpInfos(span, l4d2Layout: true);

            if (!AreLumpsInsideFile(l4d2Lumps, data.Length))
            {
                throw new InvalidDataException("Lump table is invalid for both header layouts.");
            }

            lumps = l4d2Lumps;
        }
    }

    public static BspFile Open(string path)
    {
        return new BspFile(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
    }

    public static BspFile Open(Stream stream, string name)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new BspFile(memory.ToArray(), name);
    }

    private static LumpInfo[] ReadLumpInfos(ReadOnlySpan<byte> span, bool l4d2Layout)
    {
        var result = new LumpInfo[HeaderLumpCount];

        for (var i = 0; i < HeaderLumpCount; i++)
        {
            var entry = span.Slice(8 + (i * 16), 16);
            var a = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var b = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
            var c = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            var d = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);

            result[i] = l4d2Layout
                ? new LumpInfo { Type = (LumpType)i, Version = a, Offset = b, Length = c, FourCC = d }
                : new LumpInfo { Type = (LumpType)i, Offset = a, Length = b, Version = c, FourCC = d };
        }

        return result;
    }

    private static bool AreLumpsInsideFile(LumpInfo[] infos, int fileLength)
    {
        foreach (var info in infos)
        {
            if (info.Length == 0)
            {
                continue;
            }

            if (info.Offset < HeaderSize || info.Length < 0 || (long)info.Offset + info.Length > fileLength)
            {
                return false;
            }
        }

        return true;
    }

    public LumpInfo GetLumpInfo(LumpType type) => lumps[(int)type];

    /// <summary>
    /// Replaces lumps with the external lump files <c>maps/name_l_N.lmp</c> of the map, read for N from 0 until one is missing.
    /// A later file replaces an earlier one that names the same lump.
    /// </summary>
    /// <param name="fileSystem">The file system the lump files are opened from.</param>
    internal void ApplyLumpFiles(Source1FileSystem fileSystem)
    {
        for (var index = 0; index < MaxLumpFiles; index++)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"maps/{Name}_l_{index}.lmp");
            using var stream = fileSystem.OpenFile(path);

            if (stream == null)
            {
                return;
            }

            ApplyLumpFile(Source1Streams.ReadAll(stream));
        }
    }

    private void ApplyLumpFile(byte[] file)
    {
        if (file.Length < LumpFileHeaderSize)
        {
            throw new InvalidDataException("Lump file is too small.");
        }

        var lumpOffset = BinaryPrimitives.ReadInt32LittleEndian(file);
        var lumpIndex = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4));
        var version = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8));
        var length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));

        if (lumpIndex < 0 || lumpIndex >= HeaderLumpCount)
        {
            return;
        }

        if (lumpOffset < 0 || length < 0 || lumpOffset > file.Length)
        {
            throw new InvalidDataException("Lump file data lies outside the file.");
        }

        // The game reads as much of the lump as the file has
        length = Math.Min(length, file.Length - lumpOffset);

        var type = (LumpType)lumpIndex;

        // Game lump items address the BSP file itself
        if (type == LumpType.GameLump)
        {
            return;
        }

        lumps[lumpIndex] = new LumpInfo { Type = type, Offset = lumpOffset, Length = length, Version = version };
        lumpFileData[type] = file[lumpOffset..(lumpOffset + length)];
        decompressedLumps.Remove(type);
    }

    public ReadOnlyMemory<byte> GetLumpData(LumpType type)
    {
        var info = lumps[(int)type];

        if (info.Length == 0)
        {
            return default;
        }

        // Lump files are never compressed
        if (lumpFileData.TryGetValue(type, out var fileData))
        {
            return fileData;
        }

        var raw = data.AsMemory(info.Offset, info.Length);

        if (!IsLzmaCompressed(raw.Span))
        {
            return raw;
        }

        if (!decompressedLumps.TryGetValue(type, out var decompressed))
        {
            decompressed = DecompressLzma(raw.Span, info.FourCC);
            decompressedLumps[type] = decompressed;
        }

        return decompressed;
    }

    internal ReadOnlyMemory<byte> GetFileData(long offset, int length)
    {
        return data.AsMemory((int)offset, length);
    }

    internal int FileLength => data.Length;

    internal static bool IsLzmaCompressed(ReadOnlySpan<byte> span)
    {
        return span.Length >= LzmaHeaderSize && BinaryPrimitives.ReadInt32LittleEndian(span) == LzmaIdentifier;
    }

    internal static byte[] DecompressLzma(ReadOnlySpan<byte> span, int expectedSize)
    {
        var actualSize = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        var lzmaSize = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);

        if (actualSize < 0 || lzmaSize < 0 || actualSize != expectedSize)
        {
            throw new InvalidDataException("Compressed data has an invalid size.");
        }

        var properties = span.Slice(12, LzmaDecoder.PropertiesSize);
        var input = span.Slice(LzmaHeaderSize, Math.Min(lzmaSize, span.Length - LzmaHeaderSize));

        var output = new byte[actualSize];
        LzmaDecoder.Decode(properties, input, output);
        return output;
    }

    /// <summary>Gets the data of a lump after checking that its version is one of <paramref name="minVersion"/> to <paramref name="maxVersion"/>.</summary>
    internal ReadOnlyMemory<byte> GetVersionedLumpData(LumpType type, int minVersion, int maxVersion)
    {
        var info = lumps[(int)type];

        if (info.Length > 0 && (info.Version < minVersion || info.Version > maxVersion))
        {
            var supported = minVersion == maxVersion ? $"version is {minVersion}" : $"versions are {minVersion} to {maxVersion}";
            throw new UnexpectedMagicException($"Unsupported version of the {type} lump, supported {supported}", info.Version, nameof(info.Version));
        }

        return GetLumpData(type);
    }

    internal ReadOnlyMemory<byte> GetVersionedLumpData(LumpType type, int version) => GetVersionedLumpData(type, version, version);

    private T[] ReadLump<T>(LumpType type, int version = 0) where T : unmanaged
        => ToArray<T>(GetVersionedLumpData(type, version).Span);

    private static T[] ToArray<T>(ReadOnlySpan<byte> span) where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var count = span.Length / size;
        return MemoryMarshal.Cast<byte, T>(span[..(count * size)]).ToArray();
    }

    public BspModel[] Models => models ??= ReadLump<BspModel>(LumpType.Models);
    public BspPlane[] Planes => planes ??= ReadLump<BspPlane>(LumpType.Planes);
    public Vector3[] Vertices => vertices ??= ReadLump<Vector3>(LumpType.Vertexes);
    public BspEdge[] Edges => edges ??= ReadLump<BspEdge>(LumpType.Edges);
    public int[] SurfEdges => surfEdges ??= ReadLump<int>(LumpType.SurfEdges);
    public BspFace[] Faces => faces ??= ReadLump<BspFace>(LumpType.Faces, version: 1);
    public BspFace[] FacesHdr => facesHdr ??= ReadLump<BspFace>(LumpType.FacesHdr, version: 1);
    public BspPrimitive[] Primitives => primitives ??= ReadLump<BspPrimitive>(LumpType.Primitives);
    public ushort[] PrimitiveIndices => primitiveIndices ??= ReadLump<ushort>(LumpType.PrimIndices);
    public Vector3[] PrimitiveVertices => primitiveVertices ??= ReadLump<Vector3>(LumpType.PrimVerts);
    public BspTextureInfo[] TextureInfos => textureInfos ??= ReadLump<BspTextureInfo>(LumpType.TexInfo);
    public BspTextureData[] TextureData => textureData ??= ReadLump<BspTextureData>(LumpType.TexData);
    public BspDispInfo[] DisplacementInfos => displacementInfos ??= ReadLump<BspDispInfo>(LumpType.DispInfo);
    public BspDispVert[] DisplacementVerts => displacementVerts ??= ReadLump<BspDispVert>(LumpType.DispVerts);
    public CubemapSample[] Cubemaps => cubemaps ??= ReadLump<CubemapSample>(LumpType.Cubemaps);
    public LeafAmbientIndex[] LeafAmbientIndices => leafAmbientIndices ??= ReadLeafAmbientIndices(LumpType.LeafAmbientIndex, LumpType.LeafAmbientLighting);
    public LeafAmbientIndex[] LeafAmbientIndicesHdr => leafAmbientIndicesHdr ??= ReadLeafAmbientIndices(LumpType.LeafAmbientIndexHdr, LumpType.LeafAmbientLightingHdr);
    public LeafAmbientLighting[] LeafAmbientLighting => leafAmbientLighting ??= ReadLeafAmbientLighting(LumpType.LeafAmbientIndex, LumpType.LeafAmbientLighting);
    public LeafAmbientLighting[] LeafAmbientLightingHdr => leafAmbientLightingHdr ??= ReadLeafAmbientLighting(LumpType.LeafAmbientIndexHdr, LumpType.LeafAmbientLightingHdr);

    // Maps from before the ambient index existed store one light cube per leaf instead of samples
    private bool HasLeafAmbientCubes(LumpType indexType, LumpType lightingType)
        => GetLumpData(indexType).Length == 0
        && GetLumpData(lightingType).Length == Leafs.Length * Unsafe.SizeOf<CompressedLightCube>();

    private LeafAmbientIndex[] ReadLeafAmbientIndices(LumpType indexType, LumpType lightingType)
    {
        if (!HasLeafAmbientCubes(indexType, lightingType))
        {
            return ReadLump<LeafAmbientIndex>(indexType);
        }

        var indices = new LeafAmbientIndex[Leafs.Length];

        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = new LeafAmbientIndex(1, (ushort)i);
        }

        return indices;
    }

    private LeafAmbientLighting[] ReadLeafAmbientLighting(LumpType indexType, LumpType lightingType)
    {
        if (!HasLeafAmbientCubes(indexType, lightingType))
        {
            return ReadLump<LeafAmbientLighting>(lightingType, version: 1);
        }

        var cubes = ToArray<CompressedLightCube>(GetLumpData(lightingType).Span);
        var samples = new LeafAmbientLighting[cubes.Length];

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = new LeafAmbientLighting(cubes[i]);
        }

        return samples;
    }

    public ReadOnlyMemory<byte> Lighting => GetVersionedLumpData(LumpType.Lighting, 1);
    public ReadOnlyMemory<byte> LightingHdr => GetVersionedLumpData(LumpType.LightingHdr, 1);

    public BspOverlayFade[] OverlayFades => overlayFades ??= ReadLump<BspOverlayFade>(LumpType.OverlayFades);

    /// <summary>Whether the map carries an HDR lightmap lump.</summary>
    public bool HasHdrLighting => LightingHdr.Length > 0;

    // Maps without an HDR face lump store the HDR lightmap offsets in the regular faces
    private BspFace[] HdrFaces => FacesHdr.Length > 0 ? FacesHdr : Faces;

    /// <summary>The faces that match the preferred lightmap lump (HDR when present).</summary>
    public BspFace[] LightmappedFaces => HasHdrLighting ? HdrFaces : Faces;

    /// <summary>The preferred lightmap sample lump (HDR when present).</summary>
    public ReadOnlyMemory<byte> LightmapSamples => HasHdrLighting ? LightingHdr : Lighting;

    // The requested lightmap kind, or the other one when the map has no lighting of the requested kind
    private bool UseHdrLighting(bool preferHdr) => preferHdr ? HasHdrLighting : Lighting.Length == 0 && HasHdrLighting;

    /// <summary>Gets the face lump that goes with the chosen lightmap lump. Falls back to the other kind when the requested one is empty.</summary>
    public BspFace[] GetLightmappedFaces(bool preferHdr) => UseHdrLighting(preferHdr) ? HdrFaces : Faces;

    /// <summary>Gets the lightmap sample lump of the chosen kind. Falls back to the other kind when the requested one is empty.</summary>
    public ReadOnlyMemory<byte> GetLightmapSamples(bool preferHdr) => UseHdrLighting(preferHdr) ? LightingHdr : Lighting;

    /// <summary>Gets the lightmap layout for the HDR or LDR lump. Falls back to the other kind when the requested one is empty.</summary>
    public LightmapLayout GetLightmapLayout(bool preferHdr)
    {
        var hdr = UseHdrLighting(preferHdr);
        return hdr
            ? hdrLightmapLayout ??= new LightmapLayout(this, hdr: true)
            : ldrLightmapLayout ??= new LightmapLayout(this, hdr: false);
    }

    public string[] TextureNames => textureNames ??= ReadTextureNames();

    /// <summary>The BSP leaves. Only the fields shared by all lump versions are read.</summary>
    public BspLeaf[] Leafs => leafs ??= BspLeaf.Read(GetLumpInfo(LumpType.Leafs).Version, GetVersionedLumpData(LumpType.Leafs, 0, 1).Span);

    /// <summary>The map overlays followed by the water overlays.</summary>
    public IReadOnlyList<BspOverlay> Overlays => overlays ??= BspOverlay.Read(this);

    /// <summary>The detail props of the map (the dprp game lump).</summary>
    public BspDetailProps DetailProps => detailProps ??= new BspDetailProps(this);

    public IReadOnlyList<BspEntity> Entities => entities ??= BspEntity.Parse(GetLumpData(LumpType.Entities).Span);

    public BspGameLump GameLump => gameLump ??= new BspGameLump(this);
    public BspStaticProps StaticProps => staticProps ??= new BspStaticProps(this);
    internal DisplacementManager DisplacementManager => displacementManager ??= new DisplacementManager(this);
    /// <summary>The lightmap layout of the preferred (HDR when present) lightmap lump.</summary>
    public LightmapLayout LightmapLayout => GetLightmapLayout(preferHdr: true);

    public Vector3 GetVertexFromSurfEdge(int surfEdgeIndex)
    {
        var surfEdge = SurfEdges[surfEdgeIndex];
        var edge = Edges[Math.Abs(surfEdge)];
        return Vertices[surfEdge >= 0 ? edge.A : edge.B];
    }

    public string GetTextureName(int texDataStringIndex)
    {
        return TextureNames[texDataStringIndex];
    }

    private string[] ReadTextureNames()
    {
        var table = ReadLump<int>(LumpType.TexDataStringTable);
        var stringData = GetLumpData(LumpType.TexDataStringData).Span;
        var names = new string[table.Length];

        for (var i = 0; i < table.Length; i++)
        {
            var start = table[i];

            if (start < 0 || start >= stringData.Length)
            {
                names[i] = string.Empty;
                continue;
            }

            var remaining = stringData[start..];
            var length = remaining.IndexOf((byte)0);

            if (length < 0)
            {
                length = remaining.Length;
            }

            names[i] = Source1Paths.DecodeName(remaining[..length]);
        }

        return names;
    }
}
