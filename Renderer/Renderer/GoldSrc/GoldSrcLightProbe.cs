using ValveResourceFormat.GoldSrc;
using ValveResourceFormat.Source1.Bsp;

namespace ValveResourceFormat.Renderer.GoldSrc
{
    /// <summary>The light a studio model receives, in the units the studio shader takes.</summary>
    /// <param name="Ambient">The light that reaches every side of the model, from 0 to 1.</param>
    /// <param name="Shade">The light that only reaches the sides facing the light, from 0 to 1.</param>
    /// <param name="Color">The tint of both, with its brightest channel at 1.</param>
    /// <param name="Direction">The direction the light travels in.</param>
    internal readonly record struct GoldSrcStudioLight(float Ambient, float Shade, Vector3 Color, Vector3 Direction)
    {
        /// <summary>The light of a model that has no map around it.</summary>
        public static readonly GoldSrcStudioLight Default = new(0.3f, 0.6f, Vector3.One, new Vector3(0f, 0f, -1f));
    }

    /// <summary>
    /// Finds the light under a studio model from the lightmaps of a map: it traces down to the first lightmapped
    /// surface, and samples four points around it to tell which way the light comes from.
    /// </summary>
    internal sealed class GoldSrcLightProbe
    {
        private const int TraceLength = 2048;
        private const float StartHeight = 8f;
        private const float SampleSpread = 16f;
        private const int MaxAmbient = 128;
        private const int MaxLight = 255;

        // Models that ask for flat lighting get less of the gradient as direct light
        private const int FlatLightFlag = 0x100;
        private const float FlatDirectLight = 0.6f;
        private const float DirectLight = 0.9f;

        private const int InvertedLightEffect = 0x10;

        private readonly record struct SurfaceInfo(int MinS, int MinT, int ExtentS, int ExtentT, bool IsLit);

        private readonly record struct LightSample(int R, int G, int B)
        {
            public bool IsLight => R != 0 || G != 0 || B != 0;
        }

        private readonly GoldSrcBspFile bsp;
        private readonly GoldSrcLightStyles? lightStyles;
        private readonly SurfaceInfo[] surfaces;
        private readonly int rootNode;

        /// <summary>Creates a probe for a map.</summary>
        /// <param name="bsp">The map.</param>
        /// <param name="lightStyles">The light styles whose current brightness scales the lightmaps, or <see langword="null"/> for full brightness.</param>
        public GoldSrcLightProbe(GoldSrcBspFile bsp, GoldSrcLightStyles? lightStyles)
        {
            ArgumentNullException.ThrowIfNull(bsp);

            this.bsp = bsp;
            this.lightStyles = lightStyles;
            rootNode = bsp.Models.Length > 0 ? bsp.Models[0].HeadNode0 : -1;
            surfaces = BuildSurfaces();
        }

