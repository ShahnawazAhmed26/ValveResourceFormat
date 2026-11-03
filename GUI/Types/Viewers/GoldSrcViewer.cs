using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Types.GLViewers;
using GUI.Utils;
using SkiaSharp;
using ValveResourceFormat.GoldSrc;
using ValveResourceFormat.Renderer;

namespace GUI.Types.Viewers
{
    /// <summary>
    /// Shows the textures of GoldSrc files: WADs, sprites, models and maps.
    /// </summary>
    class GoldSrcViewer(VrfGuiContext vrfGuiContext) : IViewer, IDisposable
    {
        private const int ThumbnailSize = 128;
        private const int SpriteMagic = 0x50534449;

        private readonly List<(string Name, string Details, SKBitmap Bitmap)> textures = [];
        private GLBaseControl? glViewer;
        private RendererContext? viewerRendererContext;
        private GLGoldSrcMapViewer? mapViewer;
        private RendererContext? mapRendererContext;
        private ImageList? thumbnails;

        /// <param name="magic">The first four bytes of the file.</param>
        /// <param name="version">The two bytes after the magic, the version of models and sprites.</param>
        public static bool IsAccepted(uint magic, ushort version, string fileName)
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, magic);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], version);

            if (WadFile.IsWad(header) || GoldSrcMdlFile.IsModel(header))
            {
                return true;
            }

            if (magic == SpriteMagic)
            {
                return version == SprFile.SupportedVersion;
            }

            return GoldSrcBspFile.IsGoldSrcBsp(header) && fileName.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase);
        }

        public async Task LoadAsync(Stream? stream)
        {
            await Task.Yield();

            byte[] data;

            if (stream == null)
            {
                using var file = new FileStream(vrfGuiContext.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                data = new byte[file.Length];
                await file.ReadExactlyAsync(data).ConfigureAwait(false);
            }
            else
            {
                stream.Position = 0;
                data = new byte[stream.Length];
                await stream.ReadExactlyAsync(data).ConfigureAwait(false);
            }

            var name = Path.GetFileNameWithoutExtension(vrfGuiContext.FileName);

            if (WadFile.IsWad(data))
            {
                LoadWad(data, name);
            }
            else if (BinaryPrimitives.ReadInt32LittleEndian(data) == SpriteMagic)
            {
                LoadSprite(data);
            }
            else if (GoldSrcMdlFile.IsModel(data))
            {
                LoadModel(data, name);
            }
            else
            {
                LoadMap(data, name);
            }
        }

        public void Create(TabPage tab)
        {
            if (glViewer != null)
            {
                tab.Controls.Add(glViewer.InitializeUiControls());
                return;
            }

            if (mapViewer != null)
            {
                var tabs = new ThemedTabControl
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                };
                tab.Controls.Add(tabs);

                var mapTab = new ThemedTabPage("Map");
                mapTab.Controls.Add(mapViewer.InitializeUiControls());
                tabs.TabPages.Add(mapTab);

                var texturesTab = new ThemedTabPage("Textures");
                CreateTextureList(texturesTab);
                tabs.TabPages.Add(texturesTab);
                return;
            }

            CreateTextureList(tab);
        }

        private void CreateTextureList(Control parent)
        {
            thumbnails = new ImageList
            {
                ImageSize = new Size(ThumbnailSize, ThumbnailSize),
                ColorDepth = ColorDepth.Depth32Bit,
            };

            // The images are copied into the native list, which the thumbnails are disposed after
            _ = thumbnails.Handle;

            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.LargeIcon,
                LargeImageList = thumbnails,
                ShowItemToolTips = true,
            };

            foreach (var (name, details, bitmap) in textures)
            {
                using var thumbnail = CreateThumbnail(bitmap);
                thumbnails.Images.Add(thumbnail);

                list.Items.Add(new ListViewItem(name, thumbnails.Images.Count - 1)
                {
                    ToolTipText = details,
                });
            }

            parent.Controls.Add(list);
        }

        public void NotifyVisible()
        {
            glViewer?.NotifyVisible();
            mapViewer?.NotifyVisible();
        }

        public void Dispose()
        {
            glViewer?.Dispose();
            viewerRendererContext?.Dispose();
            mapViewer?.Dispose();
            mapRendererContext?.Dispose();
            thumbnails?.Dispose();

            foreach (var (_, _, bitmap) in textures)
            {
                bitmap.Dispose();
            }
        }

        private void LoadWad(byte[] data, string name)
        {
            var wad = new WadFile(data, name);

            foreach (var lump in wad.Lumps)
            {
                var image = wad.ReadImage(lump);

                if (image != null)
                {
                    AddImage(lump.Name, image, image.IsMasked);
                }
            }
        }

        private void LoadSprite(byte[] data)
        {
            var sprite = new SprFile(data);
            var image = sprite.GetImage(0, Path.GetFileNameWithoutExtension(vrfGuiContext.FileName));
            var bitmap = image.ToBitmap(0, sprite.HasTransparentLastColor);

            viewerRendererContext = vrfGuiContext.CreateRendererContext();
            glViewer = new GLTextureViewer(vrfGuiContext, viewerRendererContext, bitmap);
            glViewer.InitializeLoad();
        }

        private void LoadModel(byte[] data, string name)
        {
            var fileSystem = GoldSrcFileSystem.FromPath(vrfGuiContext.FileName, VrfGuiContext.Logger);
            var mdl = GoldSrcMdlFile.Load(data, name, fileName => GoldSrcFileSystem.ReadCompanion(vrfGuiContext.FileName, fileName, fileSystem));

            viewerRendererContext = vrfGuiContext.CreateRendererContext();
            glViewer = new GLGoldSrcModelViewer(vrfGuiContext, viewerRendererContext, mdl);
            glViewer.InitializeLoad();
        }

        private void LoadMap(byte[] data, string name)
        {
            var bsp = new GoldSrcBspFile(data, name);
            var fileSystem = GoldSrcFileSystem.FromPath(vrfGuiContext.FileName, VrfGuiContext.Logger);

            for (var i = 0; i < bsp.Textures.Count; i++)
            {
                var textureName = bsp.TextureNames[i];
                var image = bsp.Textures[i];
                var source = "embedded";

                if (image == null && textureName.Length > 0)
                {
                    image = fileSystem?.FindTexture(textureName, bsp.WadNames);
                    source = "from a WAD";
                }

                if (image != null)
                {
                    AddImage(textureName, image, textureName.StartsWith('{'), source);
                }
            }

            mapRendererContext = vrfGuiContext.CreateRendererContext();
            mapViewer = new GLGoldSrcMapViewer(vrfGuiContext, mapRendererContext, bsp, fileSystem);
            mapViewer.InitializeLoad();
        }

        private void AddImage(string name, PalettedImage image, bool masked, string? extra = null)
        {
            var details = string.Create(CultureInfo.InvariantCulture, $"{image.Width}x{image.Height} {extra}");
            textures.Add((name, details.Trim(), image.ToBitmap(0, masked)));
        }

        private static Bitmap CreateThumbnail(SKBitmap bitmap)
        {
            var scale = Math.Min(1f, ThumbnailSize / (float)Math.Max(bitmap.Width, bitmap.Height));
            var width = Math.Max(1, (int)(bitmap.Width * scale));
            var height = Math.Max(1, (int)(bitmap.Height * scale));

            using var resized = bitmap.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default);
            using var decoded = (resized ?? bitmap).ToBitmap();

            var result = new Bitmap(ThumbnailSize, ThumbnailSize);
            using var graphics = Graphics.FromImage(result);
            graphics.DrawImage(decoded, (ThumbnailSize - width) / 2, (ThumbnailSize - height) / 2, width, height);
            return result;
        }
    }
}
