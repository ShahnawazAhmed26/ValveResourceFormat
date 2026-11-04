using ValveResourceFormat.GoldSrc;
using ValveResourceFormat.Source1.Bsp;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>
    /// The lightmaps of all faces of a GoldSrc map packed into atlases, one atlas for each of the four light style
    /// slots of a face. Every face has the same place in all of them.
    /// </summary>
    internal sealed class GoldSrcLightmapAtlas
    {
        /// <summary>The number of light style slots a face can have.</summary>
        public const int SlotCount = 4;

        private const int MaxWidth = 1024;
        private const int Border = 1;
        private const int BytesPerLuxel = 3;

        // The position is the first luxel of the lightmap, inside its border
        private readonly record struct Placement(int Width, int Height, int X, int Y, int MinS, int MinT);

        private readonly Placement[] placements;

        /// <summary>Gets the width of the atlases in luxels.</summary>
        public int Width { get; }

        /// <summary>Gets the height of the atlases in luxels.</summary>
        public int Height { get; }

        /// <summary>Gets the RGBA pixels of each slot, or null for a slot no face uses.</summary>
        public byte[]?[] Slots { get; } = new byte[SlotCount][];

        public GoldSrcLightmapAtlas(GoldSrcBspFile bsp)
        {
            ArgumentNullException.ThrowIfNull(bsp);

            var faces = bsp.Faces;
            var lighting = bsp.Lighting;
            var textureInfos = bsp.TextureInfos;
            var extents = bsp.LightmapExtents;
            var candidates = new List<int>();

            placements = new Placement[faces.Length];

            for (var i = 0; i < faces.Length; i++)
            {
                var face = faces[i];

                if (!face.HasLightmap || face.TexInfo < 0 || face.TexInfo >= textureInfos.Length || textureInfos[face.TexInfo].IsSpecial)
                {
                    continue;
                }

                var extent = extents[i];
                var byteCount = (long)extent.Width * extent.Height * BytesPerLuxel * face.LightStyleCount;

                if (extent.Width <= 0 || extent.Height <= 0 || extent.Width > MaxWidth - (Border * 2) || face.LightOffset < 0 || face.LightOffset + byteCount > lighting.Length)
                {
                    continue;
                }

                candidates.Add(i);
            }

            candidates.Sort((a, b) => extents[b].Height.CompareTo(extents[a].Height));

            // The atlas is always the full width and as tall as the packed lightmaps
            var packer = new SkylinePacker(MaxWidth, int.MaxValue);
            var usedHeight = 0;

            foreach (var faceIndex in candidates)
            {
                var extent = extents[faceIndex];
                var paddedWidth = extent.Width + (Border * 2);
                var paddedHeight = extent.Height + (Border * 2);

                if (!packer.TryPack(paddedWidth, paddedHeight, out var x, out var y))
                {
                    continue;
                }

                placements[faceIndex] = new Placement(extent.Width, extent.Height, x + Border, y + Border, extent.MinS, extent.MinT);
                usedHeight = Math.Max(usedHeight, y + paddedHeight);
            }

            Width = MaxWidth;
            Height = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, usedHeight));

            foreach (var faceIndex in candidates)
            {
                if (HasLightmap(faceIndex))
                {
                    Copy(faces[faceIndex], placements[faceIndex], lighting);
                }
            }
        }

        /// <summary>Gets whether a face has a lightmap.</summary>
        public bool HasLightmap(int face) => placements[face].Width > 0;

        /// <summary>
        /// Gets the coordinates in the atlas of a point of a face, from its texture coordinates in texels.
        /// A luxel is 16 texels wide and its sample is at its center.
        /// </summary>
        public Vector2 GetCoordinates(int face, Vector2 textureCoordinates)
        {
            var placement = placements[face];

            var s = placement.X + ((textureCoordinates.X - (placement.MinS * 16f)) / 16f) + 0.5f;
            var t = placement.Y + ((textureCoordinates.Y - (placement.MinT * 16f)) / 16f) + 0.5f;

            return new Vector2(s / Width, t / Height);
        }

        private void Copy(in GoldSrcFace face, Placement placement, ReadOnlySpan<byte> lighting)
        {
            var luxelCount = placement.Width * placement.Height;

            for (var slot = 0; slot < face.LightStyleCount; slot++)
            {
                var pixels = Slots[slot] ??= new byte[Width * Height * 4];
                var source = lighting.Slice(face.LightOffset + (slot * luxelCount * BytesPerLuxel), luxelCount * BytesPerLuxel);

                for (var y = -Border; y < placement.Height + Border; y++)
                {
                    var sourceY = Math.Clamp(y, 0, placement.Height - 1);

                    for (var x = -Border; x < placement.Width + Border; x++)
                    {
                        var sourceX = Math.Clamp(x, 0, placement.Width - 1);
                        var luxel = ((sourceY * placement.Width) + sourceX) * BytesPerLuxel;
                        var target = (((placement.Y + y) * Width) + placement.X + x) * 4;

                        pixels[target] = source[luxel];
                        pixels[target + 1] = source[luxel + 1];
                        pixels[target + 2] = source[luxel + 2];
                        pixels[target + 3] = 255;
                    }
                }
            }
        }
    }
}
