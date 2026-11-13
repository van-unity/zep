using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.UI.AssetsArea.Substrate;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.UIElements;
using static Zep.Core.State.ZepSelectionPredicates;

namespace Zep.Editor.UI
{
    internal interface IZepShortcutHost
    {
        bool IsPromptOverlayVisible { get; }
        void HidePromptOverlay();
        // True while the settings drawer or theme-creator modal is open. Those own
        // the keyboard: browser chords must not fire behind them, and Escape closes
        // the top overlay via CloseOverlay.
        bool IsOverlayActive { get; }
        bool IsSearchOpen { get; }
        void CloseOverlay();
        // Lets the overlay guard still route Space to close a Quick Look that was
        // opened before the overlay (QuickLookToggle self-guards to close-only).
        bool IsQuickLookOpen { get; }
        IReadOnlyList<ZepAssetEntry> SelectedAssetEntries { get; }
        ZepFolderEntry SelectedFolderEntry { get; }
        ZepController CommandHandler { get; }
        IReadOnlyList<ZepTagFilterOption> TagFilterOptions { get; }
        void AnimateTagHighlight(string tagId);
        void NavigateBack();
        void NavigateForward();
        void PromptCreateFolder();
        void DuplicateSelected();
        // Both return false when the panel declined the chord (the editor is
        // compiling or updating) so the key falls through to Unity's own
        // Edit > Undo instead of being swallowed by the panel.
        bool RedoRequested();
        bool UndoRequested();
        void PromptRenameSelected();
        void OpenSelected();
        // `target` is the element the chord was aimed at (the event target,
        // i.e. the focused element): the shell routes a delete aimed at the
        // sidebar folder tree to the tree's selected folder, everything else
        // to the asset selection.
        void DeleteSelected(IEventHandler target);
        void ToggleSidebar();
        void FocusSearch();
        void QuickLookToggle();
        void SelectAllAssets();
        // Abandons a live pointer gesture or inline edit in the active pane.
        void CancelInteractions();
        void NavigateToParentFolder();
        // Both return false when the assets area isn't the keyboard target
        // (sidebar focus, overlay page) so the key stays unconsumed.
        bool MoveAssetSelection(AssetSelectionMove move, bool extend);
        bool TypeToSelectAsset(string prefix);
    }

    internal sealed class ZepShortcutController
    {
        private readonly IZepShortcutHost _host;
        // Gesture timing and type-ahead share the editor clock; tests control time.
        private readonly Func<double> _now;
        private bool _shiftHeld;
        private double _shiftPressedAt;
        private double _lastShiftTapAt = double.NegativeInfinity;
        private const double DoubleShiftInterval = 0.3;

        public ZepShortcutController(
            IZepShortcutHost host,
            Func<double> nowSource = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _now = nowSource ?? (() => EditorApplication.timeSinceStartup);
        }

        internal void ResetDoubleShift()
        {
            _shiftHeld = false;
            _shiftPressedAt = double.NegativeInfinity;
            _lastShiftTapAt = double.NegativeInfinity;
        }

        internal void HandleKeyUp(KeyUpEvent evt)
        {
            if (evt.keyCode == KeyCode.LeftShift || evt.keyCode == KeyCode.RightShift)
                HandleModifierKeysChanged(evt.modifiers);
        }

        internal bool HandleModifierKeysChanged(EventModifiers modifiers)
        {
            if (!ZepSearchShortcut.UsesDoubleShift)
            {
                ResetDoubleShift();
                return false;
            }
            var shiftHeld = (modifiers & EventModifiers.Shift) != 0;
            if (_host.IsPromptOverlayVisible || _host.IsOverlayActive && !_host.IsSearchOpen
                || (modifiers & (EventModifiers.Control | EventModifiers.Command | EventModifiers.Alt)) != 0)
            {
                ResetDoubleShift();
                _shiftHeld = shiftHeld;
                return false;
            }

            // Key events and native modifier notifications can describe the same
            // transition. Count it once, and require a release between taps.
            if (_shiftHeld == shiftHeld) return false;
            _shiftHeld = shiftHeld;
            var now = _now();
            if (!shiftHeld)
            {
                _lastShiftTapAt = now - _shiftPressedAt <= DoubleShiftInterval
                    ? _shiftPressedAt
                    : double.NegativeInfinity;
                return false;
            }

            _shiftPressedAt = now;
            if (now - _lastShiftTapAt > DoubleShiftInterval) return false;
            _lastShiftTapAt = double.NegativeInfinity;
            _shiftPressedAt = double.NegativeInfinity;
            _host.FocusSearch();
            return true;
        }

