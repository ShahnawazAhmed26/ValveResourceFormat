using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using GUI.Utils;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Source1;
using ValveResourceFormat.Source1.FileSystem;
using ValveResourceFormat.Source1.Model;

namespace GUI.Types.GLViewers
{
    /// <summary>
    /// Scene of a Source 1 model. The renderer context reads materials and textures through <see cref="Source1FileSystem"/>.
    /// </summary>
    class GLSource1ModelViewer(VrfGuiContext vrfGuiContext, RendererContext rendererContext, Source1Model model)
        : GLSingleNodeViewer(vrfGuiContext, rendererContext)
    {
        public Source1Model Model { get; } = model;

        private Source1ModelSceneNode? modelNode;
        private CheckBox? autoplayCheckBox;
        private ComboBox? sequenceComboBox;

        public override void Dispose()
        {
            base.Dispose();

            autoplayCheckBox?.Dispose();
            sequenceComboBox?.Dispose();
        }

        protected override void LoadScene()
        {
            base.LoadScene();

            modelNode = new Source1ModelSceneNode(Scene, Model, animated: true, flexes: true);
            Scene.Add(modelNode, true);
        }

        protected override void AddUiControls()
        {
            Debug.Assert(UiControl != null);

            if (modelNode != null)
            {
                Input.OrbitTargetProvider = () => modelNode.BoundingBox.Center;

                AddModelControls(modelNode);

                if (modelNode.IsAnimated && modelNode.PlayableSequences.Count > 0)
                {
                    AddAnimationControls(modelNode);
                }

                if (modelNode.HasFlexes)
                {
                    AddFlexControllerControls(modelNode);
                }
            }

            base.AddUiControls();
        }

        private void AddModelControls(Source1ModelSceneNode node)
        {
            Debug.Assert(UiControl != null);

            using var _ = UiControl.BeginGroup("Model");

            var bodyGroups = Model.BodyParts
                .Select(static part => new BodyGroupChoices(part.Name, [.. part.Models.Select(static model => model.Name)]))
                .ToList();

            this.AddSkinAndBodyGroupControls(node.SkinCount, node.Skin, node.SetSkin, bodyGroups, node.GetBodyGroup, node.SetBodyGroup);
        }

        private void AddAnimationControls(Source1ModelSceneNode node)
        {
            Debug.Assert(UiControl != null);

            using (UiControl.BeginGroup("Animation"))
            {
                sequenceComboBox = UiControl.AddSelection("Sequence", (name, index) =>
                {
                    if (index < 0)
                    {
                        return;
                    }

                    node.PlaySequence(index == 0 ? null : name);
                });

                sequenceComboBox.Items.Add("(reference pose)");
                sequenceComboBox.Items.AddRange([.. node.PlayableSequences.Select(static sequence => sequence.Name).Order(StringComparer.OrdinalIgnoreCase)]);
                var defaultIndex = sequenceComboBox.FindStringExact("ref");

                if (defaultIndex < 0)
                {
                    defaultIndex = sequenceComboBox.FindStringExact("idle");
                }

                sequenceComboBox.SelectedIndex = Math.Max(defaultIndex, 0);

                autoplayCheckBox = this.AddPlaybackControls(
                    isPaused => node.Paused = isPaused,
                    isLooping => node.Loop = isLooping,
                    cycle => node.Cycle = cycle,
                    speed => node.PlaybackSpeed = speed);

                UiControl.AddCheckBox("Root Motion", false, isChecked =>
                {
                    node.RootMotion = isChecked;
                });
            }

            AddPoseParameterControls(node);
        }

        private void AddFlexControllerControls(Source1ModelSceneNode node)
        {
            Debug.Assert(UiControl != null);

            using var _ = UiControl.BeginGroup("Flex Controllers");

            var controllers = Model.FlexControllers;

            for (var i = 0; i < controllers.Length; i++)
            {
                var controller = controllers[i];
                var range = controller.Max - controller.Min;

                if (range <= 0f)
                {
                    continue;
                }

                var index = i;
                var min = controller.Min;
                var normalizedDefault = Math.Clamp((node.GetFlexController(index) - min) / range, 0f, 1f);

                UiControl.AddTrackBar(controller.Name, value =>
                {
                    node.SetFlexController(index, min + (value * range));
                }, normalizedDefault);
            }
        }

        private void AddPoseParameterControls(Source1ModelSceneNode node)
        {
            Debug.Assert(UiControl != null);

            var parameters = Model.PoseParameters;

            if (parameters.Length == 0)
            {
                return;
            }

            var defaults = Model.CreateDefaultPoseParameters();

            using var _ = UiControl.BeginGroup("Pose Parameters");

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var low = MathF.Min(parameter.Start, parameter.End);
                var high = MathF.Max(parameter.Start, parameter.End);
                var range = high - low;

                if (range <= 0f)
                {
                    continue;
                }

                var name = parameter.Name;
                var normalizedDefault = Math.Clamp((defaults[i] - low) / range, 0f, 1f);

                UiControl.AddTrackBar(name, value =>
                {
                    node.SetPoseParameter(name, low + (value * range));
                }, normalizedDefault);
            }
        }
    }
}
