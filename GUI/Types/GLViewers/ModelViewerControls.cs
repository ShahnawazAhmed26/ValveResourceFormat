using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using GUI.Controls;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;

namespace GUI.Types.GLViewers
{
    /// <summary>A body group of a model: its name and the names of the sub-models to choose from.</summary>
    internal readonly record struct BodyGroupChoices(string Name, string[] ModelNames);

    /// <summary>
    /// Controls shared by the viewers of Source 1 and GoldSrc studio models.
    /// </summary>
    internal static class ModelViewerControls
    {
        /// <summary>Gets whether an animation is a sequence the model marks as hidden, which no sequence list shows.</summary>
        public static bool IsHiddenSequence(AnimationBase animation) => animation is SequenceAnimation { Hidden: true };

        /// <summary>
        /// Adds the autoplay, loop, frame and speed controls of an animation that starts out playing and looping.
        /// </summary>
        /// <returns>The autoplay check box.</returns>
        public static CheckBox AddPlaybackControls(
            this GLBaseControl viewer,
            Action<bool> setPaused,
            Action<bool> setLoop,
            Action<float> setCycle,
            Action<float> setSpeed)
        {
            var uiControl = viewer.UiControl;
            Debug.Assert(uiControl != null);

            var autoplayCheckBox = uiControl.AddCheckBox("Autoplay", true, isChecked => setPaused(!isChecked));

            uiControl.AddCheckBox("Loop", true, setLoop);
            setLoop(true);

            uiControl.AddTrackBar("Animation Frame", cycle =>
            {
                setPaused(true);
                setCycle(cycle);
            });

            uiControl.AddTrackBar("Animation Speed", value => setSpeed(value * 2f), 0.5f);

            return autoplayCheckBox;
        }

        /// <summary>
        /// Adds the skin and body group selections of a converted studio model shown by a <see cref="ModelSceneNode"/>.
        /// </summary>
        /// <param name="bodyGroups">The body groups of the source model, in the order the converted model has them.</param>
        public static void AddConvertedModelControls(this GLBaseControl viewer, ModelSceneNode node, IReadOnlyList<BodyGroupChoices> bodyGroups)
        {
            var uiControl = viewer.UiControl;
            Debug.Assert(uiControl != null);

            using var _ = uiControl.BeginGroup("Model");

            var skins = node.GetMaterialGroups().ToList();
            var selectedSubModels = new int[bodyGroups.Count];

            void SetSkin(int skin)
            {
                node.SetMaterialGroup(skins[skin]);
            }

            void SetBodyGroup(int bodyPart, int subModel)
            {
                selectedSubModels[bodyPart] = subModel;
                node.SetBodyGroup(bodyGroups[bodyPart].Name, subModel);
            }

            viewer.AddSkinAndBodyGroupControls(skins.Count, 0, SetSkin, bodyGroups, part => selectedSubModels[part], SetBodyGroup);
        }

        /// <summary>
        /// Adds the sequence selection and playback controls for the animations of a converted studio model.
        /// The reference pose is the first entry.
        /// </summary>
        /// <returns>The sequence selection and the autoplay check box.</returns>
        public static (ComboBox Sequences, CheckBox Autoplay) AddConvertedAnimationControls(this GLBaseControl viewer, ModelSceneNode node)
        {
            var uiControl = viewer.UiControl;
            Debug.Assert(uiControl != null);

            var controller = node.AnimationController;

            using var _ = uiControl.BeginGroup("Animation");

            var sequenceComboBox = uiControl.AddSelection("Sequence", (name, index) =>
            {
                if (index < 0)
                {
                    return;
                }

                var animation = index == 0 ? null : node.Animations[name];
                node.SetAnimation(animation);
            });

            sequenceComboBox.Items.Add("(reference pose)");

            foreach (var (name, animation) in node.Animations.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (IsHiddenSequence(animation) || animation.IsAdditive)
                {
                    continue;
                }

                sequenceComboBox.Items.Add(name);
            }

            var defaultIndex = sequenceComboBox.FindStringExact("ref");

            if (defaultIndex < 0)
            {
                defaultIndex = sequenceComboBox.FindStringExact("idle");
            }

            if (defaultIndex < 0)
            {
                defaultIndex = 1;
            }

            sequenceComboBox.SelectedIndex = defaultIndex;

            var autoplayCheckBox = viewer.AddPlaybackControls(
                isPaused => controller.IsPaused = isPaused,
                isLooping => controller.Looping = isLooping,
                cycle =>
                {
                    if (controller.ActiveAnimation is { CycleFrames: > 0 } animation)
                    {
                        controller.Frame = (int)MathF.Round(cycle * animation.CycleFrames);
                    }
                },
                speed => controller.FrametimeMultiplier = speed);

            foreach (var parameter in controller.PoseParameters)
            {
                AddPoseParameterControl(uiControl, controller, parameter);
            }

            return (sequenceComboBox, autoplayCheckBox);
        }

        public static void AddPoseParameterControl(RendererControl uiControl, AnimationController controller, PoseParameter parameter)
        {
            var range = parameter.Max - parameter.Min;
            var current = controller.GetPoseParameter(parameter.Name);
            var normalized = range > 0f ? (current - parameter.Min) / range : 0f;

            uiControl.AddTrackBar(parameter.Name, value => controller.SetPoseParameter(parameter.Name, parameter.Min + value * range), normalized);
        }

        /// <summary>
        /// Adds the skin selection when the model has more than one skin, and a selection for each body group
        /// that has more than one sub-model.
        /// </summary>
        public static void AddSkinAndBodyGroupControls(
            this GLBaseControl viewer,
            int skinCount,
            int skin,
            Action<int> setSkin,
            IReadOnlyList<BodyGroupChoices> bodyGroups,
            Func<int, int> getBodyGroup,
            Action<int, int> setBodyGroup)
        {
            var uiControl = viewer.UiControl;
            Debug.Assert(uiControl != null);

            if (skinCount > 1)
            {
                var skinComboBox = uiControl.AddSelection("Skin", (_, index) =>
                {
                    if (index < 0)
                    {
                        return;
                    }

                    using var lockedGl = viewer.MakeCurrent();
                    setSkin(index);
                });

                for (var i = 0; i < skinCount; i++)
                {
                    skinComboBox.Items.Add(string.Create(CultureInfo.InvariantCulture, $"Skin {i}"));
                }

                skinComboBox.SelectedIndex = skin;
            }

            for (var partIndex = 0; partIndex < bodyGroups.Count; partIndex++)
            {
                var bodyGroup = bodyGroups[partIndex];

                if (bodyGroup.ModelNames.Length < 2)
                {
                    continue;
                }

                var capturedPartIndex = partIndex;

                var bodyGroupComboBox = uiControl.AddSelection(bodyGroup.Name, (_, index) =>
                {
                    if (index < 0)
                    {
                        return;
                    }

                    using var lockedGl = viewer.MakeCurrent();
                    setBodyGroup(capturedPartIndex, index);
                });

                for (var i = 0; i < bodyGroup.ModelNames.Length; i++)
                {
                    var subModelName = bodyGroup.ModelNames[i];
                    bodyGroupComboBox.Items.Add(string.IsNullOrEmpty(subModelName) ? string.Create(CultureInfo.InvariantCulture, $"Model {i}") : subModelName);
                }

                bodyGroupComboBox.SelectedIndex = getBodyGroup(partIndex);
            }
        }
    }
}