        private bool TryHandleDoubleShift(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.LeftShift && evt.keyCode != KeyCode.RightShift)
            {
                ResetDoubleShift();
                _shiftHeld = evt.shiftKey;
                return false;
            }

            if (!HandleModifierKeysChanged(evt.modifiers | EventModifiers.Shift)) return false;
            evt.StopPropagation();
            return true;
        }

        public void HandleKeyDown(KeyDownEvent evt)
        {
            if (TryHandleDoubleShift(evt))
            {
                return;
            }

            if (_host.IsPromptOverlayVisible)
            {
                if (evt.keyCode == KeyCode.Escape)
                {
                    _host.HidePromptOverlay();
                    evt.StopPropagation();
                }

                return;
            }

            // Search owns all other keys, but its opening shortcut toggles it
            // closed, including from its query, type drawer and Quick Look.
            if (_host.IsSearchOpen)
            {
                if (ZepSearchShortcut.Matches(evt.keyCode, evt.modifiers, Application.platform))
                {
                    _host.FocusSearch();
                    evt.StopPropagation();
                }
                return;
            }

            // The settings drawer / theme-creator modal own the keyboard while
            // open: Escape closes the top overlay; every other key belongs to the
            // overlay's own controls (buttons, fields, sliders), so we neither run
            // a browser chord behind it nor StopPropagation — the focused control
            // still receives the key. Sits before the text-input check because the
            // overlay owns its text fields too (name/tag fields keep typing).
            if (_host.IsOverlayActive)
            {
                if (evt.keyCode == KeyCode.Escape)
                {
                    _host.CloseOverlay();
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Space && _host.IsQuickLookOpen)
                {
                    // Preserve "Space closes a Quick Look opened before the overlay";
                    // when none is open, fall through so a focused overlay button
                    // still gets the Space.
                    _host.QuickLookToggle();
                    evt.StopPropagation();
                }

                return;
            }

            // The configured Search shortcut also works from text inputs.
            // Overlay fields keep ownership through the guard above.
            if (ZepSearchShortcut.Matches(evt.keyCode, evt.modifiers, Application.platform))
            {
                _host.FocusSearch();
                evt.StopPropagation();
                return;
            }

            if (IsTextInputTarget(evt.target))
            {
                return;
            }

            var platform = UnityEngine.Application.platform;

            // Printable characters arrive on their own KeyDownEvent (keyCode
            // None, character set); type-ahead consumes those before any chord
            // matching. Every check below keys off keyCode, so chord handling
            // never sees character events.
            if (TryHandleTypeToSelect(evt))
            {
                evt.StopPropagation();
                return;
            }

            if (evt.actionKey && evt.keyCode == KeyCode.A)
            {
                _host.SelectAllAssets();
                evt.StopPropagation();
                return;
            }

            if (evt.actionKey && evt.keyCode == KeyCode.B)
            {
                _host.ToggleSidebar();
                evt.StopPropagation();
                return;
            }

            // Shift is deliberately not checked: Cmd/Ctrl+Shift+N is the
            // Finder/Explorer/GNOME "new folder" convention, and the panel's
            // historical Cmd+N stays as an alias.
            if (evt.actionKey && evt.keyCode == KeyCode.N)
            {
                _host.PromptCreateFolder();
                evt.StopPropagation();
                return;
            }

            if (evt.actionKey && evt.keyCode == KeyCode.D)
            {
                _host.DuplicateSelected();
                evt.StopPropagation();
                return;
            }

            if (IsRedoChord(platform, evt.keyCode, evt.actionKey, evt.shiftKey))
            {
                // Leave a declined chord unconsumed: swallowing it would stop
                // Unity's own Edit > Redo from running, so the user's keystroke
                // would do nothing at all rather than merely bypassing the panel.
                if (_host.RedoRequested())
                {
                    evt.StopPropagation();
                }

                return;
            }

            if (evt.actionKey && evt.keyCode == KeyCode.Z)
            {
                if (_host.UndoRequested())
                {
                    evt.StopPropagation();
                }

                return;
            }

            if (IsNavigateToParentChord(platform, evt.keyCode, evt.actionKey, evt.altKey))
            {
                _host.NavigateToParentFolder();
                evt.StopPropagation();
                return;
            }

            if (IsOpenSelectionChord(platform, evt.keyCode, evt.actionKey))
            {
                _host.OpenSelected();
                evt.StopPropagation();
                return;
            }

            if (IsHistoryBackChord(platform, evt.keyCode, evt.actionKey, evt.altKey))
            {
                _host.NavigateBack();
                evt.StopPropagation();
                return;
            }

            if (IsHistoryForwardChord(platform, evt.keyCode, evt.actionKey, evt.altKey))
            {
                _host.NavigateForward();
                evt.StopPropagation();
                return;
            }

            if (TryHandleFinderTagShortcut(evt))
            {
                evt.StopPropagation();
                return;
            }

            if (!evt.actionKey && !evt.altKey && !evt.ctrlKey
                && TryResolveSelectionMove(evt.keyCode, out var move))
            {
                // Left unconsumed when the host declines (sidebar focus, no
                // assets) so the key falls through to whoever owns it natively.
                if (_host.MoveAssetSelection(move, evt.shiftKey))
                {
                    evt.StopPropagation();
                }

                return;
            }

            switch (evt.keyCode)
            {
                case KeyCode.Escape:
                    if (_host.IsQuickLookOpen)
                    {
                        // Esc dismisses Quick Look (macOS QL convention), matching
                        // Space and the ✕ button. QuickLookToggle closes it when open.
                        _host.QuickLookToggle();
                        evt.StopPropagation();
                        break;
                    }

                    _typeAheadBuffer = string.Empty;
                    // A marquee under way has to give the selection back, the
                    // way it already does in a detached pane window; here
                    // Escape did nothing and the next release committed it.
                    _host.CancelInteractions();
                    break;
                case KeyCode.Space:
                    // Recovered seam: the reconstructed switch dropped the
                    // spacebar Quick Look case; QuickLookToggle had no caller.
                    if (IsTypeAheadActive())
                    {
                        // Mid type-ahead a space is text ("foo bar"), not Quick
                        // Look; the character event that follows appends it.
                        evt.StopPropagation();
                        break;
                    }

                    // Only BARE Space toggles Quick Look. Modified Space must fall
                    // through unconsumed — Shift+Space is Unity's "maximize window",
                    // and Cmd/Ctrl/Alt+Space belong to the OS or Unity.
                    if (evt.shiftKey || evt.actionKey || evt.altKey || evt.ctrlKey)
                    {
                        break;
                    }

                    _host.QuickLookToggle();
                    evt.StopPropagation();
                    break;
                case KeyCode.F2:
                    _host.PromptRenameSelected();
                    evt.StopPropagation();
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (ReturnBeginsRename(platform))
                    {
                        _host.PromptRenameSelected();
                    }
                    else
                    {
                        _host.OpenSelected();
                    }

                    evt.StopPropagation();
                    break;
                case KeyCode.Delete:
                case KeyCode.Backspace:

                    if (IsDeleteShortcut(platform, evt.keyCode, evt.actionKey))
                    {
                        _host.DeleteSelected(evt.target);
                        evt.StopPropagation();
                    }
                    break;
            }
        }

