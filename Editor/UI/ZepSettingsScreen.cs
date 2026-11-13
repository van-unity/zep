using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.ShortcutManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Zep.Core.Domain;
using Zep.Core.Theming;
using Zep.Editor.UI.ContextMenu;

namespace Zep.Editor.UI
{
    internal sealed class ZepSettingsScreen : VisualElement
    {
        private readonly ZepSettingsScreenModel _model;
        private readonly VisualElement _tagEditor;
        private readonly VisualElement _themeControls;
        private readonly VisualElement _themeManagementList;
        private readonly List<Button> _navButtons = new List<Button>();
        private readonly List<VisualElement> _sectionPanels = new List<VisualElement>();
        private int _tagsSectionIndex;
        private PopupField<string> _themeDropdown;
        private Toggle _includeMetaToggle;
        private Toggle _promptForMetaToggle;
        private bool _isApplyingModelState;
        private PopupField<ShortcutModifiers> _searchModifiers;
        private PopupField<KeyCode> _searchKey;
        private Label _searchShortcutStatus;
        private Button _applySearchShortcut;
        private Button _resetSearchShortcut;

        // Test-only rebuild counters: a model change must touch only its own section.
        internal static int RebuildTagEditorCountForTests;
        internal static int RebuildThemeDropdownCountForTests;

        public event Action CloseRequested;
        public event Action<string> ThemeSelected;
        public event Action AddTagRequested;
        public event Action<ZepTag> TagDefinitionUpdated;
        public event Action<string> TagDefinitionRemoved;
        public event Action ResetTagsRequested;
        public event Action CreateThemeRequested;
        public event Action<string> EditThemeRequested;
        public event Action<ZepSettingsThemeOption> DeleteThemeRequested;
        public event Action<bool> IncludeMetaInArchiveChanged;
        public event Action<bool> PromptForMetaOnCompressChanged;
        public event Action<KeyCombination> SearchShortcutSelected;

        public ZepSettingsScreen(ZepSettingsScreenModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));

            name = "zep-settings-page";
            AddToClassList(ZepUiClasses.SettingsPage);

            // Fixed-width inner: the drawer root animates its own width from 0,
            // clipping this reveal; the content stays at full width so it does not
            // reflow mid-slide.
            var inner = new VisualElement { name = "zep-settings-inner" };
            inner.AddToClassList(ZepUiClasses.SettingsInner);
            Add(inner);

            inner.Add(BuildHeader());

            var body = new VisualElement { name = "zep-settings-body" };
            body.AddToClassList(ZepUiClasses.SettingsBody);
            inner.Add(body);

            var nav = new VisualElement { name = "zep-settings-nav" };
            nav.AddToClassList(ZepUiClasses.SettingsNav);
            body.Add(nav);

            var content = new VisualElement { name = "zep-settings-content" };
            content.AddToClassList(ZepUiClasses.SettingsContent);
            body.Add(content);

            // Theme section.
            var themePanel = BuildSectionPanel("Theme");
            _themeControls = new VisualElement { name = "zep-settings-theme-controls" };
            themePanel.Add(_themeControls);
            var preview = new VisualElement { name = "zep-settings-theme-preview" };
            preview.AddToClassList(ZepUiClasses.SettingsThemePreview);
            var previewTitle = new Label("Theme preview");
            previewTitle.AddToClassList(ZepUiClasses.SectionTitle);
            preview.Add(previewTitle);
            var previewText = new Label("Text and background in your workspace.");
            previewText.AddToClassList(ZepUiClasses.SettingsThemePreviewText);
            preview.Add(previewText);
            var previewAccent = new Label("Selected item");
            previewAccent.AddToClassList(ZepUiClasses.SettingsThemePreviewAccent);
            preview.Add(previewAccent);
            themePanel.Add(preview);
            _themeManagementList = new VisualElement { name = "zep-theme-actions" };
            _themeManagementList.AddToClassList(ZepUiClasses.SettingsThemeActions);
            themePanel.Add(_themeManagementList);

            // Finder Tags section.
            var tagsPanel = BuildSectionPanel("Finder Tags");
            var tagEditorScroll = new ScrollView(ScrollViewMode.Vertical)
            {
                name = "zep-settings-tags-editor-scroll"
            };
            tagEditorScroll.AddToClassList(ZepUiClasses.SettingsTagsEditorScroll);
            _tagEditor = new VisualElement { name = "zep-settings-tags-editor" };
            _tagEditor.AddToClassList(ZepUiClasses.SettingsImGui);
            tagEditorScroll.Add(_tagEditor);
            tagsPanel.Add(tagEditorScroll);

