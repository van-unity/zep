using System;
using Zep.Core.Theming;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Theming
{
    internal sealed class ThemeCreatorScreen : VisualElement
    {
        private readonly ThemeCreatorScreenModel _model;

        private readonly VisualElement _card;
        private readonly TextField _nameField;
        private readonly Button _modeLightButton;
        private readonly Button _modeDarkButton;
        private readonly SliderInt _redSlider;
        private readonly SliderInt _greenSlider;
        private readonly SliderInt _blueSlider;
        private readonly Label _redValue;
        private readonly Label _greenValue;
        private readonly Label _blueValue;
        private readonly ThemePreviewCard _preview;
        private readonly VisualElement _saveDestinationGroup;
        private readonly Button _destinationLocalButton;
        private readonly Button _destinationProjectButton;
        private readonly Button _saveButton;
        private readonly Label _titleLabel;

        private bool _suppressEvents;
        private readonly ModalFade _fade = new ModalFade();

        public event Action<string, string> SaveSucceeded;
        public event Action<string> SaveFailed;
        // Raised on Cancel / close / scrim-click / Escape — the controller tears
        // the modal down (same exit the old top-bar "← Back" button drove).
        public event Action CancelRequested;

        public ThemeCreatorScreen(ThemeCreatorScreenModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));

            // Root is a full-panel modal scrim; the card floats centered on it.
            AddToClassList(ZepUiClasses.ModalScrim);
            AddToClassList(ZepUiClasses.PromptRoot);
            style.display = DisplayStyle.None;
            RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.target == this && evt.button == 0)
                    CancelRequested?.Invoke();
            });
            // No Escape handler here: the shell's TrickleDown key router closes this
            // modal and stops the event before it descends (ZepShortcutController
            // .HandleKeyDown, the IsOverlayActive leg).

            _card = new VisualElement { usageHints = UsageHints.DynamicTransform };
            _card.AddToClassList(ZepUiClasses.CreatorScreen);
            Add(_card);

            var header = new VisualElement();
            header.AddToClassList(ZepUiClasses.CreatorHeader);
            _titleLabel = new Label("Create Theme");
            _titleLabel.AddToClassList(ZepUiClasses.CreatorTitle);
            header.Add(_titleLabel);
            var closeButton = new Button(() => CancelRequested?.Invoke())
            {
                text = "✕",
                tooltip = "Close"
            };
            closeButton.AddToClassList(ZepUiClasses.CreatorCloseButton);
            closeButton.AddToClassList(ZepUiClasses.FlatButton);
            header.Add(closeButton);
            _card.Add(header);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList(ZepUiClasses.CreatorScroll);
            _card.Add(scroll);

            scroll.Add(BuildSection("Theme details", BuildNameField(out _nameField)));
            scroll.Add(BuildSection("Base mode", BuildModeRow(out _modeLightButton, out _modeDarkButton)));
            scroll.Add(BuildSection("Seed color", BuildSeedSliders(out _redSlider, out _redValue, out _greenSlider, out _greenValue, out _blueSlider, out _blueValue)));

            _preview = new ThemePreviewCard();
            scroll.Add(BuildSection("Preview", _preview));

            _saveDestinationGroup = BuildSaveDestinationGroup(out _destinationLocalButton, out _destinationProjectButton);
            scroll.Add(_saveDestinationGroup);

            var actions = new VisualElement();
            actions.AddToClassList(ZepUiClasses.CreatorActions);
            var cancelButton = new Button(() => CancelRequested?.Invoke()) { text = "Cancel" };
            cancelButton.AddToClassList(ZepUiClasses.CreatorCancelButton);
            actions.Add(cancelButton);
            _saveButton = new Button(OnSaveClicked) { text = "Save theme" };
            _saveButton.AddToClassList(ZepUiClasses.CreatorSaveButton);
            actions.Add(_saveButton);
            _card.Add(actions);

            // Subscribe on attach, not in the ctor: this screen is built once and
            // re-parents with the shell on dock/undock (detach -> attach, no
            // rebuild). A ctor-only subscribe is dropped on the first detach and
            // never restored, leaving the modal frozen on stale model state.
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                _model.Changed += SyncFromModel;
                SyncFromModel();
            });
            RegisterCallback<DetachFromPanelEvent>(_ => _model.Changed -= SyncFromModel);

            // Fields only: the preview card rebuilds its sheet on a scheduled
            // item, which needs a panel, and the attach callback above syncs
            // everything again the moment this screen is mounted.
            SyncFieldsFromModel();
        }

        // Modal show/hide with the same fade+lift the prompt overlay uses. The
        // controller owns when; the animation lives here.
        public void Show() => _fade.Show(this, _card, () => _nameField.Focus());

        public void Hide() => _fade.Hide(this, _card);

        private void OnSaveClicked()
        {
            var savedName = _model.ThemeName;
            var result = _model.Save();
            if (result.Success)
                SaveSucceeded?.Invoke(result.SavedThemeId, savedName);
            else
                SaveFailed?.Invoke(result.Error ?? "Save failed.");
        }

        private static VisualElement BuildSection(string title, VisualElement body)
        {
            var section = new VisualElement();
            section.AddToClassList(ZepUiClasses.CreatorSection);

            var label = new Label(title);
            label.AddToClassList(ZepUiClasses.CreatorSectionLabel);
            section.Add(label);

            section.Add(body);
            return section;
        }

        private VisualElement BuildNameField(out TextField nameField)
        {
            nameField = new TextField { value = string.Empty };
            nameField.AddToClassList(ZepUiClasses.CreatorNameField);
            var localField = nameField;
            localField.RegisterValueChangedCallback(evt =>
            {
                if (_suppressEvents) return;
                _model.SetThemeName(evt.newValue);
            });
            return nameField;
        }

        private VisualElement BuildModeRow(out Button lightButton, out Button darkButton)
        {
            var row = new VisualElement();
            row.AddToClassList(ZepUiClasses.CreatorSegmented);

            lightButton = new Button(() =>
            {
                if (_suppressEvents) return;
                _model.SetIsLight(true);
            }) { text = "Light" };
            lightButton.AddToClassList(ZepUiClasses.CreatorSegmentedButton);
            lightButton.AddToClassList(ZepUiClasses.FlatButton);
            row.Add(lightButton);

            darkButton = new Button(() =>
            {
                if (_suppressEvents) return;
                _model.SetIsLight(false);
            }) { text = "Dark" };
            darkButton.AddToClassList(ZepUiClasses.CreatorSegmentedButton);
            darkButton.AddToClassList(ZepUiClasses.FlatButton);
            row.Add(darkButton);

            return row;
        }

        private VisualElement BuildSeedSliders(
            out SliderInt redSlider, out Label redValue,
            out SliderInt greenSlider, out Label greenValue,
            out SliderInt blueSlider, out Label blueValue)
        {
            var container = new VisualElement();

            redSlider = BuildChannelSlider("R", ZepUiClasses.CreatorSliderRed, out redValue);
            container.Add(redSlider.parent);

            greenSlider = BuildChannelSlider("G", ZepUiClasses.CreatorSliderGreen, out greenValue);
            container.Add(greenSlider.parent);

            blueSlider = BuildChannelSlider("B", ZepUiClasses.CreatorSliderBlue, out blueValue);
            container.Add(blueSlider.parent);

            redSlider.RegisterValueChangedCallback(evt =>
            {
                if (_suppressEvents) return;
                _model.SetAccent(MakeAccent(evt.newValue, _greenSlider.value, _blueSlider.value));
            });
            greenSlider.RegisterValueChangedCallback(evt =>
            {
                if (_suppressEvents) return;
                _model.SetAccent(MakeAccent(_redSlider.value, evt.newValue, _blueSlider.value));
            });
            blueSlider.RegisterValueChangedCallback(evt =>
            {
                if (_suppressEvents) return;
                _model.SetAccent(MakeAccent(_redSlider.value, _greenSlider.value, evt.newValue));
            });

            return container;
        }

        private static SliderInt BuildChannelSlider(string label, string trackClass, out Label valueLabel)
        {
            var row = new VisualElement();
            row.AddToClassList(ZepUiClasses.CreatorSliderRow);

            var name = new Label(label);
            name.AddToClassList(ZepUiClasses.CreatorSliderLabel);
            row.Add(name);

            var slider = new SliderInt(0, 255) { showInputField = false };
            slider.AddToClassList(ZepUiClasses.CreatorSlider);
            slider.AddToClassList(trackClass);
            row.Add(slider);

            valueLabel = new Label("0");
            valueLabel.AddToClassList(ZepUiClasses.CreatorSliderValue);
            row.Add(valueLabel);

            return slider;
        }

        private VisualElement BuildSaveDestinationGroup(out Button localButton, out Button projectButton)
        {
            var section = new VisualElement();
            section.AddToClassList(ZepUiClasses.CreatorSection);

            var label = new Label("Save to");
            label.AddToClassList(ZepUiClasses.CreatorSectionLabel);
            section.Add(label);

            var row = new VisualElement();
            row.AddToClassList(ZepUiClasses.CreatorSegmented);

            localButton = new Button(() =>
            {
                if (_suppressEvents) return;
                _model.SetSaveDestination(ThemeSaveMode.UserPrefs);
            }) { text = "Local" };
            localButton.AddToClassList(ZepUiClasses.CreatorSegmentedButton);
            localButton.AddToClassList(ZepUiClasses.FlatButton);
            localButton.tooltip = "Saved on your machine only. Won't be committed to the project \u2014 others on your team won't see it.";
            row.Add(localButton);

            projectButton = new Button(() =>
            {
                if (_suppressEvents) return;
                _model.SetSaveDestination(ThemeSaveMode.ProjectAsset);
            }) { text = "Project" };
            projectButton.AddToClassList(ZepUiClasses.CreatorSegmentedButton);
            projectButton.AddToClassList(ZepUiClasses.FlatButton);
            projectButton.tooltip = "Saved as a project asset under Assets/Editor/Zep/UserThemes. Committable and shareable with your team.";
            row.Add(projectButton);

            section.Add(row);
            return section;
        }

        private void SyncFromModel()
        {
            SyncFieldsFromModel();
            _preview.ApplyVariables(_model.Variables);
        }

        private void SyncFieldsFromModel()
        {
            _suppressEvents = true;
            try
            {
                _titleLabel.text = _model.Mode == ThemeCreatorMode.Edit ? "Edit Theme" : "Create Theme";
                _saveButton.text = _model.Mode == ThemeCreatorMode.Edit ? "Save changes" : "Save theme";

                if (!string.Equals(_nameField.value, _model.ThemeName, StringComparison.Ordinal))
                    _nameField.SetValueWithoutNotify(_model.ThemeName);

                ToggleSegmentedActive(_modeLightButton, _model.IsLight);
                ToggleSegmentedActive(_modeDarkButton, !_model.IsLight);

                var r = ChannelByte(_model.Accent.r);
                var g = ChannelByte(_model.Accent.g);
                var b = ChannelByte(_model.Accent.b);
                _redSlider.SetValueWithoutNotify(r);
                _greenSlider.SetValueWithoutNotify(g);
                _blueSlider.SetValueWithoutNotify(b);
                _redValue.text = r.ToString();
                _greenValue.text = g.ToString();
                _blueValue.text = b.ToString();

                _saveDestinationGroup.style.display = _model.ShowSaveDestinationToggle
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
                ToggleSegmentedActive(_destinationLocalButton, _model.SaveDestination == ThemeSaveMode.UserPrefs);
                ToggleSegmentedActive(_destinationProjectButton, _model.SaveDestination == ThemeSaveMode.ProjectAsset);

                _saveButton.SetEnabled(_model.CanSave);
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private static void ToggleSegmentedActive(VisualElement element, bool active)
        {
            if (active)
            {
                if (!element.ClassListContains(ZepUiClasses.CreatorSegmentedButtonActive))
                    element.AddToClassList(ZepUiClasses.CreatorSegmentedButtonActive);
            }
            else if (element.ClassListContains(ZepUiClasses.CreatorSegmentedButtonActive))
            {
                element.RemoveFromClassList(ZepUiClasses.CreatorSegmentedButtonActive);
            }
        }

        private static int ChannelByte(float v) => Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private static Color MakeAccent(int r, int g, int b)
        {
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        }
    }
}