        // macOS delivers the Edit-menu accelerators (⌘A select all, ⌘D
        // duplicate, ⌘⌫ delete) to the focused window as IMGUI command events
        // — the native menu bar claims the chord before it can become a
        // KeyDownEvent — so the panel must speak both protocols. On
        // Windows/Linux the key event usually wins and these stay quiet.
        public void HandleValidateCommand(ValidateCommandEvent evt)
        {
            if (!ShouldHandleCommandEvent(evt.commandName, evt.target)) return;

            evt.StopPropagation();
        }

        public void HandleExecuteCommand(ExecuteCommandEvent evt)
        {
            if (!ShouldHandleCommandEvent(evt.commandName, evt.target)) return;

            switch (evt.commandName)
            {
                case "SelectAll":
                    _host.SelectAllAssets();
                    break;
                case "Duplicate":
                    _host.DuplicateSelected();
                    break;
                case "SoftDelete":
                case "Delete":

                    _host.DeleteSelected(evt.target);
                    break;
            }

            evt.StopPropagation();
        }

        // Focused text inputs own native editing commands.
        private bool ShouldHandleCommandEvent(string commandName, IEventHandler target)
        {
            if (_host.IsPromptOverlayVisible) return false;
            // macOS Edit-menu accelerators (⌘A/⌘D/⌘⌫) must not act on the browser
            // while an overlay owns the panel.
            if (_host.IsOverlayActive) return false;
            if (IsTextInputTarget(target)) return false;
            return IsHandledCommand(commandName);
        }

