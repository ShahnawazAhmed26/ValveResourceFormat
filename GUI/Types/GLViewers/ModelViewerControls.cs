using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;

namespace GUI.Types.GLViewers
{
    /// <summary>A body group of a model: its name and the names of the sub-models to choose from.</summary>
    internal readonly record struct BodyGroupChoices(string Name, string[] ModelNames);

    /// <summary>
    /// Controls shared by the viewers of Source 1 and GoldSrc studio models.
    /// </summary>
    internal static class ModelViewerControls
    {
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