        private SurfaceInfo[] BuildSurfaces()
        {
            var faces = bsp.Faces;
            var textureInfos = bsp.TextureInfos;
            var names = bsp.TextureNames;
            var extents = bsp.LightmapExtents;
            var result = new SurfaceInfo[faces.Length];

            for (var i = 0; i < faces.Length; i++)
            {
                var face = faces[i];

                if (face.TexInfo < 0 || face.TexInfo >= textureInfos.Length || face.NumEdges < 3)
                {
                    continue;
                }

                var info = textureInfos[face.TexInfo];
                var name = info.MipTexture >= 0 && info.MipTexture < names.Count ? names[info.MipTexture] : string.Empty;

                // Sky, scrolling and special surfaces such as water have no lightmap and are never sampled
                var isSkipped = info.IsSpecial
                    || name.StartsWith("sky", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("scroll", StringComparison.OrdinalIgnoreCase);
                var extent = extents[i];

                result[i] = new SurfaceInfo(extent.MinS * 16, extent.MinT * 16, (extent.Width - 1) * 16, (extent.Height - 1) * 16, !isSkipped);
            }

            return result;
        }

        /// <summary>Gets the light at the origin of a model.</summary>
        /// <param name="origin">The origin of the entity.</param>
        /// <param name="modelFlags">The flags of the studio model.</param>
        /// <param name="effects">The effects of the entity.</param>
        public GoldSrcStudioLight Sample(Vector3 origin, int modelFlags, int effects)
        {
            if (bsp.Lighting.Length == 0)
            {
                return GoldSrcStudioLight.Default;
            }

            var down = (effects & InvertedLightEffect) != 0 ? 1f : -1f;
            var start = new Vector3(origin.X, origin.Y, origin.Z - (down * StartHeight));
            var end = start + new Vector3(0f, 0f, down * TraceLength);

            var center = Trace(start, end);
            var lowLow = GetBrightness(start, end, -SampleSpread, -SampleSpread);
            var highLow = GetBrightness(start, end, SampleSpread, -SampleSpread);
            var highHigh = GetBrightness(start, end, SampleSpread, SampleSpread);
            var lowHigh = GetBrightness(start, end, -SampleSpread, SampleSpread);

            var gradient = new Vector3(lowLow - highLow - highHigh + lowHigh, highLow + lowLow - highHigh - lowHigh, down);
            gradient = Vector3.Normalize(gradient);

            var peak = MathF.Max(center.R, MathF.Max(center.G, center.B));
            var scale = peak == 0f ? 1f : peak;
            var directLight = (modelFlags & FlatLightFlag) != 0 ? FlatDirectLight : DirectLight;
            var direct = gradient * (scale * directLight);

            var shade = (int)direct.Length();
            var ambient = Math.Min((int)(scale - shade), MaxAmbient);

            if (ambient + shade > MaxLight)
            {
                shade = MaxLight - ambient;
            }

            var color = peak == 0f ? Vector3.One : new Vector3(center.R, center.G, center.B) / peak;

            return new GoldSrcStudioLight(ambient / 255f, shade / 255f, color, Vector3.Normalize(direct));
        }

        private float GetBrightness(Vector3 start, Vector3 end, float offsetX, float offsetY)
        {
            var offset = new Vector3(offsetX, offsetY, 0f);
            var sample = Trace(start + offset, end + offset);
            return (sample.R + sample.G + sample.B) / 768f;
        }

        private LightSample Trace(Vector3 start, Vector3 end) => TraceNode(rootNode, start, end);

        private LightSample TraceNode(int nodeIndex, Vector3 start, Vector3 end)
        {
            if (nodeIndex < 0 || nodeIndex >= bsp.Nodes.Length)
            {
                return default;
            }

            var node = bsp.Nodes[nodeIndex];
            var plane = bsp.Planes[node.PlaneNum];
            var startDistance = Vector3.Dot(start, plane.Normal) - plane.Dist;
            var endDistance = Vector3.Dot(end, plane.Normal) - plane.Dist;
            var startBehind = startDistance < 0f;
            var near = startBehind ? node.Back : node.Front;
            var far = startBehind ? node.Front : node.Back;

            if ((endDistance < 0f) == startBehind)
            {
                return TraceNode(near, start, end);
            }

            var fraction = startDistance / (startDistance - endDistance);
            var middle = start + ((end - start) * fraction);

            var nearSample = TraceNode(near, start, middle);

            if (nearSample.IsLight)
            {
                return nearSample;
            }

            var surfaceSample = SampleSurfaces(node, middle);

            if (surfaceSample.IsLight)
            {
                return surfaceSample;
            }

            return TraceNode(far, middle, end);
        }

        private LightSample SampleSurfaces(in GoldSrcNode node, Vector3 point)
        {
            var faces = bsp.Faces;
            var textureInfos = bsp.TextureInfos;
            var lighting = bsp.Lighting;

            for (var i = 0; i < node.NumFaces; i++)
            {
                var faceIndex = node.FirstFace + i;

                if (faceIndex >= faces.Length)
                {
                    break;
                }

                var surface = surfaces[faceIndex];
                var face = faces[faceIndex];

                if (!surface.IsLit)
                {
                    continue;
                }

                var info = textureInfos[face.TexInfo];
                var coordinates = info.GetTextureCoordinates(point);
                var s = (int)coordinates.X - surface.MinS;
                var t = (int)coordinates.Y - surface.MinT;

                if (s < 0 || t < 0 || s > surface.ExtentS || t > surface.ExtentT)
                {
                    continue;
                }

                // A surface without a lightmap ends the search with no light
                if (!face.HasLightmap)
                {
                    return default;
                }

                return ReadLuxel(face, surface, s >> 4, t >> 4, lighting);
            }

            return default;
        }

        private LightSample ReadLuxel(in GoldSrcFace face, in SurfaceInfo surface, int s, int t, ReadOnlySpan<byte> lighting)
        {
            var width = (surface.ExtentS >> 4) + 1;
            var height = (surface.ExtentT >> 4) + 1;
            var stride = width * height * 3;
            var offset = face.LightOffset + (((t * width) + s) * 3);
            var r = 0;
            var g = 0;
            var b = 0;

            for (var slot = 0; slot < 4; slot++)
            {
                var style = face.GetLightStyle(slot);

                if (style == BspLightStyles.None)
                {
                    break;
                }

                var index = offset + (slot * stride);

                if (index < 0 || index + 3 > lighting.Length)
                {
                    break;
                }

                var factor = lightStyles?.GetSampleScale(style) ?? 256;

                r += factor * lighting[index];
                g += factor * lighting[index + 1];
                b += factor * lighting[index + 2];
            }

            // A lit surface always counts as a hit, even when it is black
            return new LightSample(Math.Max(r >> 8, 1), g >> 8, b >> 8);
        }
    }
}