        // Focused text inputs own every key. Unity 6 TextFields delegate focus
        // to their inner TextElement, so key events target that inner element
        // — a bare `target is TextField` misses live text editing (typing
        // during inline rename was eaten by type-ahead). Walk up to the owning
        // TextField instead; this also covers the toolbar search field.
        // A HIDDEN field doesn't count: it can't be edited, so it must not
        // swallow the panel's keys (see IsStaleTextInputTarget).
        internal static bool IsTextInputTarget(IEventHandler target)
        {
            return TryGetTargetTextField(target, out var field) && IsShownInHierarchy(field);
        }

        // Log-verified UITK gotcha: after an inline edit's row is hidden, the
        // panel keeps dispatching key events to the hidden field's inner
        // TextElement (the stale focus LEAF) while focusController.focusedElement
        // reports the composite ListView — so no focus-out ever fires and the
        // ghost silently eats every key until the window is refocused. Detect
        // that shape so the shell can re-anchor focus.
        internal static bool IsStaleTextInputTarget(IEventHandler target)
        {
            return TryGetTargetTextField(target, out var field) && !IsShownInHierarchy(field);
        }

        private static bool TryGetTargetTextField(IEventHandler target, out TextField field)
        {
            switch (target)
            {
                case TextField textField:
                    field = textField;
                    return true;
                case VisualElement element:
                    field = element.GetFirstAncestorOfType<TextField>();
                    return field != null;
                default:
                    field = null;
                    return false;
            }
        }

        // Inline display stamping is how this panel hides rows/containers, so
        // the inline style is checked alongside the resolved style (which only
        // updates while attached to a panel).
        private static bool IsShownInHierarchy(VisualElement element)
        {
            for (var current = element; current != null; current = current.hierarchy.parent)
            {
                if (current.style.display == DisplayStyle.None) return false;
                if (current.panel != null && current.resolvedStyle.display == DisplayStyle.None) return false;
            }

            return true;
        }

        internal static bool IsHandledCommand(string commandName)
        {
            switch (commandName)
            {
                case "SelectAll":
                case "Duplicate":
                case "SoftDelete":
                case "Delete":
                    return true;
                default:
                    return false;
            }
        }

        // -----------------------------------------------------------------------
        // Type-to-select: Finder/Explorer/GNOME all jump the selection to the
        // first name matching what the user types; the buffer resets after a
        // second of silence.
        // -----------------------------------------------------------------------

        private const double TypeAheadResetSeconds = 1.0;
        private string _typeAheadBuffer = string.Empty;
        private double _typeAheadLastInputTime = double.NegativeInfinity;

        private bool IsTypeAheadActive()
        {
            return _typeAheadBuffer.Length > 0
                && _now() - _typeAheadLastInputTime <= TypeAheadResetSeconds;
        }

        private bool TryHandleTypeToSelect(KeyDownEvent evt)
        {
            if (evt.actionKey || evt.altKey || evt.ctrlKey) return false;

            var character = evt.character;
            if (character == '\0' || char.IsControl(character)) return false;
            // A lone space is Quick Look; it only becomes text once a
            // type-ahead is in flight.
            if (character == ' ' && !IsTypeAheadActive()) return false;

            var buffer = IsTypeAheadActive() ? _typeAheadBuffer + character : character.ToString();
            if (!_host.TypeToSelectAsset(buffer))
            {
                return false;
            }

            _typeAheadBuffer = buffer;
            _typeAheadLastInputTime = _now();
            return true;
        }

        // -----------------------------------------------------------------------
        // Platform conventions. Each divergent chord is a pure static taking
        // the platform explicitly — the test seam that lets a mac machine
        // exercise the Windows/Linux tables (mirrors IsDeleteShortcut, the
        // original of this pattern).
        // -----------------------------------------------------------------------

