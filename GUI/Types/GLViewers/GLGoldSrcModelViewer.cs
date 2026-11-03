using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using GUI.Utils;
using ValveResourceFormat.GoldSrc;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.GoldSrc;

namespace GUI.Types.GLViewers
{
    /// <summary>
    /// Scene of a GoldSrc studio model.
    /// </summary>
    class GLGoldSrcModelViewer(VrfGuiContext vrfGuiContext, RendererContext rendererContext, GoldSrcMdlFile model)
        : GLSingleNodeViewer(vrfGuiContext, rendererContext)
    {
        private const string RestPoseName = "(rest pose)";

        public GoldSrcMdlFile Model { get; } = model;

        private GoldSrcModelSceneNode? modelNode;
        private ComboBox? sequenceComboBox;
        private CheckBox? autoplayCheckBox;

        public override void Dispose()
        {
            base.Dispose();

            autoplayCheckBox?.Dispose();
            sequenceComboBox?.Dispose();
        }

        protected override void LoadScene()
        {
            base.LoadScene();

            modelNode = new GoldSrcModelSceneNode(Scene, Model);
            Scene.Add(modelNode, true);
        }

        protected override void AddUiControls()
        {
            Debug.Assert(UiControl != null);

            if (modelNode != null)
            {
                Input.OrbitTargetProvider = () => modelNode.BoundingBox.Center;

                AddModelControls(modelNode);

                if (modelNode.PlayableSequences.Count > 0)
                {
                    AddAnimationControls(modelNode);
                }

                AddBoneControllerControls(modelNode);
            }

            base.AddUiControls();
        }

        private void AddModelControls(GoldSrcModelSceneNode node)
        {
            Debug.Assert(UiControl != null);

            using var _ = UiControl.BeginGroup("Model");

            var bodyGroups = Model.BodyParts
                .Select(static part => new BodyGroupChoices(part.Name, [.. part.Models.Select(static model => model.Name)]))
                .ToList();

            this.AddSkinAndBodyGroupControls(node.SkinCount, node.Skin, node.SetSkin, bodyGroups, node.GetBodyGroup, node.SetBodyGroup);
        }

        private void AddAnimationControls(GoldSrcModelSceneNode node)
        {
            Debug.Assert(UiControl != null);

            using var _ = UiControl.BeginGroup("Animation");

            sequenceComboBox = UiControl.AddSelection("Sequence", (_, index) =>
            {
                if (index < 0)
                {
                    return;
                }

                node.PlaySequence(index == 0 ? null : node.PlayableSequences[index - 1]);
            });

            sequenceComboBox.Items.Add(RestPoseName);
            sequenceComboBox.Items.AddRange([.. node.PlayableSequences.Select(static sequence => sequence.Label)]);

            // The node starts out on its first sequence
            sequenceComboBox.SelectedIndex = 1;

            autoplayCheckBox = this.AddPlaybackControls(
                isPaused => node.Paused = isPaused,
                isLooping => node.Loop = isLooping,
                cycle => node.Cycle = cycle,
                speed => node.PlaybackSpeed = speed);

            UiControl.AddTrackBar("Blend", blend =>
            {
                node.Blend = blend;
            });
        }

        private void AddBoneControllerControls(GoldSrcModelSceneNode node)
        {
            Debug.Assert(UiControl != null);

            var controllers = Model.BoneControllers;

            if (controllers.Length == 0)
            {
                return;
            }

            using var _ = UiControl.BeginGroup("Bone Controllers");

            for (var i = 0; i < controllers.Length; i++)
            {
                var controller = controllers[i];
                var low = MathF.Min(controller.Start, controller.End);
                var range = MathF.Abs(controller.End - controller.Start);

                if (range <= 0f)
                {
                    continue;
                }

                var index = i;
                var normalizedDefault = (Model.GetDefaultControllerValue(index) - low) / range;
                var name = string.Create(CultureInfo.InvariantCulture, $"Controller {controller.Index}");

                UiControl.AddTrackBar(name, value =>
                {
                    node.SetController(index, low + (value * range));
                }, normalizedDefault);
            }
        }
    }
}
