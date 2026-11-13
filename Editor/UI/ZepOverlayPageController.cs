using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.Theming;
using Zep.Editor.Bridge.Settings;
using Zep.Editor.Bridge.Theming;
using Zep.Editor.UI.Theming;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    // Owns the lifecycle of the two overlay pages — settings + theme creator — that
    // sit on top of the main browser. Centralises the state machine (only one of the
    // two is active at a time) and the lazy mount of the theme-creator screen inside
    // the settings screen's parent. ShellView reads IsAnyOverlayActive /
    // IsSettingsPageActive to drive its top-bar / asset-pane visibility.
    internal sealed class ZepOverlayPageController
    {
        private readonly ZepController _commandHandler;
        private readonly IThemeSaveOrchestrator _themeSaveOrchestrator;
        private readonly ZepThemeRegistry _registry;
        private readonly ZepThemeSwitcher _themeSwitcher;
        private readonly Action<string, ZepToastKind> _showToast;
        private readonly Action _onCreateTagRequested;
        private readonly Action _onActiveChanged;
        // (title, message, confirm-button label) -> the user confirmed. A Func
        // rather than a direct call because EditorUtility.DisplayDialog cannot
        // be answered in a headless run, so "declining destroys nothing" has no
        // proof otherwise; the default below IS the real dialog. Same shape as
        // AssetDropOperations' askMoveConflict.
        private readonly Func<string, string, string, bool> _askConfirmDestructive;
        // The settings screen is now a left drawer at the workspace root, so its
        // parent is no longer the right column. The theme creator still fills the
        // right column, so the shell hands us that host explicitly.
        private readonly ZepShellView _themeCreatorHost;

        private ThemeCreatorScreenModel _themeCreatorModel;
        private ThemeCreatorScreen _themeCreatorScreen;

        public ZepSettingsScreenModel SettingsScreenModel { get; }
        public ZepSettingsScreen SettingsScreen { get; }

        public bool IsSettingsPageActive { get; private set; }
        public bool IsThemeCreatorPageActive { get; private set; }
        public bool IsAnyOverlayActive => IsSettingsPageActive || IsThemeCreatorPageActive;

        public ZepOverlayPageController(
            ZepController commandHandler,
            IThemeSaveOrchestrator themeSaveOrchestrator,
            ZepThemeRegistry registry,
            ZepThemeSwitcher themeSwitcher,
            Action<string, ZepToastKind> showToast,
            Action onCreateTagRequested,
            Action onActiveChanged,
            ZepShellView themeCreatorHost,
            Func<string, string, string, bool> askConfirmDestructive = null)
        {
            _commandHandler = commandHandler;
            _themeSaveOrchestrator = themeSaveOrchestrator;
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _themeSwitcher = themeSwitcher ?? throw new ArgumentNullException(nameof(themeSwitcher));
            _showToast = showToast ?? throw new ArgumentNullException(nameof(showToast));
            _onCreateTagRequested = onCreateTagRequested ?? throw new ArgumentNullException(nameof(onCreateTagRequested));
            _onActiveChanged = onActiveChanged ?? throw new ArgumentNullException(nameof(onActiveChanged));
            _askConfirmDestructive = askConfirmDestructive ?? AskConfirmWithDialog;
            _themeCreatorHost = themeCreatorHost ?? throw new ArgumentNullException(nameof(themeCreatorHost));

            SettingsScreenModel = new ZepSettingsScreenModel();
            // Pre-populate options + current id BEFORE creating the screen — its ctor
            // builds the theme dropdown from the model and crashes if the default
            // value isn't in the choice list.
            RefreshThemeOptionsFromRegistry();
            SettingsScreenModel.SetCurrentThemeId(_themeSwitcher.CurrentTheme);
            // Seed the compression toggles from persisted settings BEFORE the screen is
            // built (its ctor renders them from the model); otherwise they'd show the
            // model's C# field defaults, which disagree with the real persisted defaults.
            PushCompressionState();
            PushShortcutState();
            SettingsScreen = new ZepSettingsScreen(SettingsScreenModel);
            SettingsScreen.CloseRequested += Close;
            SettingsScreen.ThemeSelected += id => _themeSwitcher.SetTheme(id, true);
            SettingsScreen.AddTagRequested += () => _onCreateTagRequested();
            SettingsScreen.TagDefinitionUpdated += OnTagDefinitionUpdated;
            SettingsScreen.TagDefinitionRemoved += OnTagDefinitionRemoved;
            SettingsScreen.ResetTagsRequested += OnResetTagsRequested;
            SettingsScreen.CreateThemeRequested += OnCreateThemeRequested;
            SettingsScreen.EditThemeRequested += OnEditThemeRequested;
            SettingsScreen.DeleteThemeRequested += OnDeleteUserThemeRequested;
            SettingsScreen.IncludeMetaInArchiveChanged += OnIncludeMetaInArchiveChanged;
            SettingsScreen.PromptForMetaOnCompressChanged += OnPromptForMetaOnCompressChanged;
            SettingsScreen.SearchShortcutSelected += OnSearchShortcutSelected;
            SettingsScreen.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                ZepSearchShortcut.Changed += PushShortcutState;
                PushShortcutState();
            });
            SettingsScreen.RegisterCallback<DetachFromPanelEvent>(_ =>
                ZepSearchShortcut.Changed -= PushShortcutState);
        }

        public void OpenSettings()
        {
            PushShortcutState();
            PushTagsState();
            // Re-seed compression in case the compress prompt changed a value while the
            // drawer was closed (that flow reads/writes ZepSettings directly).
            PushCompressionState();
            IsSettingsPageActive = true;
            _onActiveChanged();
        }

        // Opens the drawer straight to the Finder Tags section (the "Manage Tags…"
        // context entry) instead of the default Theme section.
        public void OpenSettingsToTags()
        {
            SettingsScreen.ShowTagsSection();
            OpenSettings();
        }

        public void Close()
        {
            if (IsThemeCreatorPageActive)
            {
                SetThemeCreatorActive(false);
                return;
            }

            IsSettingsPageActive = false;
            _onActiveChanged();
        }

        // True when the element lives inside one of the overlay pages. The shell's
        // Escape-close path uses this to blur a focused field that would otherwise
        // wedge the keyboard: the drawer hides via width→0 (display stays Flex), so
        // a still-focused settings field keeps counting as a text-input target.
        public bool OwnsElement(VisualElement element)
        {
            for (var current = element; current != null; current = current.hierarchy.parent)
            {
                if (current == SettingsScreen || current == _themeCreatorScreen)
                {
                    return true;
                }
            }

            return false;
        }

        public void RefreshThemeOptionsFromRegistry()
        {
            var ids = new List<string>();
            foreach (var theme in _registry.AvailableThemes)
                ids.Add(theme.Id);
            var details = new List<ZepSettingsThemeOption>();
            foreach (var def in _registry.AvailableDefinitions)
                details.Add(new ZepSettingsThemeOption(def.Id, def.DisplayName, def.Source));
            // One push for the whole catalog: separate ids/details calls announced the
            // same registry refresh twice, rebuilding the theme section twice.
            SettingsScreenModel.SetThemeCatalog(ids, details);
        }

        public void SetCurrentThemeId(string themeId)
        {
            SettingsScreenModel?.SetCurrentThemeId(themeId);
        }

        public void PushTagsState()
        {
            SettingsScreenModel?.SetTags(_commandHandler?.GetSharedTags() ?? Array.Empty<ZepTag>());
        }

        private void PushShortcutState()
        {
            var binding = ZepSearchShortcut.Current;
            SettingsScreenModel.SetSearchShortcuts(ZepSearchShortcut.GetAvailableBindings(),
                binding, ZepSearchShortcut.GetUnavailableReason(binding));
        }

        private void OnSearchShortcutSelected(KeyCombination binding)
        {
            if (!ZepSearchShortcut.TrySet(binding, out var reason))
                _showToast(reason, ZepToastKind.Error);
            PushShortcutState();
        }

        // Mirrors the persisted ZepSettings into the settings-screen model so the
        // compression toggles show real state. Without this the toggles always reflected
        // the model's C# field defaults (include=off) while the effective default — and
        // what the compress prompt reads — is include=on.
        public void PushCompressionState()
        {
            var settings = ZepSettings.instance;
            if (settings == null)
            {
                return;
            }

            SettingsScreenModel?.SetIncludeMetaInArchive(settings.IncludeMetaFilesInArchive);
            SettingsScreenModel?.SetPromptForMetaOnCompress(settings.PromptForMetaOnCompress);
        }

        public void ApplySettingsScreenVisibility()
        {
            SettingsScreen?.SetOpen(IsSettingsPageActive);
        }

        private void OnTagDefinitionUpdated(ZepTag tag)
        {
            if (_commandHandler == null)
            {
                return;
            }

            if (_commandHandler.UpdateTagDefinition(tag))
            {
                PushTagsState();
                return;
            }

            // A refusal (blank name, or a name another tag already uses) only
            // reached the status bar, which drops an identical repeat — so the
            // rejected text sat in the row looking accepted. Say why, then
            // re-push so the drawer's tag state is the stored one again.
            var reason = _commandHandler.StatusMessage;
            _showToast(
                string.IsNullOrWhiteSpace(reason) ? "Tag could not be updated." : reason,
                ZepToastKind.Error);
            PushTagsState();
        }

        private void OnTagDefinitionRemoved(string tagId)
        {
            if (_commandHandler == null)
            {
                return;
            }

            // Removing a definition also strips that tag from every asset in the
            // project that carries it (the command filters the assignment store
            // by GUID), so this one-click ✕ asks first — like every other
            // destructive panel action, including Delete theme just below.
            if (!_askConfirmDestructive(
                    "Remove Tag",
                    $"Remove the tag “{GetTagDisplayName(tagId)}”?"
                    + "\n\nThis also removes it from every asset in the project that carries it.",
                    "Remove"))
            {
                return;
            }

            if (_commandHandler.DeleteTagDefinition(tagId))
            {
                PushTagsState();
            }
        }

        private string GetTagDisplayName(string tagId)
        {
            var tags = _commandHandler?.GetSharedTags();
            if (tags != null)
            {
                for (var i = 0; i < tags.Count; i++)
                {
                    if (string.Equals(tags[i].Id, tagId, StringComparison.OrdinalIgnoreCase))
                    {
                        return tags[i].Name;
                    }
                }
            }

            return tagId;
        }

        private static bool AskConfirmWithDialog(string title, string message, string confirmLabel)
        {
            return EditorUtility.DisplayDialog(title, message, confirmLabel, "Cancel");
        }

        private void OnIncludeMetaInArchiveChanged(bool includeMeta)
        {
            var settings = ZepSettings.instance;
            if (settings == null || settings.IncludeMetaFilesInArchive == includeMeta)
            {
                return;
            }

            settings.IncludeMetaFilesInArchive = includeMeta;
            settings.SaveSettings();
        }

        private void OnPromptForMetaOnCompressChanged(bool promptForMeta)
        {
            var settings = ZepSettings.instance;
            if (settings == null || settings.PromptForMetaOnCompress == promptForMeta)
            {
                return;
            }

            settings.PromptForMetaOnCompress = promptForMeta;
            settings.SaveSettings();
        }

        private void OnResetTagsRequested()
        {
            if (_commandHandler == null)
            {
                return;
            }

            // Reset replaces the whole vocabulary and drops every assignment that
            // the default set does not define, so it discards strictly more than
            // a single ✕ does — it asks first for the same reason.
            if (!_askConfirmDestructive(
                    "Reset Tags",
                    "Reset the tag list to the defaults?"
                    + "\n\nThis discards every tag you added — and removes those tags from every"
                    + " asset in the project that carries them — and puts the default tags back"
                    + " to their original names and colours.",
                    "Reset"))
            {
                return;
            }

            if (_commandHandler.ResetTagDefinitions())
            {
                PushTagsState();
            }
        }

        private void OnCreateThemeRequested()
        {
            if (!EnsureThemeCreatorScreen()) return;
            var activeMeta = _registry.GetTheme(_themeSwitcher.CurrentTheme);
            _themeCreatorModel.LoadForNew(activeMeta != null && activeMeta.IsLight);
            SetThemeCreatorActive(true);
        }

        private void OnEditThemeRequested(string themeId)
        {
            ZepThemeDefinition definition = null;
            foreach (var def in _registry.AvailableDefinitions)
            {
                if (string.Equals(def.Id, themeId, StringComparison.Ordinal))
                {
                    definition = def;
                    break;
                }
            }
            if (definition == null) return;
            if (!EnsureThemeCreatorScreen()) return;
            _themeCreatorModel.LoadForEdit(definition);
            SetThemeCreatorActive(true);
        }

        private void OnDeleteUserThemeRequested(ZepSettingsThemeOption option)
        {
            if (option == null || _themeSaveOrchestrator == null) return;
            if (!EditorUtility.DisplayDialog("Delete theme", $"Delete '{option.DisplayName}'?", "Delete", "Cancel"))
                return;
            _themeSaveOrchestrator.DeleteUserTheme(option.Id, option.Source);
        }

        private bool EnsureThemeCreatorScreen()
        {
            if (_themeCreatorScreen != null) return true;
            if (_themeSaveOrchestrator == null)
            {
                Debug.LogWarning("[Zep] Theme creator orchestrator is unavailable.");
                return false;
            }

            _themeCreatorModel = new ThemeCreatorScreenModel(_themeSaveOrchestrator);
            _themeCreatorScreen = new ThemeCreatorScreen(_themeCreatorModel);
            _themeCreatorScreen.SaveSucceeded += OnThemeSaveSucceeded;
            _themeCreatorScreen.SaveFailed += OnThemeSaveFailed;
            _themeCreatorScreen.CancelRequested += () => SetThemeCreatorActive(false);
            _themeCreatorHost.Add(_themeCreatorScreen);
            return true;
        }

        // The creator is a modal over the whole panel now — it floats above the
        // (still-open) settings drawer instead of taking over the right column, so
        // we leave IsSettingsPageActive alone and just show/hide the modal.
        private void SetThemeCreatorActive(bool active)
        {
            if (IsThemeCreatorPageActive == active) return;
            IsThemeCreatorPageActive = active;
            if (active)
                _themeCreatorScreen?.Show();
            else
                _themeCreatorScreen?.Hide();
            _onActiveChanged();
        }

        private void OnThemeSaveSucceeded(string themeId, string themeName)
        {
            _themeSwitcher.SetTheme(themeId, persistPreference: true);
            _showToast(
                string.IsNullOrWhiteSpace(themeName) ? "Theme saved" : $"Saved “{themeName}”",
                ZepToastKind.Success);
            SetThemeCreatorActive(false);
        }

        private void OnThemeSaveFailed(string error)
        {
            _showToast(
                string.IsNullOrWhiteSpace(error) ? "Save failed." : error,
                ZepToastKind.Error);
        }
    }
}