        internal static bool IsMacPlatform(RuntimePlatform platform)
        {
            return platform == RuntimePlatform.OSXEditor || platform == RuntimePlatform.OSXPlayer;
        }

        internal static bool IsReservedSearchBinding(KeyCombination binding, RuntimePlatform platform)
        {
            var key = binding.keyCode;
            var action = (binding.modifiers & ShortcutModifiers.Action) != 0;
            var alt = (binding.modifiers & ShortcutModifiers.Alt) != 0;
            var shift = (binding.modifiers & ShortcutModifiers.Shift) != 0;
            if (key == KeyCode.F2) return true;
            if (key == KeyCode.F10 && shift) return true;
            if (action && (key == KeyCode.A || key == KeyCode.B || key == KeyCode.N
                || key == KeyCode.D || key == KeyCode.Z)) return true;
            if (IsRedoChord(platform, key, action, shift)
                || IsNavigateToParentChord(platform, key, action, alt)
                || IsOpenSelectionChord(platform, key, action)
                || IsHistoryBackChord(platform, key, action, alt)
                || IsHistoryForwardChord(platform, key, action, alt)) return true;
            return IsFinderTagShortcutPlatform(platform) && binding.modifiers == ShortcutModifiers.Control
                && GetFinderTagShortcutIndex(key) >= 0;
        }

        internal static bool IsDeleteShortcut(RuntimePlatform platform, KeyCode keyCode, bool actionKey)
        {
            if (IsMacPlatform(platform))
            {
                return actionKey && (keyCode == KeyCode.Backspace || keyCode == KeyCode.Delete);
            }

            // Windows and Linux (Explorer, GNOME Files): plain Delete deletes.
            return !actionKey && keyCode == KeyCode.Delete;
        }

        // macOS convention (Finder, Unity's own Project window): Return begins
        // a rename and ⌘↓ opens. Windows/Linux: Enter opens, F2 renames.
        internal static bool ReturnBeginsRename(RuntimePlatform platform)
        {
            return IsMacPlatform(platform);
        }

        // ⌘O is left alone: Unity's File menu owns that accelerator globally.
        internal static bool IsOpenSelectionChord(RuntimePlatform platform, KeyCode keyCode, bool actionKey)
        {
            return IsMacPlatform(platform) && actionKey && keyCode == KeyCode.DownArrow;
        }

        // Go to parent folder: ⌘↑ on macOS (Finder), Alt+Up on Windows/Linux
        // (Explorer, GNOME Files).
        internal static bool IsNavigateToParentChord(RuntimePlatform platform, KeyCode keyCode, bool actionKey, bool altKey)
        {
            if (keyCode != KeyCode.UpArrow) return false;
            return IsMacPlatform(platform) ? actionKey && !altKey : altKey && !actionKey;
        }

        // History: Alt+←/→ everywhere (Explorer/GNOME; long-standing panel
        // binding on mac too), plus Finder's ⌘[ / ⌘] on macOS.
        internal static bool IsHistoryBackChord(RuntimePlatform platform, KeyCode keyCode, bool actionKey, bool altKey)
        {
            if (altKey && !actionKey && keyCode == KeyCode.LeftArrow) return true;
            return IsMacPlatform(platform) && actionKey && !altKey && keyCode == KeyCode.LeftBracket;
        }

        internal static bool IsHistoryForwardChord(RuntimePlatform platform, KeyCode keyCode, bool actionKey, bool altKey)
        {
            if (altKey && !actionKey && keyCode == KeyCode.RightArrow) return true;
            return IsMacPlatform(platform) && actionKey && !altKey && keyCode == KeyCode.RightBracket;
        }

        // Redo: Cmd/Ctrl+Shift+Z everywhere, plus Ctrl+Y on Windows/Linux.
        internal static bool IsRedoChord(RuntimePlatform platform, KeyCode keyCode, bool actionKey, bool shiftKey)
        {
            if (!actionKey) return false;
            if (keyCode == KeyCode.Z && shiftKey) return true;
            return !IsMacPlatform(platform) && keyCode == KeyCode.Y;
        }

