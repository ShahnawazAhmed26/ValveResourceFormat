using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Types.GLViewers;
using GUI.Utils;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Source1.Model;

namespace GUI.Types.Viewers
{
    class Source1ModelViewer(VrfGuiContext vrfGuiContext) : IViewer, IDisposable
    {
        private RendererContext? rendererContext;
        private GLSource1ModelViewer? glViewer;

        /// <summary>"IDST", the magic of a studio model.</summary>
        public static bool IsAccepted(uint magic) => magic == 0x54534449;

        public async Task LoadAsync(Stream? stream)
        {
            await Task.Yield();

            var fileSystem = Source1Content.GetShared(vrfGuiContext);
            var modelPath = Source1Content.GetGamePath(vrfGuiContext, fileSystem);

            var package = vrfGuiContext.CurrentPackage;
            var modelDirectory = Path.GetDirectoryName(vrfGuiContext.FileName);

            // The model's other files must come from the same place as the model, the game can override them with another build
            Stream? OpenFile(string path)
            {
                if (path.Equals(modelPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Source1Content.OpenFile(vrfGuiContext, stream);
                }

                if (package != null)
                {
                    var entry = package.FindEntry(path);

                    if (entry != null)
                    {
                        return package.GetMemoryMappedStreamIfPossible(entry);
                    }
                }
                else if (modelDirectory != null)
                {
                    var diskPath = Path.Combine(modelDirectory, Path.GetFileName(path));

                    if (File.Exists(diskPath))
                    {
                        return new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    }
                }

                return fileSystem?.OpenFile(path);
            }

            var model = Source1Model.Load(modelPath, OpenFile);

            rendererContext = vrfGuiContext.CreateRendererContext();
            rendererContext.Source1FileSystem = fileSystem;

            glViewer = new GLSource1ModelViewer(vrfGuiContext, rendererContext, model);
            glViewer.InitializeLoad();
        }

        public void Create(TabPage tab)
        {
            Debug.Assert(glViewer is not null);

            tab.Controls.Add(glViewer.InitializeUiControls());
        }

        public void NotifyVisible() => glViewer?.NotifyVisible();

        public void Dispose()
        {
            glViewer?.Dispose();
            rendererContext?.Dispose();
        }
    }
}