            // Compression section.
            var compressionPanel = BuildSectionPanel("Compression");
            compressionPanel.Add(BuildCompressionSection());

            AddSection(nav, content, "Theme", themePanel);
            _tagsSectionIndex = AddSection(nav, content, "Finder Tags", tagsPanel);
            AddSection(nav, content, "Compression", compressionPanel);
            var shortcutsPanel = BuildSectionPanel("Shortcuts");
            shortcutsPanel.Add(BuildShortcutsSection());
            AddSection(nav, content, "Shortcuts", shortcutsPanel);
            SelectSection(0);

            RebuildThemeDropdown();
            RebuildThemeManagementList();
            RebuildTagEditor();
            RebuildCompressionToggles();
            OnShortcutsChanged();

            // Subscribe on attach, not just in the ctor: the drawer re-parents with
            // the shell on dock/undock (detach -> attach, no rebuild), so a
            // ctor-only subscribe is dropped on the first detach and the drawer then
            // ignores model changes (stale themes/tags) until a domain reload.
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                _model.ThemesChanged += OnThemesChanged;
                _model.TagsChanged += OnTagsChanged;
                _model.CompressionChanged += OnCompressionChanged;
                _model.ShortcutsChanged += OnShortcutsChanged;
                OnModelChanged();
            });
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
        }

        // The drawer stays mounted at width 0 and animates open via USS
        // (transition on width); toggling display would defeat the transition.
        public void SetOpen(bool isOpen)
        {
            EnableInClassList(ZepUiClasses.SettingsPageOpen, isOpen);
        }

        // Deep-link for the "Manage Tags…" context-menu entry.
        public void ShowTagsSection() => SelectSection(_tagsSectionIndex);

        private VisualElement BuildHeader()
        {
            var header = new VisualElement { name = "zep-settings-header" };
            header.AddToClassList(ZepUiClasses.SettingsHeader);

            var title = new Label("Settings");
            title.AddToClassList(ZepUiClasses.SettingsTitle);
            header.Add(title);

            var close = new Button(() => CloseRequested?.Invoke())
            {
                text = "✕",
                name = "zep-settings-close-button",
                tooltip = "Close settings"
            };
            close.AddToClassList(ZepUiClasses.SettingsCloseButton);
            close.AddToClassList(ZepUiClasses.FlatButton);
            header.Add(close);
            return header;
        }

        private int AddSection(VisualElement nav, VisualElement content, string label, VisualElement panel)
        {
            var index = _sectionPanels.Count;

            var navButton = new Button(() => SelectSection(index)) { text = label };
            navButton.AddToClassList(ZepUiClasses.SettingsNavButton);
            navButton.AddToClassList(ZepUiClasses.FlatButton);
            nav.Add(navButton);
            _navButtons.Add(navButton);

            content.Add(panel);
            _sectionPanels.Add(panel);
            return index;
        }

        private void SelectSection(int index)
        {
            for (var i = 0; i < _sectionPanels.Count; i++)
            {
                var active = i == index;
                _sectionPanels[i].style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
                _navButtons[i].EnableInClassList(ZepUiClasses.SettingsNavButtonActive, active);
            }
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            _model.ThemesChanged -= OnThemesChanged;
            _model.TagsChanged -= OnTagsChanged;
            _model.CompressionChanged -= OnCompressionChanged;
            _model.ShortcutsChanged -= OnShortcutsChanged;
        }

        // Full resync, used when the drawer (re)attaches and the model may have moved
        // while it was detached. Model mutations take the per-section handlers below.
        private void OnModelChanged()
        {
            OnThemesChanged();
            OnTagsChanged();
            OnCompressionChanged();
            OnShortcutsChanged();
        }

        private VisualElement BuildShortcutsSection()
        {
            var section = new VisualElement { name = "zep-settings-shortcuts" };
            var title = new Label("Open Search");
            title.AddToClassList(ZepUiClasses.SectionTitle);
            section.Add(title);
            _searchShortcutStatus = new Label { name = "zep-search-shortcut-status" };
            _searchShortcutStatus.AddToClassList(ZepUiClasses.SettingsShortcutHint);
            section.Add(_searchShortcutStatus);

            _searchModifiers = new PopupField<ShortcutModifiers>
            {
                name = "zep-search-shortcut-modifiers",
                formatSelectedValueCallback = ZepSearchShortcut.FormatModifiers,
                formatListItemCallback = ZepSearchShortcut.FormatModifiers,
                tooltip = "Modifier keys"
            };
            _searchModifiers.AddToClassList(ZepUiClasses.SettingsShortcutField);
            _searchModifiers.RegisterValueChangedCallback(_ => RefreshSearchKeys());
            section.Add(_searchModifiers);
            _searchKey = new PopupField<KeyCode>
            {
                name = "zep-search-shortcut-key",
                formatSelectedValueCallback = key => key == KeyCode.None ? "No available keys" : ZepSearchShortcut.FormatKey(key),
                formatListItemCallback = ZepSearchShortcut.FormatKey,
                tooltip = "Available key — combinations already used by Unity are omitted"
            };
            _searchKey.AddToClassList(ZepUiClasses.SettingsShortcutField);
            _searchKey.RegisterValueChangedCallback(_ => RefreshShortcutApplyButton());
            section.Add(_searchKey);

            var actions = new VisualElement();
            actions.AddToClassList(ZepUiClasses.SettingsActions);
            _applySearchShortcut = new Button(() => SearchShortcutSelected?.Invoke(
                new KeyCombination(_searchKey.value, _searchModifiers.value)))
                { text = "Use shortcut", name = "zep-search-shortcut-apply" };
            _applySearchShortcut.AddToClassList(ZepUiClasses.ActionButton);
            _applySearchShortcut.AddToClassList(ZepUiClasses.ActionButtonPrimary);
            actions.Add(_applySearchShortcut);
            _resetSearchShortcut = new Button(() => SearchShortcutSelected?.Invoke(default))
                { text = "Reset", name = "zep-search-shortcut-reset", tooltip = "Restore Double Shift" };
            _resetSearchShortcut.AddToClassList(ZepUiClasses.ActionButton);
            _resetSearchShortcut.AddToClassList(ZepUiClasses.ActionButtonSecondary);
            actions.Add(_resetSearchShortcut);
            section.Add(actions);
            var hint = new Label("Only available combinations are listed. Saved for your editor. Reset restores Double Shift.");
            hint.AddToClassList(ZepUiClasses.SettingsShortcutHint);
            section.Add(hint);
            return section;
        }

        private void OnShortcutsChanged()
        {
            var current = _model.SearchShortcut;
            _searchShortcutStatus.text = "Current: " + ZepSearchShortcut.Format(current);
            if (_model.SearchShortcutUnavailableReason != null)
                _searchShortcutStatus.text += "\nUnavailable: " + _model.SearchShortcutUnavailableReason;
            var modifiers = _model.SearchShortcutOptions.Select(option => option.modifiers).Distinct().ToList();
            _searchModifiers.choices = modifiers;
            _searchModifiers.SetEnabled(modifiers.Count > 0);
            _searchModifiers.SetValueWithoutNotify(modifiers.Contains(current.modifiers)
                ? current.modifiers : modifiers.FirstOrDefault());
            _searchKey.SetValueWithoutNotify(current.keyCode);
            _resetSearchShortcut.SetEnabled(!current.Equals(default(KeyCombination)));
            RefreshSearchKeys();
        }

        private void RefreshSearchKeys()
        {
            var keys = _model.SearchShortcutOptions.Where(option => option.modifiers == _searchModifiers.value)
                .Select(option => option.keyCode).ToList();
            _searchKey.choices = keys;
            _searchKey.SetEnabled(keys.Count > 0);
            if (!keys.Contains(_searchKey.value)) _searchKey.SetValueWithoutNotify(keys.FirstOrDefault());
            RefreshShortcutApplyButton();
        }

        private void RefreshShortcutApplyButton()
        {
            var candidate = new KeyCombination(_searchKey.value, _searchModifiers.value);
            _applySearchShortcut.SetEnabled(_model.SearchShortcutOptions.Contains(candidate)
                && !candidate.Equals(_model.SearchShortcut));
        }

        private void OnThemesChanged()
        {
            _isApplyingModelState = true;
            RebuildThemeDropdown();
            RebuildThemeManagementList();
            _isApplyingModelState = false;
        }

        private void OnTagsChanged()
        {
            RebuildTagEditor();
        }

        private void OnCompressionChanged()
        {
            _isApplyingModelState = true;
            RebuildCompressionToggles();
            _isApplyingModelState = false;
        }

        private void RebuildThemeManagementList()
        {
            _themeManagementList.Clear();

            var create = new Button(() => CreateThemeRequested?.Invoke()) { text = "Create theme\u2026" };
            create.AddToClassList(ZepUiClasses.ActionButton);
            create.AddToClassList(ZepUiClasses.ActionButtonSecondary);
            create.tooltip = "Open the theme creator with default seeds.";
            _themeManagementList.Add(create);

            var current = FindCurrentThemeOption();
            if (current == null || current.Source == ZepThemeSource.Builtin) return;

            var edit = new Button(() => EditThemeRequested?.Invoke(current.Id)) { text = "Edit current" };
            edit.AddToClassList(ZepUiClasses.ActionButton);
            edit.AddToClassList(ZepUiClasses.ActionButtonSecondary);
            edit.tooltip = $"Edit {current.DisplayName}.";
            _themeManagementList.Add(edit);

            var del = new Button(() => DeleteThemeRequested?.Invoke(current)) { text = "Delete" };
            del.AddToClassList(ZepUiClasses.ActionButton);
            del.AddToClassList(ZepUiClasses.ActionButtonDanger);
            del.tooltip = $"Delete {current.DisplayName}.";
            _themeManagementList.Add(del);
        }

        private ZepSettingsThemeOption FindCurrentThemeOption()
        {
            var currentId = _model.CurrentThemeId;
            if (string.IsNullOrEmpty(currentId)) return null;
            var details = _model.ThemeOptionDetails;
            if (details == null) return null;
            for (var i = 0; i < details.Count; i++)
            {
                if (string.Equals(details[i].Id, currentId, StringComparison.Ordinal))
                    return details[i];
            }
            return null;
        }

        private void RebuildThemeDropdown()
        {
            RebuildThemeDropdownCountForTests++;
            var options = new List<string>(_model.ThemeOptions);
            var current = _model.CurrentThemeId ?? string.Empty;
            if (!options.Contains(current))
                current = options.Count > 0 ? options[0] : string.Empty;

            if (_themeDropdown == null)
            {
                _themeDropdown = new PopupField<string>(options, current) { name = "zep-theme-dropdown" };
                // The option VALUES stay theme ids (OnThemeDropdownChanged forwards the id
                // to _applyTheme), but the user sees the friendly DisplayName instead of a
                // raw id like "ZepDark" or "user-my-theme".
                _themeDropdown.formatSelectedValueCallback = FormatThemeDisplayName;
                _themeDropdown.formatListItemCallback = FormatThemeDisplayName;
                _themeDropdown.AddToClassList(ZepUiClasses.ThemeToggle);
                _themeDropdown.tooltip = "Change Zep theme.";
                _themeDropdown.RegisterValueChangedCallback(OnThemeDropdownChanged);
                _themeControls.Add(_themeDropdown);
                return;
            }

            _themeDropdown.choices = options;
            if (!string.Equals(_themeDropdown.value, current, StringComparison.Ordinal))
            {
                _themeDropdown.SetValueWithoutNotify(current);
            }
        }

        // Maps a theme id to its DisplayName for the dropdown's label/list rendering,
        // falling back to the id when no matching option detail is known.
        private string FormatThemeDisplayName(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            var details = _model.ThemeOptionDetails;
            if (details != null)
            {
                for (var i = 0; i < details.Count; i++)
                {
                    if (string.Equals(details[i].Id, id, StringComparison.Ordinal))
                        return details[i].DisplayName;
                }
            }
            return id;
        }

        private void OnThemeDropdownChanged(ChangeEvent<string> evt)
        {
            if (_isApplyingModelState)
            {
                return;
            }

            if (string.IsNullOrEmpty(evt.newValue))
            {
                return;
            }

            ThemeSelected?.Invoke(evt.newValue);
        }

        private void RebuildTagEditor()
        {
            RebuildTagEditorCountForTests++;
            _tagEditor.Clear();

            var tags = _model.Tags;
            if (tags.Count == 0)
            {
                var emptyLabel = new Label("No tags defined yet.");
                emptyLabel.AddToClassList(ZepUiClasses.SettingsEmptyLabel);
                _tagEditor.Add(emptyLabel);
            }
            else
            {
                for (var i = 0; i < tags.Count; i++)
                {
                    _tagEditor.Add(CreateTagEditorRow(tags[i]));
                }
            }

            var actions = new VisualElement { name = "zep-settings-tags-actions" };
            actions.AddToClassList(ZepUiClasses.SettingsActions);
            var addButton = new Button { text = "Add Tag" };
            addButton.AddToClassList(ZepUiClasses.ActionButton);
            addButton.AddToClassList(ZepUiClasses.ActionButtonSecondary);
            addButton.clicked += () => AddTagRequested?.Invoke();
            var resetButton = new Button { text = "Reset Defaults" };
            resetButton.AddToClassList(ZepUiClasses.ActionButton);
            resetButton.AddToClassList(ZepUiClasses.ActionButtonSecondary);
            resetButton.clicked += () => ResetTagsRequested?.Invoke();
            actions.Add(addButton);
            actions.Add(resetButton);
            _tagEditor.Add(actions);
        }

        private VisualElement CreateTagEditorRow(ZepTag tag)
        {
            var row = new VisualElement { name = $"zep-settings-tag-row-{tag.Id}" };
            row.AddToClassList(ZepUiClasses.SettingsTagRow);

            TextField nameField = null;

            var swatch = new VisualElement
            {
                name = $"zep-settings-tag-swatch-{tag.Id}",
                tooltip = "Change color",
                focusable = true
            };
            swatch.AddToClassList(ZepUiClasses.SettingsTagSwatch);
            ZepTagColorPalette.ApplyBackgroundColor(swatch, tag.Color);
            swatch.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0)
                {
                    return;
                }

                ContextMenuPopupRenderer.Show(
                    swatch,
                    BuildTagColorMenuItems(tag, swatch, () => nameField?.value ?? tag.Name),
                    null,
                    swatch.worldBound);
                evt.StopPropagation();
            });
            row.Add(swatch);

            nameField = new TextField
            {
                value = tag.Name,
                name = $"zep-settings-tag-name-{tag.Id}"
            };
            nameField.AddToClassList(ZepUiClasses.SettingsTagNameField);
            nameField.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (!string.Equals(nameField.value, tag.Name, StringComparison.Ordinal))
                {
                    CommitTagName(row, nameField, tag);
                }
            });
            nameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    CommitTagName(row, nameField, tag);
                }
            });
            row.Add(nameField);

            var favoriteButton = new Button
            {
                text = tag.IsFavorite ? "★" : "☆",
                name = $"zep-settings-tag-favorite-{tag.Id}",
                tooltip = tag.IsFavorite ? "Remove from favorites" : "Add to favorites"
            };
            favoriteButton.AddToClassList(ZepUiClasses.SettingsTagFavoriteButton);
            favoriteButton.AddToClassList(ZepUiClasses.FlatButton);
            if (tag.IsFavorite)
            {
                favoriteButton.AddToClassList(ZepUiClasses.SettingsTagFavoriteButtonActive);
            }

            favoriteButton.clicked += () =>
            {
                TagDefinitionUpdated?.Invoke(new ZepTag(
                    tag.Id,
                    nameField?.value ?? tag.Name,
                    tag.Color,
                    !tag.IsFavorite));
            };
            row.Add(favoriteButton);

            var removeButton = new Button(() => TagDefinitionRemoved?.Invoke(tag.Id))
            {
                text = "✕",
                name = $"zep-settings-tag-remove-{tag.Id}",
                tooltip = "Remove tag"
            };
            removeButton.AddToClassList(ZepUiClasses.SettingsTagRemoveButton);
            removeButton.AddToClassList(ZepUiClasses.FlatButton);
            row.Add(removeButton);

            return row;
        }

        // A rename that the controller refuses (a blank name, or one another tag
        // already uses) changes nothing, so the tag state it pushes back is
        // value-equal to the one the model already holds: no TagsChanged, no
        // rebuild, and the rejected text used to sit in the row looking accepted
        // until the drawer was reopened. The row reads its own name back from the
        // model — the stored tags — and puts it back. A rename that stuck rebuilt
        // the tag editor, which detaches this row; then there is nothing to revert.
        private void CommitTagName(VisualElement row, TextField nameField, ZepTag tag)
        {
            TagDefinitionUpdated?.Invoke(new ZepTag(
                tag.Id,
                nameField.value,
                tag.Color,
                tag.IsFavorite));

            if (row.parent == null)
            {
                return;
            }

            var tags = _model.Tags;
            for (var i = 0; i < tags.Count; i++)
            {
                if (!string.Equals(tags[i].Id, tag.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.Equals(nameField.value, tags[i].Name, StringComparison.Ordinal))
                {
                    nameField.SetValueWithoutNotify(tags[i].Name);
                }

                return;
            }
        }

        // The swatch shows the panel's own themed menu, like every other menu the
        // panel opens: GenericMenu drops the colour cue the choice is actually made
        // by, so its items read as seven identical rows of text. Internal because
        // the proof reads the item set directly — with no live panel window to
        // borrow a theme from, the renderer refuses to open a popup at all.
        internal IReadOnlyList<ZepContextMenuItem> BuildTagColorMenuItems(
            ZepTag tag,
            VisualElement swatch,
            Func<string> currentName)
        {
            var items = new List<ZepContextMenuItem>();
            foreach (ZepTagColor color in Enum.GetValues(typeof(ZepTagColor)))
            {
                if (color == ZepTagColor.None)
                {
                    continue;
                }

                var captured = color;
                items.Add(new ZepContextMenuItem(
                    "Zep/Tags/Color/" + captured,
                    "Zep/Tags/Color/" + captured,
                    captured.ToString(),
                    string.Empty,
                    true,
                    captured == tag.Color,
                    false,
                    Array.Empty<ZepContextMenuItem>(),
                    iconTagColor: captured,
                    onClick: () =>
                    {
                        if (captured == tag.Color)
                        {
                            return;
                        }

                        TagDefinitionUpdated?.Invoke(new ZepTag(
                            tag.Id,
                            currentName(),
                            captured,
                            tag.IsFavorite));
                    }));
            }

            return items;
        }

        private VisualElement BuildCompressionSection()
        {
            var container = new VisualElement { name = "zep-settings-compression" };

            _includeMetaToggle = new Toggle
            {
                text = "Include .meta files in archives",
                name = "zep-settings-include-meta-toggle",
                tooltip = "Default for the compress prompt. When on, .zip archives keep sibling/nested .meta files so they re-import into Unity with guids and import settings intact."
            };
            _includeMetaToggle.AddToClassList(ZepUiClasses.SettingsToggle);
            _includeMetaToggle.RegisterValueChangedCallback(evt =>
            {
                if (_isApplyingModelState)
                {
                    return;
                }

                IncludeMetaInArchiveChanged?.Invoke(evt.newValue);
            });
            container.Add(_includeMetaToggle);

            _promptForMetaToggle = new Toggle
            {
                text = "Ask about .meta files when compressing",
                name = "zep-settings-prompt-meta-toggle",
                tooltip = "When off, compressing skips the prompt and always uses the setting above."
            };
            _promptForMetaToggle.AddToClassList(ZepUiClasses.SettingsToggle);
            _promptForMetaToggle.RegisterValueChangedCallback(evt =>
            {
                if (_isApplyingModelState)
                {
                    return;
                }

                PromptForMetaOnCompressChanged?.Invoke(evt.newValue);
            });
            container.Add(_promptForMetaToggle);

            // The editor's checkbox texture includes its skin-colored box. Render
            // the tick separately so USS can own both surfaces in every theme.
            foreach (var toggle in new[] { _includeMetaToggle, _promptForMetaToggle })
            {
                var tick = new Label("✓") { pickingMode = PickingMode.Ignore };
                tick.AddToClassList(ZepUiClasses.SettingsToggleTick);
                toggle.Q(className: "unity-toggle__checkmark").Add(tick);
            }

            return container;
        }

        private void RebuildCompressionToggles()
        {
            _includeMetaToggle?.SetValueWithoutNotify(_model.IncludeMetaInArchive);
            _promptForMetaToggle?.SetValueWithoutNotify(_model.PromptForMetaOnCompress);
        }

        // A content-pane section: a header title followed by that section's
        // controls (added by the caller). Only one is shown at a time — the nav
        // tabs above switch between them.
        private static VisualElement BuildSectionPanel(string title)
        {
            var slug = title.ToLowerInvariant().Replace(" ", "-");
            var panel = new VisualElement { name = $"zep-settings-section-{slug}" };

            var header = new VisualElement { name = $"zep-settings-header-{slug}" };
            header.AddToClassList(ZepUiClasses.SettingsSectionHeader);
            var label = new Label(title);
            label.AddToClassList(ZepUiClasses.SectionTitle);
            header.Add(label);
            panel.Add(header);

            return panel;
        }


    }
}