        private static bool TryResolveSelectionMove(KeyCode keyCode, out AssetSelectionMove move)
        {
            switch (keyCode)
            {
                case KeyCode.UpArrow: move = AssetSelectionMove.Up; return true;
                case KeyCode.DownArrow: move = AssetSelectionMove.Down; return true;
                case KeyCode.LeftArrow: move = AssetSelectionMove.Left; return true;
                case KeyCode.RightArrow: move = AssetSelectionMove.Right; return true;
                case KeyCode.Home: move = AssetSelectionMove.First; return true;
                case KeyCode.End: move = AssetSelectionMove.Last; return true;
                case KeyCode.PageUp: move = AssetSelectionMove.PageUp; return true;
                case KeyCode.PageDown: move = AssetSelectionMove.PageDown; return true;
                default: move = AssetSelectionMove.Up; return false;
            }
        }

        internal bool TryHandleFinderTagShortcut(KeyDownEvent evt)
        {
            if (evt == null || !IsFinderTagShortcutPlatform(UnityEngine.Application.platform) || !evt.ctrlKey || evt.actionKey || evt.altKey || evt.shiftKey)
            {
                return false;
            }

            var shortcutIndex = GetFinderTagShortcutIndex(evt.keyCode);
            if (shortcutIndex < 0)
            {
                return false;
            }

            var commandHandler = _host.CommandHandler;
            var selectedAssets = _host.SelectedAssetEntries;
            var selectedPaths = selectedAssets
                .Where(static asset => asset != null && !string.IsNullOrWhiteSpace(asset.Path))
                .Select(static asset => asset.Path)
                .ToArray();
            if (selectedPaths.Length == 0 || !ZepSelectionPredicates.CanTagAllSelectedPaths(commandHandler, selectedPaths))
            {
                return false;
            }

            var assetPaths = Array.ConvertAll(selectedPaths, AssetPath.From);
            if (shortcutIndex == 0)
            {
                return commandHandler != null && commandHandler.ClearTags(assetPaths);
            }

            var favoriteTags = _host.TagFilterOptions
                .Where(static option => option.Tag.IsFavorite)
                .Take(7)
                .ToArray();
            var favoriteTagIndex = shortcutIndex - 1;
            if (favoriteTagIndex < 0 || favoriteTagIndex >= favoriteTags.Length)
            {
                return false;
            }

            var targetTag = favoriteTags[favoriteTagIndex].Tag;
            var allSelectedHaveTag = selectedAssets.All(asset =>
                asset.Tags.Any(tag => string.Equals(tag.Id, targetTag.Id, StringComparison.OrdinalIgnoreCase)));
            if (allSelectedHaveTag)
            {
                return commandHandler != null && commandHandler.RemoveTag(assetPaths, targetTag.Id);
            }

            var added = commandHandler != null && commandHandler.AddTag(assetPaths, targetTag.Id);
            if (added) _host.AnimateTagHighlight(targetTag.Id);
            return added;
        }

        // Internal (not private) so ContextMenuTagSubmenuBuilder can ask the same
        // question when deciding whether to advertise the ⌃0/⌃1…⌃7 hotkeys on a
        // tag-submenu row, instead of re-deriving this platform gate.
        internal static bool IsFinderTagShortcutPlatform(RuntimePlatform platform)
        {
            return platform == RuntimePlatform.OSXEditor || platform == RuntimePlatform.OSXPlayer;
        }

        private static int GetFinderTagShortcutIndex(KeyCode keyCode)
        {
            switch (keyCode)
            {
                case KeyCode.Alpha0:
                case KeyCode.Keypad0:
                    return 0;
                case KeyCode.Alpha1:
                case KeyCode.Keypad1:
                    return 1;
                case KeyCode.Alpha2:
                case KeyCode.Keypad2:
                    return 2;
                case KeyCode.Alpha3:
                case KeyCode.Keypad3:
                    return 3;
                case KeyCode.Alpha4:
                case KeyCode.Keypad4:
                    return 4;
                case KeyCode.Alpha5:
                case KeyCode.Keypad5:
                    return 5;
                case KeyCode.Alpha6:
                case KeyCode.Keypad6:
                    return 6;
                case KeyCode.Alpha7:
                case KeyCode.Keypad7:
                    return 7;
                default:
                    return -1;
            }
        }
    }
}
