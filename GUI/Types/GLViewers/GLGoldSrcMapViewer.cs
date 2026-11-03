using System.Diagnostics;
using System.Linq;
using GUI.Utils;
using ValveResourceFormat.GoldSrc;
using ValveResourceFormat.IO.Fgd;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Editor.Picking;
using ValveResourceFormat.Renderer.GoldSrc;
using ValveResourceFormat.Renderer.SceneEnvironment;
using ValveResourceFormat.Source1.Bsp;
using ValveResourceFormat.Source1.Entities;

namespace GUI.Types.GLViewers
{
    /// <summary>
    /// Scene of a GoldSrc map: the world and brush models with their baked lightmaps, the sky box and the sprites.
    /// </summary>
    class GLGoldSrcMapViewer(VrfGuiContext vrfGuiContext, RendererContext rendererContext, GoldSrcBspFile bsp, GoldSrcFileSystem? fileSystem)
        : GLSceneViewer(vrfGuiContext, rendererContext)
    {
        private MapStartPoint? start;

        public override void PreSceneLoad()
        {
            base.PreSceneLoad();
            LoadDefaultLighting();

            // Trigger, clip and hint brushes are editor-only
            Tools.ShowTools = false;
        }

        protected override void AddUiControls()
        {
            Debug.Assert(UiControl != null);

            using (UiControl.BeginGroup("Render"))
            {
                AddRenderModeSelectionControl();
                AddWireframeToggleControl();
            }

            base.AddUiControls();
        }

        protected override void OnPicked(PickResult result)
        {
            if (result.HitNothing)
            {
                Selection.Clear();
                return;
            }

            if (result.Intent != PickIntent.Select)
            {
                return;
            }

            var sceneNode = Renderer.FindPickedNode(result);

            if (sceneNode == null)
            {
                return;
            }

            if (result.Has(PickModifiers.Control))
            {
                Selection.Toggle(sceneNode);
            }
            else
            {
                Selection.Select(sceneNode);
            }
        }

        protected override void LoadScene()
        {
            var classes = fileSystem == null ? null : Source1EntityClassCatalog.GetGoldSrc(fileSystem.ModDirectory);
            var worldBounds = GoldSrcMapScene.Build(Scene, bsp, fileSystem, classes);

            start = MapStartPoint.Find(bsp.Entities, worldBounds, GoldSrcEntityAngles.Get);

            var post = new ScenePostProcessVolume(Scene)
            {
                IsMaster = true,
            };

            Scene.PostProcessInfo.AddPostProcessVolume(post);
        }

        public override void PostSceneLoad()
        {
            base.PostSceneLoad();

            start?.Apply(Input.Camera);
        }
    }
}
