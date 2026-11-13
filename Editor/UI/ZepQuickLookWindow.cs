using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.TopBar;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    // macOS-style Quick Look: a frameless floating panel toggled by spacebar.
    // It live-follows the active pane or the search result selection. The asset
    // panel keeps focus; Search hands focus to Quick Look and forwards arrows
    // back to its result selection so the preview stays above both popups.
    // Content per item: an interactive UnityEditor.Editor preview (orbitable
    // models/materials, playable animations/audio/video), the raw text for
    // code-like assets, or a stats-header + child-thumbnail grid for folders;
    // plus an info pane (size, dates, GUID, tags, type details, dependencies)
    // and a filmstrip for multi-selections.
    //
    // Styling: per styling-standards §1, a separate EditorWindow attaches
    // sheets at its own root — this window reuses the same three panel sheets
    // (structural + defaults + active theme via its own ThemeSwitcher) and
    // carries .zep-root/.zep-theme-<Id> so the token tables resolve.
    internal sealed class ZepQuickLookWindow : EditorWindow
    {
        private const string WidthPrefKey = "Zep.QuickLook.Width";
        private const string HeightPrefKey = "Zep.QuickLook.Height";
        private const float MinWindowWidth = 560f;
        private const float MinWindowHeight = 360f;
        private const float DefaultWindowWidth = 780f;
        private const float DefaultWindowHeight = 520f;

        // Folder stats walk is capped so Quick Look on Assets/ of a huge
        // project stays instant; the header shows "…+" when truncated.
        private const int MaxFolderWalkEntries = 25000;
        private const int MaxFolderGridTiles = 512;
        private const int MaxTextPreviewBytes = 200 * 1024;
        private const int MaxTextPreviewLines = 2000;
        private const int MaxDependencyRows = 12;
        // The filmstrip renders a WINDOW of tiles around the current item, never
        // the whole selection: each tile holds an Image the icon repository may
        // upgrade off-frame and Unity's preview cache is pinned at 512 entries,
        // so a strip holding more preview-able assets than that turned every
        // landed preview into an eviction plus a re-enqueue - a poll that never
        // settles while the window is open. The clipped ends show a "+N" count.
        private const int FilmstripWindow = 64;

        private static ZepQuickLookWindow _open;

        // Lets the shell's Space handler close an open Quick Look even while
        // an overlay (Settings, theme creator) owns the panel.
        internal static bool IsOpen => _open != null;

        // None of these survive a domain reload; OnEnable closes the reopened
        // husk instead of rendering without its wiring.
        private ZepShellView _shell;
        private ZepPaneWindow _paneWindow;
        private ZepState _viewModel;
        private SearchBarWidget _search;
        private EditorWindow _hostWindow;
        private ZepThemeSwitcher _themeSwitcher;

        private bool HasOwner => _shell != null || _paneWindow != null;
        private ZepController OwnerController =>
            _paneWindow != null ? _paneWindow.Controller : _shell?.Controller;
        private UnityZepIconRepository OwnerIcons =>
            _paneWindow != null ? _paneWindow.IconRepository : _shell?.IconRepository;
        private ZepPaneState PreviewPane =>
            _paneWindow != null ? _paneWindow.Pane : _shell?.LocalActivePane;
        private SubAssetDescriptor SelectedSubAsset =>
            _paneWindow != null ? _paneWindow.ActiveSubAssetSelection : _shell?.ActiveSubAssetSelection;
        private ZepFolderEntry SelectedSidebarFolder =>
            _shell != null && ReferenceEquals(PreviewPane, _viewModel.PrimaryPane)
                ? _shell.SelectedSidebarFolder : null;
        private VisualElement OwnerElement =>
            _paneWindow != null ? _paneWindow.rootVisualElement : _shell;

        private sealed class Item
        {
            public string Path;
            public string Name;
            public string TypeName;
            public bool IsFolder;
            // Present only for items sourced from the panel selection; drilled
            // children and the sidebar-folder fallback have no entry (and so
            // no tags in the info pane).
            public ZepAssetEntry Entry;
            // Present only for a committed sub-asset item. Path then holds the
            // PARENT asset's path (a sub-asset has no path of its own), so
            // identity must come from Key, never Path.
            public SubAssetDescriptor SubAsset;

            public string Key => SubAsset != null ? SubAsset.Key : Path;
        }

        private readonly List<Item> _items = new List<Item>();
        private int _index;
        // Clicking into a folder's contents grid or a dependency row pushes
        // onto this trail; Backspace / the ‹ button pops. Any panel selection
        // change clears it — live-follow wins over local exploration.
        private readonly List<Item> _drillTrail = new List<Item>();

        private UnityEditor.Editor _editor;
        private int _previewGeneration;
        private UnityEngine.Object _currentAsset;
        private GUIStyle _previewBackground;

        private VisualElement _root;
        private Image _headerIcon;
        private Label _title;
        private Label _subtitle;
        private Button _backButton;
        private Button _openButton;
        private VisualElement _previewZone;
        private IMGUIContainer _previewImgui;
        private ScrollView _infoScroll;
        private ScrollView _filmstrip;

        // Thumbnails that arrive asynchronously (UnityZepIconRepository
        // upgrades icons to rendered previews off-frame); PreviewsReady re-resolves.
        private readonly List<(Image image, string path, string typeName)> _folderThumbs =
            new List<(Image, string, string)>();
        private readonly List<(Image image, string path, string typeName)> _stripThumbs =
            new List<(Image, string, string)>();
        // The rendered filmstrip tiles, in item order starting at _stripFirst.
        // The strip also holds the "+N" labels of the clipped ends, so tile i is
        // not child i and the highlight cannot index the container's children.
        private readonly List<VisualElement> _stripTiles = new List<VisualElement>();
        private int _stripFirst;
        // The header icon and any fallback-preview icon also start as placeholder icons that
        // the repository upgrades to rendered previews off-frame; registering them here lets
        // OnPreviewsReady re-resolve them like the folder/filmstrip thumbs (they never
        // upgraded before, so a fresh material/prefab kept its generic icon in the header).
        private readonly List<(Image image, string path, string typeName)> _previewIconThumbs =
            new List<(Image, string, string)>();
        private readonly List<(Image image, string path, string typeName)> _dependencyThumbs =
            new List<(Image, string, string)>();

        private bool _updateHooked;
        private int _shellDetachedTicks;
        private bool _draggingWindow;
        private bool _dragScreenValid;
        private Vector2 _dragGrabOffset;
        private bool _resizing;
        private Vector2 _resizeStartPointer;
        private Vector2 _resizeStartSize;

        private static Font _monoFont;

        internal static bool IsOpenForSearch(SearchBarWidget search) => _open != null && _open._search == search;

        internal static bool CloseForSearch(SearchBarWidget search)
        {
            if (!IsOpenForSearch(search)) return false;
            _open.Close();
            return true;
        }

        internal static void Toggle(ZepShellView shell, EditorWindow hostWindow, SearchBarWidget search = null)
        {
            // Spacebar again while open = dismiss, regardless of selection.
            if (_open != null)
            {
                var sameSource = search == null || _open._search == search;
                _open.Close();
                if (sameSource) return;
            }

            if (shell == null || search != null && (search.HasPendingQuery
                || search.SelectedResult == null))
            {
                return;
            }

            var window = CreateInstance<ZepQuickLookWindow>();
            window._search = search;
            if (!window.Initialize(shell, hostWindow))
            {
                DestroyImmediate(window);
                return;
            }

            window.ShowPopup();
            // Search is itself a popup: focusing it here would raise it over
            // Quick Look. Its selection still follows Up/Down in this window.
            if (search != null)
            {
                window.Focus();
            }
            else if (hostWindow != null)
            {
                // The ordinary asset panel keeps navigating beneath Quick Look.
                hostWindow.Focus();
            }
        }

        private bool Initialize(ZepShellView shell, EditorWindow hostWindow)
        {
            _shell = shell;
            _viewModel = shell.ViewModel;
            _hostWindow = hostWindow;
            return InitializePreview();
        }

        internal static void Toggle(ZepPaneWindow hostWindow)
        {
            if (_open != null)
            {
                _open.Close();
                return;
            }

            if (hostWindow == null || hostWindow.Pane == null) return;
            var window = CreateInstance<ZepQuickLookWindow>();
            if (!window.InitializePaneWindow(hostWindow))
            {
                DestroyImmediate(window);
                return;
            }

            window.ShowPopup();
            hostWindow.Focus();
        }

        private bool InitializePaneWindow(ZepPaneWindow hostWindow)
        {
            _paneWindow = hostWindow;
            _viewModel = hostWindow.ViewModel;
            _hostWindow = hostWindow;
            return InitializePreview();
        }

        private bool InitializePreview()
        {
            var items = BuildItemsFromOwner();
            titleContent = new GUIContent("Quick Look");

            BuildUi();

            _items.AddRange(items);
            _index = 0;

            if (_paneWindow != null) _paneWindow.ThemeApplied += OnPanelThemeChanged;
            else _shell.ThemeApplied += OnPanelThemeChanged;
            OwnerIcons.PreviewsReady += OnPreviewsReady;
            EditorApplication.update += OnEditorUpdate;
            _updateHooked = true;

            var width = Mathf.Max(MinWindowWidth, EditorPrefs.GetFloat(WidthPrefKey, DefaultWindowWidth));
            var height = Mathf.Max(MinWindowHeight, EditorPrefs.GetFloat(HeightPrefKey, DefaultWindowHeight));
            position = CenterOver(_hostWindow, width, height);
            minSize = new Vector2(MinWindowWidth, MinWindowHeight);

            // Live-follow is wired whatever the window opens with: having
            // nothing selected yet is a DISPLAY state, not a lifecycle state.
            // Subscribing only for a non-empty open left a Quick Look opened
            // over an empty selection permanently blank — selecting an asset
            // did nothing until the user closed and reopened it. OnDisable
            // unhooks all three unconditionally, so the pairing still holds.
            if (_search != null)
            {
                _search.SelectedResultChanged += RefreshFromActivePane;
                _search.Closed += Close;
            }
            else
            {
                _viewModel.PaneChanged += OnPanelStateChanged;
                _viewModel.GlobalChanged += OnPanelGlobalChanged;
                // Sub-asset commits move Unity's Selection but not the pane's
                // path selection (it stays empty), so sub→sub and sub→cleared
                // transitions raise no PaneChanged — this is their live-follow
                // signal.
                Selection.selectionChanged += OnUnitySelectionChanged;
            }

            if (items.Count == 0)
            {
                _backButton.style.display = DisplayStyle.None;
                _title.text = "Quick Look";
                _subtitle.text = "Nothing to preview";
                ApplyEmptySelectionChrome(true);
                _filmstrip.style.display = DisplayStyle.None;
                AddEmptyLabel(_previewZone, "Select something in the panel to preview it");
                return true;
            }

            RebuildFilmstrip();
            ShowCurrent();
            return true;
        }

        private static Rect CenterOver(EditorWindow hostWindow, float width, float height)
        {
            var anchor = hostWindow != null
                ? hostWindow.position
                : new Rect(120f, 120f, 1280f, 800f);
            // No 0-clamp: desktop coordinates are legitimately negative on
            // monitors left of / above the primary display.
            var x = anchor.x + (anchor.width - width) * 0.5f;
            var y = anchor.y + (anchor.height - height) * 0.5f;
            return new Rect(x, y, width, height);
        }

        // Test-only: proves the key compare in TryFollowUnchangedSelection
        // short-circuits before a single Item is materialized (audit §5.21).
        internal static int BuildItemsFromSelectionCallsForTests;

        private static List<Item> BuildItemsFromSelection(ZepShellView shell)
        {
            return BuildSelectionItems(
                shell.LocalActivePane,
                shell.ActiveSubAssetSelection,
                ReferenceEquals(shell.LocalActivePane, shell.ViewModel.PrimaryPane)
                    ? shell.SelectedSidebarFolder : null,
                shell.Controller);
        }

        private List<Item> BuildItemsFromOwner()
        {
            if (_search != null) return BuildItemsFromSearch(_search);
            return _paneWindow != null
                ? BuildSelectionItems(PreviewPane, SelectedSubAsset, null, OwnerController)
                : BuildItemsFromSelection(_shell);
        }

        private static List<Item> BuildSelectionItems(
            ZepPaneState activePane,
            SubAssetDescriptor subAsset,
            ZepFolderEntry folder,
            ZepController controller)
        {
            BuildItemsFromSelectionCallsForTests++;
            var items = new List<Item>();
            var entries = activePane.GetSelectedAssetEntries();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.Path))
                {
                    continue;
                }

                items.Add(new Item
                {
                    Path = entry.Path,
                    Name = entry.Name,
                    TypeName = entry.TypeName,
                    IsFolder = entry.IsFolder,
                    Entry = entry,
                });
            }

            if (items.Count > 0)
            {
                return items;
            }

            // Empty path selection can mean a committed SUB-asset: committing
            // one clears the pane's path-keyed selection (sub-assets share
            // their parent's path and never enter it) and the pane view keeps
            // the descriptor instead. Previewing it here is what makes Space
            // on a sub-asset show the sub-object, not its container.
            if (subAsset != null)
            {
                items.Add(new Item
                {
                    Path = subAsset.ParentPath,
                    Name = subAsset.Name,
                    TypeName = subAsset.TypeName,
                    IsFolder = false,
                    SubAsset = subAsset,
                });
                return items;
            }

            if (folder != null
                && IndexedPathQueries.IsFolder(controller, folder.Path))
            {
                items.Add(new Item
                {
                    Path = folder.Path,
                    Name = folder.Name,
                    TypeName = "Folder",
                    IsFolder = true,
                });
            }

            return items;
        }

        private static List<Item> BuildItemsFromSearch(SearchBarWidget search)
        {
            var items = new List<Item>();
            var entry = search.HasPendingQuery ? null : search.SelectedResult;
            if (entry != null && !string.IsNullOrEmpty(entry.Path))
                items.Add(new Item
                {
                    Path = entry.Path,
                    Name = entry.Name,
                    TypeName = entry.TypeName,
                    IsFolder = entry.IsFolder,
                    Entry = entry,
                });
            return items;
        }

        private void OnEnable()
        {
            _open = this;
            if (!HasOwner)
            {
                // Reopened by the layout system after a domain reload: the
                // wiring (shell, view model, editor) did not survive. Close
                // instead of rendering a dead husk; Space reopens it fresh.
                EditorApplication.delayCall += () =>
                {
                    if (this != null && !HasOwner)
                    {
                        Close();
                    }
                };
            }
        }

        private void OnDisable()
        {
            _previewGeneration++;
            if (_open == this)
            {
                _open = null;
            }

            if (_search != null)
            {
                _search.SelectedResultChanged -= RefreshFromActivePane;
                _search.Closed -= Close;
            }
            Selection.selectionChanged -= OnUnitySelectionChanged;
            if (_viewModel != null)
            {
                _viewModel.PaneChanged -= OnPanelStateChanged;
                _viewModel.GlobalChanged -= OnPanelGlobalChanged;
            }

            if (_shell != null)
            {
                _shell.ThemeApplied -= OnPanelThemeChanged;
            }
            if (_paneWindow != null) _paneWindow.ThemeApplied -= OnPanelThemeChanged;
            if (OwnerIcons != null) OwnerIcons.PreviewsReady -= OnPreviewsReady;

            if (_updateHooked)
            {
                EditorApplication.update -= OnEditorUpdate;
                _updateHooked = false;
            }

            DestroyEditor();
            _dependencyThumbs.Clear();
            _themeSwitcher?.Teardown();
            _themeSwitcher = null;

            if (_root != null && position.width >= MinWindowWidth && position.height >= MinWindowHeight)
            {
                EditorPrefs.SetFloat(WidthPrefKey, position.width);
                EditorPrefs.SetFloat(HeightPrefKey, position.height);
            }

            // Hand keyboard control back to the panel so Space→Space→arrows
            // flows without a mouse click in between. Focusing only the host
            // window restores OS focus but leaves the panel's UI Toolkit focus
            // null, so the first key after a ✕/Esc close was NOT delivered to the
            // shell's shortcut handler — it took a throwaway Space to re-seat focus
            // (the "press Space twice to reopen" bug). Focus the shell element too
            // so the very next Space reopens Quick Look.
            // Query edits can close a stale search preview while the text field
            // already owns focus. Preserve that focus instead of interrupting typing.
            if (_hostWindow != null && (_search == null || _search.IsVisible && focusedWindow != _hostWindow))
            {
                _hostWindow.Focus();
                if (_search != null) _search.FocusResults();
                else OwnerElement?.Focus();
            }
        }

        private void OnLostFocus()
        {
            if (_search != null && _hostWindow is ZepSearchWindow searchWindow)
            {
                _shell.ShortcutController.ResetDoubleShift();
                EditorApplication.delayCall += searchWindow.CloseIfFocusLeft;
            }
        }

        private void ModifierKeysChanged()
        {
            if (focusedWindow == this && _search != null)
                _shell.ShortcutController.HandleModifierKeysChanged(
                    EditorGUIUtility.CommandEvent("ModifierKeysChanged").modifiers);
        }

        private void OnFocus()
        {
            // Frameless popups only deliver key events once something inside
            // the window owns UI Toolkit focus.
            _root?.Focus();
        }

        // ------------------------------------------------------------------
        // UI construction
        // ------------------------------------------------------------------

        private void BuildUi()
        {
            rootVisualElement.Clear();

            _root = new VisualElement { focusable = true };
            _root.AddToClassList(ZepUiClasses.Root);
            _root.AddToClassList(ZepUiClasses.QuickLookRoot);
            foreach (var sheetPath in new[]
                     {
                         ZepUiClasses.PanelStyleSheetPath,
                         ZepUiClasses.DefaultsStyleSheetPath,
                     })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(sheetPath);
                if (sheet != null)
                {
                    _root.styleSheets.Add(sheet);
                }
            }

            // Own switcher, same registry: mirrors the shell's active theme and
            // follows live changes via ThemeApplied. Never persists preference.
            var registry = _paneWindow != null ? _paneWindow.ThemeRegistry : _shell.Registry;
            var ownerTheme = _paneWindow != null ? _paneWindow.ThemeSwitcher : _shell.ThemeSwitcher;
            _themeSwitcher = new ZepThemeSwitcher(_root, registry, null, null);
            _themeSwitcher.SetTheme(ownerTheme.CurrentTheme, persistPreference: false);

            _root.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);
            if (_search != null)
            {
                _root.RegisterCallback<KeyUpEvent>(_shell.ShortcutController.HandleKeyUp, TrickleDown.TrickleDown);
                _root.RegisterCallback<PointerDownEvent>(_ => _shell.ShortcutController.ResetDoubleShift(), TrickleDown.TrickleDown);
            }
            rootVisualElement.Add(_root);

            var header = new VisualElement();
            header.AddToClassList(ZepUiClasses.QuickLookHeader);

            _backButton = new Button(DrillBack) { text = "‹", tooltip = "Back (Backspace)" };
            _backButton.AddToClassList(ZepUiClasses.ActionButton);
            _backButton.AddToClassList(ZepUiClasses.ActionButtonSecondary);
            _backButton.AddToClassList(ZepUiClasses.QuickLookBackButton);
            header.Add(_backButton);

            _headerIcon = new Image { scaleMode = ScaleMode.ScaleToFit };
            _headerIcon.AddToClassList(ZepUiClasses.QuickLookHeaderIcon);
            header.Add(_headerIcon);

            var headerText = new VisualElement();
            headerText.AddToClassList(ZepUiClasses.QuickLookHeaderText);
            _title = new Label();
            _title.AddToClassList(ZepUiClasses.QuickLookTitle);
            _subtitle = new Label();
            _subtitle.AddToClassList(ZepUiClasses.QuickLookSubtitle);
            headerText.Add(_title);
            headerText.Add(_subtitle);
            header.Add(headerText);

            _openButton = new Button(OpenCurrent) { text = "Open" };
            _openButton.AddToClassList(ZepUiClasses.ActionButton);
            _openButton.AddToClassList(ZepUiClasses.ActionButtonSecondary);
            header.Add(_openButton);

            var closeButton = new Button(Close) { text = "✕", tooltip = "Close (Space / Esc)" };
            closeButton.AddToClassList(ZepUiClasses.ActionButton);
            closeButton.AddToClassList(ZepUiClasses.ActionButtonSecondary);
            header.Add(closeButton);

            RegisterHeaderDrag(header);
            _root.Add(header);

            var body = new VisualElement();
            body.AddToClassList(ZepUiClasses.QuickLookBody);

            _previewZone = new VisualElement();
            _previewZone.AddToClassList(ZepUiClasses.QuickLookPreview);
            body.Add(_previewZone);

            _infoScroll = new ScrollView(ScrollViewMode.Vertical);
            _infoScroll.AddToClassList(ZepUiClasses.QuickLookInfoPane);
            body.Add(_infoScroll);

            _root.Add(body);

            _filmstrip = new ScrollView(ScrollViewMode.Horizontal);
            _filmstrip.AddToClassList(ZepUiClasses.QuickLookFilmstrip);
            _root.Add(_filmstrip);

            var grip = new VisualElement();
            grip.AddToClassList(ZepUiClasses.QuickLookResizeGrip);
            RegisterResizeGrip(grip);
            _root.Add(grip);
        }

        private void RegisterHeaderDrag(VisualElement header)
        {
            header.RegisterCallback<PointerDownEvent>(evt =>
            {
                // Buttons in the header keep their click; everything else drags.
                if (evt.button != 0 || evt.target is Button)
                {
                    return;
                }

                _draggingWindow = true;

                // Drag by anchoring the grabbed point to the DESKTOP cursor, not
                // to window-local pointer coords. evt.position is measured against
                // the window's *physical* origin, but EditorWindow.position only
                // updates its getter synchronously — macOS applies the real move a
                // frame or more later. The old code fed that lag back into itself
                // (position += local delta), so the window jittered and jumped.
                // Reading the cursor in desktop space takes the window origin out
                // of the math, so a deferred native move can't compound.
                //
                // GUIToScreenPoint needs a valid GUI context, which a UITK pointer
                // callback doesn't guarantee. Validate it once here: the window is
                // stationary at press, so the true desktop cursor is exactly
                // position.position + pointer. If GUIToScreenPoint agrees, trust it
                // for the drag; if not, fall back to the window-local estimate,
                // which reproduces the old behaviour (never worse).
                var pointer = (Vector2)evt.position;
                var screen = GUIUtility.GUIToScreenPoint(pointer);
                _dragScreenValid = (screen - (position.position + pointer)).sqrMagnitude < 64f;
                _dragGrabOffset = pointer;

                header.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });
            header.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!_draggingWindow || !header.HasPointerCapture(evt.pointerId))
                {
                    return;
                }

                // origin = desktopCursor - grabOffset keeps the grabbed point under
                // the cursor. grabOffset is fixed for the whole drag, so the target
                // never reads the window's own (possibly stale) current origin —
                // that independence is what removes the feedback jitter.
                var pointer = (Vector2)evt.position;
                var cursor = _dragScreenValid
                    ? GUIUtility.GUIToScreenPoint(pointer)
                    : position.position + pointer;
                var origin = cursor - _dragGrabOffset;
                if ((origin - position.position).sqrMagnitude > 0f)
                {
                    position = new Rect(origin, position.size);
                }
            });
            header.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (!_draggingWindow)
                {
                    return;
                }

                _draggingWindow = false;
                header.ReleasePointer(evt.pointerId);
            });
        }

        private void RegisterResizeGrip(VisualElement grip)
        {
            grip.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0)
                {
                    return;
                }

                _resizing = true;
                _resizeStartPointer = evt.position;
                _resizeStartSize = position.size;
                grip.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });
            grip.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!_resizing || !grip.HasPointerCapture(evt.pointerId))
                {
                    return;
                }

                var delta = (Vector2)evt.position - _resizeStartPointer;
                var size = new Vector2(
                    Mathf.Max(MinWindowWidth, _resizeStartSize.x + delta.x),
                    Mathf.Max(MinWindowHeight, _resizeStartSize.y + delta.y));
                position = new Rect(position.position, size);
            });
            grip.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (!_resizing)
                {
                    return;
                }

                _resizing = false;
                grip.ReleasePointer(evt.pointerId);
                EditorPrefs.SetFloat(WidthPrefKey, position.width);
                EditorPrefs.SetFloat(HeightPrefKey, position.height);
            });
        }

        // ------------------------------------------------------------------
        // Live-follow + input
        // ------------------------------------------------------------------

        private void OnPanelStateChanged(ZepPaneChange change)
        {
            // Mirror ShellView.RefreshView's inner-fire skip: while the
            // controller is reacting to a view-model change, the outer PaneChanged
            // dispatch is still in flight and will reach us again — acting on
            // the inner fire would rebuild from a half-applied projection. See
            // ZepController.IsReactingToViewModelChange for the why.
            if (OwnerController != null && OwnerController.IsReactingToViewModelChange)
            {
                return;
            }

            if (change.Kind == ZepPaneChangeKind.Updated
                && !ReferenceEquals(change.NewPane, PreviewPane))
            {
                return;
            }

            RefreshFromActivePane();
        }

        private void OnPanelGlobalChanged()
        {
            RefreshFromActivePane();
        }

        private void OnUnitySelectionChanged()
        {
            RefreshFromActivePane();
        }

        private void RefreshFromActivePane()
        {
            if (!HasOwner
                || _viewModel == null
                || PreviewPane == null
                || (OwnerController != null
                    && OwnerController.IsReactingToViewModelChange))
            {
                return;
            }

            // GlobalChanged fires per status-bar update and per search
            // keystroke, and PaneChanged fires for every pane update - the
            // selection behind Quick Look is unchanged on nearly all of them.
            // Compare the selection's KEYS against what is on screen before
            // materializing an Item per selected asset: a 2,000-asset selection
            // allocated 2,000 objects per status message just to answer
            // "did anything move?".
            if (TryFollowUnchangedSelection())
            {
                return;
            }

            var fresh = BuildItemsFromOwner();
            if (fresh.Count == 0)
            {
                // A selection that empties out dismisses the window — but a
                // window that OPENED empty has nothing to dismiss, and the
                // state changes that fire per keystroke and per status-bar
                // update would otherwise slam it shut before the user can
                // select anything.
                if (_items.Count > 0)
                {
                    Close();
                }

                return;
            }

            // Preserve the displayed item across selection-set changes
            // (Shift+Down extending a range, a filmstrip sibling deselected):
            // when it survives, skip the preview rebuild so playing audio or
            // an orbited model isn't reset — only the chrome around it moves.
            var displayedKey = _drillTrail.Count == 0 && _items.Count > 0
                ? _items[Mathf.Clamp(_index, 0, _items.Count - 1)].Key
                : null;
            _items.Clear();
            _items.AddRange(fresh);
            _drillTrail.Clear();

            var surviving = displayedKey != null
                ? _items.FindIndex(i => string.Equals(i.Key, displayedKey, StringComparison.Ordinal))
                : -1;
            _index = surviving >= 0 ? surviving : 0;
            RebuildFilmstrip();
            if (surviving >= 0)
            {
                _subtitle.text = BuildSubtitle(_items[_index]);
                RebuildInfoPane();
                UpdateFilmstripHighlight();
            }
            else
            {
                ShowCurrent();
            }
        }

        // True when the panel's selection still resolves to exactly the items on
        // screen, in order. Mirrors BuildItemsFromSelection's three sources
        // (previewable path selection, then the committed sub-asset, then the
        // sidebar folder) key by key, allocating nothing; any mismatch returns
        // false and the caller runs the full rebuild.
        //
        // Identity is the item KEY, not the path: an Item that carries a
        // SubAsset holds its PARENT's path, so a path compare would call
        // "sub of X" and "X selected" the same set and leave a stale parent
        // preview on screen (the sub-asset live-follow contract).
        private bool TryFollowUnchangedSelection()
        {
            if (_search != null)
            {
                var entry = _search.HasPendingQuery ? null : _search.SelectedResult;
                if (entry == null || string.IsNullOrEmpty(entry.Path)) return _items.Count == 0;
                if (_items.Count != 1 || !string.Equals(_items[0].Path, entry.Path, StringComparison.Ordinal)) return false;
                var tagsChanged = !SameTags(_items[0].Entry, entry);
                _items[0].Entry = entry;
                if (tagsChanged && _drillTrail.Count == 0) RebuildInfoPane();
                return true;
            }
            var activePane = PreviewPane;
            var entries = activePane.GetSelectedAssetEntries();
            var matched = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.Path))
                {
                    continue;
                }

                if (matched >= _items.Count
                    || _items[matched].SubAsset != null
                    || !string.Equals(_items[matched].Path, entry.Path, StringComparison.Ordinal))
                {
                    return false;
                }

                matched++;
            }

            if (matched > 0)
            {
                if (matched != _items.Count)
                {
                    return false;
                }

                // Same set — but tag edits also fire state changes, so refresh
                // the entry references and, only when tags actually differ, the
                // info pane. State changes also fire per search keystroke and
                // status-bar update; rebuilding the pane on those would churn
                // for no visible reason.
                var tagsChanged = false;
                var item = 0;
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry == null || string.IsNullOrEmpty(entry.Path))
                    {
                        continue;
                    }

                    tagsChanged |= !SameTags(_items[item].Entry, entry);
                    _items[item].Entry = entry;
                    item++;
                }

                if (tagsChanged && _drillTrail.Count == 0)
                {
                    RebuildInfoPane();
                }

                return true;
            }

            var subAsset = SelectedSubAsset;
            if (subAsset != null)
            {
                if (_items.Count != 1
                    || _items[0].SubAsset == null
                    || !string.Equals(_items[0].Key, subAsset.Key, StringComparison.Ordinal))
                {
                    return false;
                }

                // Same key, possibly a fresh descriptor (a re-commit after
                // reimport carries a new instance id under the same key).
                _items[0].SubAsset = subAsset;
                return true;
            }

            var folder = SelectedSidebarFolder;
            if (folder != null
                && IndexedPathQueries.IsFolder(OwnerController, folder.Path))
            {
                return _items.Count == 1
                    && _items[0].SubAsset == null
                    && string.Equals(_items[0].Path, folder.Path, StringComparison.Ordinal);
            }

            // Nothing previewable at all: unchanged only while nothing is shown.
            // With items on screen the full path runs and closes the window.
            return _items.Count == 0;
        }

        private static bool SameTags(ZepAssetEntry a, ZepAssetEntry b)
        {
            var tagsA = a?.Tags;
            var tagsB = b?.Tags;
            var countA = tagsA?.Count ?? 0;
            var countB = tagsB?.Count ?? 0;
            if (countA != countB)
            {
                return false;
            }

            for (var i = 0; i < countA; i++)
            {
                if (!string.Equals(tagsA[i].Id, tagsB[i].Id, StringComparison.Ordinal)
                    || tagsA[i].Color != tagsB[i].Color
                    || !string.Equals(tagsA[i].Name, tagsB[i].Name, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private void OnPanelThemeChanged(string themeId)
        {
            _themeSwitcher?.SetTheme(themeId, persistPreference: false);
        }

        private void OnEditorUpdate()
        {
            // Lifetime tether: a frameless popup has no OS close button, so it
            // must not outlive its panel. Host window destroyed → close now.
            // Shell detached is transient during dock/undock re-parenting, so
            // only a sustained detachment counts.
            if (_hostWindow == null)
            {
                Close();
                return;
            }

            _shellDetachedTicks = OwnerElement?.panel == null ? _shellDetachedTicks + 1 : 0;
            if (_shellDetachedTicks > 10)
            {
                Close();
                return;
            }

            // Animation/audio/video/particle previews advance outside the IMGUI event
            // stream; without this they freeze until the next mouse move. Guard the poll
            // like DrawPreviewImgui guards its draw: a custom/third-party editor can throw
            // from RequiresConstantRepaint, and this runs every editor tick, so an unguarded
            // throw would spam the console. Swallow it — the draw path's own try/catch tears
            // the broken editor down and rebuilds.
            if (_editor != null && _previewImgui != null)
            {
                try
                {
                    if (_editor.RequiresConstantRepaint())
                    {
                        _previewImgui.MarkDirtyRepaint();
                    }
                }
                catch (Exception)
                {
                    // Broken preview editor; DrawPreviewImgui handles the teardown.
                }
            }
        }

        // The batch is not consulted: the folder grid, the filmstrip and the
        // preview icons together hold a handful of thumbs, and each one's
        // compare against the repository cache is cheaper than testing its
        // path against the batch.
        private void OnPreviewsReady(IReadOnlyList<string> appliedPaths)
        {
            RefreshThumbs(_folderThumbs);
            RefreshThumbs(_stripThumbs);
            RefreshThumbs(_previewIconThumbs);
            RefreshThumbs(_dependencyThumbs);
        }

        private void RefreshThumbs(List<(Image image, string path, string typeName)> thumbs)
        {
            for (var i = 0; i < thumbs.Count; i++)
            {
                var (image, path, typeName) = thumbs[i];
                var icon = OwnerIcons.GetAssetIcon(path, typeName);
                if (icon != null && !ReferenceEquals(image.image, icon))
                {
                    image.image = icon;
                }
            }
        }

        private void OnRootKeyDown(KeyDownEvent evt)
        {
            if (_search != null)
            {
                _shell.ShortcutController.HandleKeyDown(evt);
                if (evt.isPropagationStopped) return;
            }
            switch (evt.keyCode)
            {
                case KeyCode.Space:
                case KeyCode.Escape:
                    Close();
                    evt.StopPropagation();
                    break;
                case KeyCode.LeftArrow:
                    StepSelection(-1);
                    evt.StopPropagation();
                    break;
                case KeyCode.RightArrow:
                    StepSelection(+1);
                    evt.StopPropagation();
                    break;
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                    if (_search != null)
                    {
                        _search.MoveResultSelection(evt.keyCode == KeyCode.DownArrow ? 1 : -1);
                        evt.StopPropagation();
                    }
                    break;
                case KeyCode.Backspace:
                    if (_drillTrail.Count > 0)
                    {
                        DrillBack();
                        evt.StopPropagation();
                    }

                    break;
            }
        }

        private void StepSelection(int direction)
        {
            if (_items.Count < 2)
            {
                return;
            }

            var next = Mathf.Clamp(_index + direction, 0, _items.Count - 1);
            if (next == _index && _drillTrail.Count == 0)
            {
                // At the strip's edge nothing changes — rebuilding here would
                // pointlessly restart a playing preview.
                return;
            }

            _index = next;
            _drillTrail.Clear();
            ShowCurrent();
        }

        private Item CurrentItem
        {
            get
            {
                if (_drillTrail.Count > 0)
                {
                    return _drillTrail[_drillTrail.Count - 1];
                }

                if (_items.Count == 0)
                {
                    return null;
                }

                _index = Mathf.Clamp(_index, 0, _items.Count - 1);
                return _items[_index];
            }
        }

        private void DrillInto(Item item)
        {
            _drillTrail.Add(item);
            ShowCurrent();
        }

        private void DrillBack()
        {
            if (_drillTrail.Count == 0)
            {
                return;
            }

            _drillTrail.RemoveAt(_drillTrail.Count - 1);
            ShowCurrent();
        }

        private void OpenCurrent()
        {
            var item = CurrentItem;
            if (item == null)
            {
                return;
            }

            if (item.IsFolder)
            {
                // "Open" on a folder browses the panel to it.
                if (_paneWindow != null) _paneWindow.Pane?.TrySetFolderSelection(item.Path);
                else _shell.OpenFolderFromPane(PreviewPane, item.Path);
                return;
            }

            if (item.SubAsset != null)
            {
                // Path-based OpenAsset would open the parent; open the
                // sub-OBJECT, mirroring the pane view's sub-open route.
                var subObject = SubAssetCatalog.Resolve(item.SubAsset);
                if (subObject != null)
                {
                    AssetDatabase.OpenAsset(subObject);
                }

                return;
            }

            OwnerController?.OpenAsset(AssetPath.From(item.Path));
        }

        // ------------------------------------------------------------------
        // Content pipeline
        // ------------------------------------------------------------------

        private void ShowCurrent()
        {
            var generation = ++_previewGeneration;
            DestroyEditor();
            if (generation != _previewGeneration) return;
            _previewImgui = null;
            _currentAsset = null;
            _previewZone.Clear();
            _folderThumbs.Clear();
            _previewIconThumbs.Clear();

            var item = CurrentItem;
            if (item == null)
            {
                _headerIcon.image = null;
                _title.text = "Nothing selected";
                _subtitle.text = "Select something in the panel to preview it";
                _backButton.style.display = DisplayStyle.None;
                ApplyEmptySelectionChrome(true);
                AddEmptyLabel(_previewZone, "Nothing to preview");
                _infoScroll.Clear();
                UpdateFilmstripHighlight();
                return;
            }

            ApplyEmptySelectionChrome(false);
            _headerIcon.image = ItemIcon(item);
            if (item.SubAsset == null)
            {
                // Sub items stay off the upgrade list: it is path-keyed, and a
                // sub-asset's path is its PARENT's — the "upgrade" would swap
                // in the parent's rendered preview.
                _previewIconThumbs.Add((_headerIcon, item.Path, item.TypeName));
            }

            _title.text = item.Name;
            _subtitle.text = BuildSubtitle(item);
            _backButton.style.display = _drillTrail.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            if (item.IsFolder)
            {
                BuildFolderPreview(item);
            }
            else
            {
                _currentAsset = item.SubAsset != null
                    ? SubAssetCatalog.Resolve(item.SubAsset)
                    : AssetDatabase.LoadMainAssetAtPath(item.Path);
                BuildAssetPreview(item, _currentAsset);
            }

            RebuildInfoPane();
            UpdateFilmstripHighlight();
        }

        // Header icon, Open button and info pane carry no content while there
        // is nothing to preview. Both directions live here so a live-follow
        // selection that arrives after an empty open brings them back — the
        // empty state hid them and nothing else would.
        // (The filmstrip is not here: RebuildFilmstrip owns its display.)
        private void ApplyEmptySelectionChrome(bool isEmpty)
        {
            var display = isEmpty ? DisplayStyle.None : DisplayStyle.Flex;
            _headerIcon.style.display = display;
            _openButton.style.display = display;
            _infoScroll.style.display = display;
        }

        // The item's own icon: sub-assets use the mini thumbnail their row
        // shows (the repository lookup is path-keyed and would answer for the
        // parent), everything else asks the shared icon repository.
        private Texture ItemIcon(Item item)
        {
            if (item.SubAsset != null && item.SubAsset.Icon != null)
            {
                return item.SubAsset.Icon;
            }

            return OwnerIcons.GetAssetIcon(item.Path, item.TypeName);
        }

        private string BuildSubtitle(Item item)
        {
            var type = item.IsFolder ? "Folder" : PrettyTypeName(item.TypeName);
            if (item.SubAsset != null)
            {
                return $"{type} · in {Path.GetFileName(item.Path)}";
            }

            if (_drillTrail.Count > 0)
            {
                var parent = Path.GetDirectoryName(item.Path)?.Replace('\\', '/');
                return string.IsNullOrEmpty(parent) ? type : $"{type} · in {parent}";
            }

            return _items.Count > 1 ? $"{type} · {_index + 1} of {_items.Count} selected" : type;
        }

        private static string PrettyTypeName(string typeName)
        {
            return string.IsNullOrEmpty(typeName) ? "Asset" : typeName;
        }

        private void AddEmptyLabel(VisualElement parent, string message)
        {
            var label = new Label(message);
            label.AddToClassList(ZepUiClasses.QuickLookEmptyLabel);
            parent.Add(label);
        }

        // ------------------------------------------------------------------
        // Asset preview (interactive editor preview / text / fallback)
        // ------------------------------------------------------------------

        private void BuildAssetPreview(Item item, UnityEngine.Object asset)
        {
            if (asset == null)
            {
                BuildFallbackPreview(item, item.SubAsset != null
                    ? "Sub-asset is no longer inside this asset"
                    : "Asset is missing on disk");
                return;
            }

            // The text gate is extension-based and a sub-asset's extension is
            // its PARENT's — a sub-object is never raw text.
            if (item.SubAsset == null && IsTextExtension(item.Path))
            {
                BuildTextPreview(item.Path);
                return;
            }

            // Third-party boundary: CreateEditor instantiates the asset type's
            // custom inspector, whose constructor/OnEnable/HasPreviewGUI can
            // throw. One bad editor must not take Quick Look down with it.
            var generation = _previewGeneration;
            try
            {
                var editor = UnityEditor.Editor.CreateEditor(asset);
                if (generation != _previewGeneration)
                {
                    if (editor != null) DestroyImmediate(editor);
                    return;
                }

                _editor = editor;
                var hasPreview = editor != null && editor.HasPreviewGUI();
                if (generation != _previewGeneration) return;
                if (hasPreview)
                {
                    var container = new IMGUIContainer(DrawPreviewImgui);
                    container.AddToClassList(ZepUiClasses.QuickLookPreviewImgui);
                    _previewZone.Add(container);
                    _previewImgui = container;
                    return;
                }
            }
            catch (Exception)
            {
                if (generation != _previewGeneration) return;
                DestroyEditor();
                if (generation != _previewGeneration) return;
                BuildFallbackPreview(item, "This asset's inspector failed to load");
                return;
            }

            DestroyEditor();
            if (generation != _previewGeneration) return;
            BuildFallbackPreview(item, "No live preview for this asset type");
        }

        private void DrawPreviewImgui()
        {
            var generation = _previewGeneration;
            var editor = _editor;
            if (editor == null)
            {
                return;
            }

            try
            {
                // Same chrome as the inspector's preview strip: the settings
                // row hosts the type's own controls (play/pause for anim,
                // audio and video, mip/channel toggles for textures, …).
                GUILayout.BeginHorizontal(EditorStyles.toolbar);
                GUILayout.FlexibleSpace();
                editor.OnPreviewSettings();
                GUILayout.EndHorizontal();
                if (generation != _previewGeneration) return;

                // No degenerate-rect guard: during the Layout event GetRect
                // returns a 1×1 dummy, and skipping the preview there would
                // desync IMGUI control IDs for editors that allocate controls
                // in Layout. The inspector calls through unconditionally too.
                var rect = GUILayoutUtility.GetRect(
                    64f, 64f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                _previewBackground ??= "PreBackground";
                editor.OnInteractivePreviewGUI(rect, _previewBackground);
            }
            catch (ExitGUIException)
            {
                // IMGUI control flow (color pickers, object selectors) — not an error.
                throw;
            }
            catch (Exception)
            {
                // Custom/third-party editors can throw from their preview.
                // Fall back once instead of spamming the console every repaint.
                // The tree rebuild is deferred: this IMGUIContainer is mid-OnGUI
                // and must not be removed from under itself.
                if (generation != _previewGeneration) return;
                DestroyEditor();
                if (generation != _previewGeneration) return;
                _previewImgui = null;
                _root.schedule.Execute(() =>
                {
                    if (this == null || generation != _previewGeneration) return;
                    var item = CurrentItem;
                    _previewZone.Clear();
                    if (item != null)
                    {
                        BuildFallbackPreview(item, "This asset's preview failed to render");
                    }
                });
            }
        }

        private void BuildFallbackPreview(Item item, string message)
        {
            var fallback = new VisualElement();
            fallback.AddToClassList(ZepUiClasses.QuickLookPreviewFallback);

            var icon = new Image
            {
                scaleMode = ScaleMode.ScaleToFit,
                image = ItemIcon(item),
            };
            icon.AddToClassList(ZepUiClasses.QuickLookPreviewFallbackIcon);
            if (item.SubAsset == null)
            {
                // Path-keyed upgrade list — see the header-icon note.
                _previewIconThumbs.Add((icon, item.Path, item.TypeName));
            }

            fallback.Add(icon);

            var label = new Label(message);
            label.AddToClassList(ZepUiClasses.QuickLookPreviewFallbackLabel);
            fallback.Add(label);

            _previewZone.Add(fallback);
        }

        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".js", ".txt", ".md", ".json", ".xml", ".yaml", ".yml", ".csv",
            ".shader", ".cginc", ".hlsl", ".glslinc", ".compute", ".raytrace",
            ".uss", ".uxml", ".asmdef", ".asmref", ".rsp", ".toml", ".ini",
            ".html", ".css", ".proto", ".sql", ".sh", ".bat",
        };

        private static bool IsTextExtension(string path)
        {
            return TextExtensions.Contains(Path.GetExtension(path));
        }

        private void BuildTextPreview(string assetPath)
        {
            string text;
            try
            {
                text = ReadTextHead(FileUtil.GetPhysicalPath(assetPath));
            }
            catch (IOException)
            {
                text = null;
            }
            catch (UnauthorizedAccessException)
            {
                text = null;
            }

            if (text == null)
            {
                var item = CurrentItem;
                if (item != null)
                {
                    BuildFallbackPreview(item, "File could not be read as text");
                }

                return;
            }

            var scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            scroll.AddToClassList(ZepUiClasses.QuickLookCodeScroll);
            var label = new Label(text) { enableRichText = false };
            label.AddToClassList(ZepUiClasses.QuickLookCodeText);
            // WHY inline: the monospace face is an OS font resolved at runtime
            // (Menlo/Consolas/DejaVu chain); a Font object created in C# cannot
            // be referenced from USS.
            var mono = MonoFont();
            if (mono != null)
            {
                label.style.unityFontDefinition = FontDefinition.FromFont(mono);
            }

            scroll.Add(label);
            _previewZone.Add(scroll);
        }

        private static Font MonoFont()
        {
            if (_monoFont == null)
            {
                _monoFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "Menlo", "Consolas", "DejaVu Sans Mono", "Courier New" }, 12);
            }

            return _monoFont;
        }

        private static string ReadTextHead(string absolutePath)
        {
            using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(MaxTextPreviewBytes, stream.Length)];
            // Stream.Read may satisfy fewer bytes than requested (a legal short read); a
            // single call would leave the buffer partly filled, truncating valid text and
            // appending a spurious "…". Loop until the buffer is full or we hit EOF.
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }

            // NUL in the head means binary content mislabeled as text.
            for (var i = 0; i < Math.Min(read, 8000); i++)
            {
                if (buffer[i] == 0)
                {
                    return null;
                }
            }

            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            var truncatedByBytes = stream.Length > read;

            var lineCount = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                {
                    continue;
                }

                lineCount++;
                if (lineCount >= MaxTextPreviewLines)
                {
                    text = text.Substring(0, i + 1);
                    truncatedByBytes = true;
                    break;
                }
            }

            return truncatedByBytes ? text + "\n…" : text;
        }

        // ------------------------------------------------------------------
        // Folder preview (stats header + child grid)
        // ------------------------------------------------------------------

        private void BuildFolderPreview(Item item)
        {
            var stats = CollectFolderStats(item.Path);

            var header = new VisualElement();
            header.AddToClassList(ZepUiClasses.QuickLookFolderStats);

            var suffix = stats.Truncated ? "+" : string.Empty;
            var line = new Label(
                $"{stats.FolderCount:N0}{suffix} folders · {stats.FileCount:N0}{suffix} files · " +
                $"{AssetEntryRenderer.FormatSize(stats.Bytes)}{suffix}");
            line.AddToClassList(ZepUiClasses.QuickLookFolderStatsLine);
            header.Add(line);

            var buckets = TopBuckets(stats.Buckets, 6);
            if (buckets.Count > 0)
            {
                var bar = new VisualElement();
                bar.AddToClassList(ZepUiClasses.QuickLookBreakdownBar);
                var legend = new VisualElement();
                legend.AddToClassList(ZepUiClasses.QuickLookBreakdownLegend);

                foreach (var (bucket, count) in buckets)
                {
                    var segment = new VisualElement();
                    segment.AddToClassList(ZepUiClasses.QuickLookBreakdownSegment);
                    // WHY inline: segment weight is the runtime file count, and
                    // the segment color reuses the tag palette's identity colors
                    // (same sanctioned mechanism as the tag dots).
                    segment.style.flexGrow = count;
                    ZepTagColorPalette.ApplyBackgroundColor(segment, BucketColor(bucket));
                    bar.Add(segment);

                    var legendItem = new VisualElement();
                    legendItem.AddToClassList(ZepUiClasses.QuickLookBreakdownLegendItem);
                    var dot = new VisualElement();
                    dot.AddToClassList(ZepUiClasses.TagColorDot);
                    ZepTagColorPalette.ApplyBackgroundColor(dot, BucketColor(bucket));
                    legendItem.Add(dot);
                    var legendLabel = new Label($"{count:N0} {bucket}");
                    legendLabel.AddToClassList(ZepUiClasses.QuickLookBreakdownLegendLabel);
                    legendItem.Add(legendLabel);
                    legend.Add(legendItem);
                }

                header.Add(bar);
                header.Add(legend);
            }

            _previewZone.Add(header);

            var children = ListDirectChildren(item.Path, out var moreChildren);
            if (children.Count == 0)
            {
                AddEmptyLabel(_previewZone, "Empty folder");
                return;
            }

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList(ZepUiClasses.QuickLookFolderGridScroll);
            var grid = new VisualElement();
            grid.AddToClassList(ZepUiClasses.QuickLookFolderGrid);

            var shown = Math.Min(children.Count, MaxFolderGridTiles);
            for (var i = 0; i < shown; i++)
            {
                var child = children[i];
                var tile = new VisualElement { tooltip = child.Name };
                tile.AddToClassList(ZepUiClasses.QuickLookFolderTile);

                var icon = new Image
                {
                    scaleMode = ScaleMode.ScaleToFit,
                    image = OwnerIcons.GetAssetIcon(child.Path, child.TypeName),
                };
                icon.AddToClassList(ZepUiClasses.QuickLookFolderTileIcon);
                tile.Add(icon);
                _folderThumbs.Add((icon, child.Path, child.TypeName));

                var label = new Label(child.Name);
                label.AddToClassList(ZepUiClasses.QuickLookFolderTileLabel);
                tile.Add(label);

                var captured = child;
                tile.RegisterCallback<ClickEvent>(_ => DrillInto(captured));
                grid.Add(tile);
            }

            if (children.Count > shown)
            {
                AddEmptyLabel(grid, $"+{children.Count - shown:N0} more…");
            }
            else if (moreChildren)
            {
                // The list was capped at MaxFolderGridTiles, so the exact remainder is
                // unknown (we stopped materializing to avoid the stall).
                AddEmptyLabel(grid, "+ more…");
            }

            scroll.Add(grid);
            _previewZone.Add(scroll);
        }

        // Materializes at most MaxFolderGridTiles direct children — the most the grid can
        // display. Type-resolving every entry of a huge flat folder (thousands of files) via
        // GetMainAssetTypeAtPath stalled the main thread; nothing past the cap is ever shown.
        // `truncated` reports whether more direct children were left unmaterialized.
        private List<Item> ListDirectChildren(string folderPath, out bool truncated)
        {
            truncated = false;
            var children = new List<Item>();
            string absolute;
            try
            {
                absolute = FileUtil.GetPhysicalPath(folderPath);
                if (!Directory.Exists(absolute))
                {
                    return children;
                }

                foreach (var dir in Directory.EnumerateDirectories(absolute))
                {
                    var name = Path.GetFileName(dir);
                    if (IsHiddenEntry(name))
                    {
                        continue;
                    }

                    // Cap BEFORE the validity probe, exactly as the file loop below
                    // caps before its type resolution: past the cap nothing is shown,
                    // so a directory with thousands of subdirectories must not pay an
                    // IsValidFolder call per entry to discover that.
                    if (children.Count >= MaxFolderGridTiles)
                    {
                        truncated = true;
                        return SortChildren(children);
                    }

                    var childPath = folderPath + "/" + name;
                    if (!IndexedPathQueries.IsFolder(OwnerController, childPath))
                    {
                        continue;
                    }

                    children.Add(new Item { Path = childPath, Name = name, TypeName = "Folder", IsFolder = true });
                }

                foreach (var file in Directory.EnumerateFiles(absolute))
                {
                    var name = Path.GetFileName(file);
                    if (IsHiddenEntry(name) || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Cap BEFORE the type resolution so a giant folder doesn't pay it per file.
                    if (children.Count >= MaxFolderGridTiles)
                    {
                        truncated = true;
                        return SortChildren(children);
                    }

                    var childPath = folderPath + "/" + name;
                    var typeName = IndexedPathQueries.ResolveTypeName(OwnerController, childPath);
                    if (typeName == null)
                    {
                        continue;
                    }

                    children.Add(new Item { Path = childPath, Name = name, TypeName = typeName, IsFolder = false });
                }
            }
            catch (IOException)
            {
                return SortChildren(children);
            }
            catch (UnauthorizedAccessException)
            {
                return SortChildren(children);
            }

            return SortChildren(children);
        }

        private static List<Item> SortChildren(List<Item> children)
        {
            children.Sort((a, b) => a.IsFolder != b.IsFolder
                ? (a.IsFolder ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return children;
        }

        internal readonly struct FolderStats
        {
            public FolderStats(int fileCount, int folderCount, long bytes, bool truncated,
                Dictionary<string, int> buckets)
            {
                FileCount = fileCount;
                FolderCount = folderCount;
                Bytes = bytes;
                Truncated = truncated;
                Buckets = buckets;
            }

            public int FileCount { get; }
            public int FolderCount { get; }
            public long Bytes { get; }
            public bool Truncated { get; }
            public Dictionary<string, int> Buckets { get; }
        }

        internal static FolderStats CollectFolderStats(string folderPath)
        {
            var files = 0;
            var folders = 0;
            long bytes = 0;
            var truncated = false;
            var buckets = new Dictionary<string, int>(StringComparer.Ordinal);

            string absolute;
            try
            {
                absolute = FileUtil.GetPhysicalPath(folderPath);
            }
            catch (Exception)
            {
                return new FolderStats(0, 0, 0, false, buckets);
            }

            if (!Directory.Exists(absolute))
            {
                return new FolderStats(0, 0, 0, false, buckets);
            }

            var pending = new Stack<string>();
            pending.Push(absolute);
            var visited = 0;
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                try
                {
                    // One syscall per entry: the enumerator already stat-ed each
                    // entry, so directory-ness (FileInfo vs DirectoryInfo, symlinks
                    // resolved) and length come off the returned info instead of a
                    // second Directory.Exists plus a third FileInfo probe.
                    foreach (var info in new DirectoryInfo(current).EnumerateFileSystemInfos())
                    {
                        if (++visited > MaxFolderWalkEntries)
                        {
                            truncated = true;
                            pending.Clear();
                            break;
                        }

                        var name = info.Name;
                        if (IsHiddenEntry(name))
                        {
                            continue;
                        }

                        if (info is DirectoryInfo)
                        {
                            folders++;
                            pending.Push(info.FullName);
                            continue;
                        }

                        if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        files++;
                        try
                        {
                            bytes += ((FileInfo)info).Length;
                        }
                        catch (IOException)
                        {
                            // A dangling symlink or an entry deleted mid-walk still
                            // counts as a file with no bytes, as it did before.
                            // FileNotFoundException is an IOException.
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }

                        var bucket = BucketForExtension(Path.GetExtension(name));
                        buckets.TryGetValue(bucket, out var count);
                        buckets[bucket] = count + 1;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return new FolderStats(files, folders, bytes, truncated, buckets);
        }

        private static bool IsHiddenEntry(string name)
        {
            // Unity's import rules: dot-prefixed and tilde-suffixed entries
            // are invisible to the AssetDatabase.
            return string.IsNullOrEmpty(name) || name[0] == '.' || name[name.Length - 1] == '~';
        }

        internal static string BucketForExtension(string extension)
        {
            switch (extension?.ToLowerInvariant())
            {
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".psd":
                case ".tif": case ".tiff": case ".exr": case ".hdr": case ".bmp":
                case ".gif": case ".webp":
                    return "Textures";
                case ".cs": case ".js": case ".shader": case ".cginc": case ".hlsl":
                case ".compute": case ".asmdef": case ".asmref":
                    return "Code";
                case ".prefab":
                    return "Prefabs";
                case ".mat": case ".physicmaterial": case ".physicsmaterial2d":
                    return "Materials";
                case ".wav": case ".mp3": case ".ogg": case ".aiff": case ".aif": case ".flac":
                    return "Audio";
                case ".fbx": case ".obj": case ".blend": case ".dae": case ".3ds":
                case ".gltf": case ".glb":
                    return "Models";
                case ".unity":
                    return "Scenes";
                case ".anim": case ".controller": case ".overridecontroller": case ".mask":
                    return "Animation";
                default:
                    return "Other";
            }
        }

        private static ZepTagColor BucketColor(string bucket)
        {
            switch (bucket)
            {
                case "Textures": return ZepTagColor.Blue;
                case "Code": return ZepTagColor.Green;
                case "Prefabs": return ZepTagColor.Purple;
                case "Materials": return ZepTagColor.Orange;
                case "Audio": return ZepTagColor.Yellow;
                case "Models": return ZepTagColor.Red;
                case "Animation": return ZepTagColor.Gray;
                default: return ZepTagColor.None;
            }
        }

        private static List<(string bucket, int count)> TopBuckets(Dictionary<string, int> buckets, int max)
        {
            var list = new List<(string bucket, int count)>();
            foreach (var pair in buckets)
            {
                list.Add((pair.Key, pair.Value));
            }

            list.Sort((a, b) => b.count.CompareTo(a.count));
            if (list.Count > max)
            {
                var rest = 0;
                for (var i = max; i < list.Count; i++)
                {
                    rest += list[i].count;
                }

                list.RemoveRange(max, list.Count - max);

                // A real "Other" bucket (BucketForExtension's default branch: .asset, .txt,
                // .ttf, .preset, …) may already survive in the kept top-`max` slice. Merge the
                // folded tail into it rather than appending a SECOND "Other", which drew two
                // grey breakdown segments and two "N Other" legend rows.
                var existingOther = -1;
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i].bucket == "Other")
                    {
                        existingOther = i;
                        break;
                    }
                }

                if (existingOther >= 0)
                {
                    list[existingOther] = ("Other", list[existingOther].count + rest);
                }
                else
                {
                    list.Add(("Other", rest));
                }
            }

            return list;
        }

        // ------------------------------------------------------------------
        // Info pane
        // ------------------------------------------------------------------

        private void RebuildInfoPane()
        {
            _infoScroll.Clear();
            _dependencyThumbs.Clear();
            var item = CurrentItem;
            if (item == null)
            {
                return;
            }

            AddInfoSection("General");
            AddInfoRow("Type", item.IsFolder ? "Folder" : PrettyTypeName(item.TypeName));

            var presentation = OwnerController != null
                ? OwnerController.GetPresentation(item.Path, item.TypeName)
                : default(AssetPresentationData);
            if (presentation.Size.HasValue)
            {
                AddInfoRow("Size", AssetEntryRenderer.FormatSize(presentation.Size));
            }

            if (presentation.ModifiedDate.HasValue)
            {
                AddInfoRow("Modified", AssetEntryRenderer.FormatModifiedDate(presentation.ModifiedDate));
            }

            AddInfoRow("Path", item.Path);
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
            var guid = AssetDatabase.AssetPathToGUID(item.Path);
            if (!string.IsNullOrEmpty(guid))
            {
                AddInfoRow("GUID", guid);
            }

            if (item.SubAsset != null)
            {
                // A sub-asset shares the parent's path and GUID; the local
                // file id is the half of its identity that is its own.
                AddInfoRow("Local ID", item.SubAsset.LocalId.ToString());
            }

            if (presentation.IsAddressable)
            {
                AddInfoRow("Addressable", "Yes");
            }

            if (item.Entry != null && item.Entry.Tags.Count > 0)
            {
                AddInfoSection("Tags");
                var row = new VisualElement();
                row.AddToClassList(ZepUiClasses.QuickLookInfoTagsRow);
                foreach (var tag in item.Entry.Tags)
                {
                    row.Add(BuildTagChip(tag));
                }

                _infoScroll.Add(row);
            }

            if (!item.IsFolder && _currentAsset != null)
            {
                AddTypeDetails(item, _currentAsset);
                if (item.SubAsset == null)
                {
                    // GetDependencies is path-level: for a sub-asset it would
                    // list the whole container file's dependencies as if they
                    // were the sub-object's.
                    AddDependencies(item);
                }
            }
        }

        private static VisualElement BuildTagChip(ZepTag tag)
        {
            var chip = new VisualElement();
            chip.AddToClassList(ZepUiClasses.TagChip);
            var dot = new VisualElement();
            dot.AddToClassList(ZepUiClasses.TagColorDot);
            ZepTagColorPalette.ApplyBackgroundColor(dot, tag.Color);
            chip.Add(dot);
            var label = new Label(tag.Name);
            label.AddToClassList(ZepUiClasses.TagChipLabel);
            chip.Add(label);
            return chip;
        }

        private void AddInfoSection(string title)
        {
            var label = new Label(title.ToUpperInvariant());
            label.AddToClassList(ZepUiClasses.QuickLookInfoSection);
            _infoScroll.Add(label);
        }

        private void AddInfoRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList(ZepUiClasses.QuickLookInfoRow);
            var key = new Label(label);
            key.AddToClassList(ZepUiClasses.QuickLookInfoRowLabel);
            row.Add(key);
            var val = new Label(value);
            val.AddToClassList(ZepUiClasses.QuickLookInfoRowValue);
            row.Add(val);
            _infoScroll.Add(row);
        }

        private void AddTypeDetails(Item item, UnityEngine.Object asset)
        {
            var rows = new List<(string label, string value)>();
            CollectTypeDetails(item, asset, rows);
            if (rows.Count == 0)
            {
                return;
            }

            AddInfoSection("Details");
            foreach (var (label, value) in rows)
            {
                AddInfoRow(label, value);
            }
        }

        private static void CollectTypeDetails(Item item, UnityEngine.Object asset, List<(string, string)> rows)
        {
            switch (asset)
            {
                case Texture2D texture:
                    rows.Add(("Dimensions", $"{texture.width} × {texture.height}"));
                    rows.Add(("Format", texture.format.ToString()));
                    rows.Add(("Mip Maps", texture.mipmapCount.ToString()));
                    rows.Add(("Memory", AssetEntryRenderer.FormatSize(
                        UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture))));
                    if (AssetImporter.GetAtPath(item.Path) is TextureImporter importer)
                    {
                        rows.Add(("Import Type", importer.textureType.ToString()));
                        rows.Add(("sRGB", importer.sRGBTexture ? "Yes" : "No"));
                        rows.Add(("Max Size", importer.maxTextureSize.ToString()));
                    }

                    break;
                case AudioClip clip:
                    rows.Add(("Duration", FormatDuration(clip.length)));
                    rows.Add(("Channels", clip.channels.ToString()));
                    rows.Add(("Frequency", $"{clip.frequency:N0} Hz"));
                    if (AssetImporter.GetAtPath(item.Path) is AudioImporter audioImporter)
                    {
                        rows.Add(("Compression",
                            audioImporter.defaultSampleSettings.compressionFormat.ToString()));
                        rows.Add(("Load Type", audioImporter.defaultSampleSettings.loadType.ToString()));
                    }

                    break;
                case AnimationClip animation:
                    rows.Add(("Duration", FormatDuration(animation.length)));
                    rows.Add(("Frame Rate", animation.frameRate.ToString("0.##")));
                    rows.Add(("Looping", animation.isLooping ? "Yes" : "No"));
                    rows.Add(("Human Motion", animation.humanMotion ? "Yes" : "No"));
                    rows.Add(("Legacy", animation.legacy ? "Yes" : "No"));
                    if (animation.events.Length > 0)
                    {
                        rows.Add(("Events", animation.events.Length.ToString()));
                    }

                    break;
                case Material material:
                    rows.Add(("Shader", material.shader != null ? material.shader.name : "(missing)"));
                    rows.Add(("Render Queue", material.renderQueue.ToString()));
                    if (material.shaderKeywords.Length > 0)
                    {
                        rows.Add(("Keywords", material.shaderKeywords.Length.ToString()));
                    }

                    break;
                case Mesh mesh:
                    rows.Add(("Vertices", mesh.vertexCount.ToString("N0")));
                    rows.Add(("Sub-meshes", mesh.subMeshCount.ToString()));
                    rows.Add(("Bounds", FormatBounds(mesh.bounds.size)));
                    break;
                case GameObject go:
                    rows.Add(("Components", go.GetComponents<Component>().Length.ToString()));
                    rows.Add(("Total Objects", go.transform.hierarchyCount.ToString("N0")));
                    CollectMeshTotals(go, rows);
                    break;
                case Shader shader:
                    rows.Add(("Properties", shader.GetPropertyCount().ToString()));
                    break;
                case MonoScript script:
                {
                    var scriptClass = script.GetClass();
                    if (scriptClass != null)
                    {
                        rows.Add(("Class", scriptClass.FullName));
                    }

                    break;
                }

                case UnityEngine.Video.VideoClip video:
                    rows.Add(("Duration", FormatDuration((float)video.length)));
                    rows.Add(("Dimensions", $"{video.width} × {video.height}"));
                    rows.Add(("Frame Rate", video.frameRate.ToString("0.##")));
                    break;
                case ScriptableObject so:
                {
                    var monoScript = MonoScript.FromScriptableObject(so);
                    if (monoScript != null)
                    {
                        rows.Add(("Script", monoScript.name));
                    }

                    break;
                }
            }
        }

        private static void CollectMeshTotals(GameObject go, List<(string, string)> rows)
        {
            long vertices = 0;
            long triangles = 0;
            foreach (var filter in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                vertices += mesh.vertexCount;
                triangles += CountTriangles(mesh);
            }

            foreach (var skinned in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = skinned.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                vertices += mesh.vertexCount;
                triangles += CountTriangles(mesh);
            }

            if (vertices > 0)
            {
                rows.Add(("Vertices", vertices.ToString("N0")));
                rows.Add(("Triangles", triangles.ToString("N0")));
            }
        }

        private static long CountTriangles(Mesh mesh)
        {
            // mesh.triangles allocates the whole index buffer; the per-submesh count query
            // does not. Divide each submesh's index count by its topology's real
            // indices-per-primitive — blindly dividing every submesh by 3 reported nonsense
            // for quad/line/point meshes.
            long total = 0;
            for (var i = 0; i < mesh.subMeshCount; i++)
            {
                var indexCount = (long)mesh.GetIndexCount(i);
                switch (mesh.GetTopology(i))
                {
                    case MeshTopology.Triangles:
                        total += indexCount / 3;
                        break;
                    case MeshTopology.Quads:
                        total += indexCount / 4 * 2; // each quad tessellates to 2 triangles
                        break;
                    // Lines, LineStrip and Points contribute no triangles.
                }
            }

            return total;
        }

        internal static string FormatDuration(float seconds)
        {
            if (seconds < 0f || float.IsNaN(seconds) || float.IsInfinity(seconds))
            {
                return "0:00";
            }

            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}.{span.Milliseconds:000}";
        }

        private static string FormatBounds(Vector3 size)
        {
            return $"{size.x:0.##} × {size.y:0.##} × {size.z:0.##}";
        }

        private void AddDependencies(Item item)
        {
            string[] dependencies;
            try
            {
                dependencies = AssetDatabase.GetDependencies(item.Path, recursive: false);
            }
            catch (Exception)
            {
                return;
            }

            var others = new List<string>();
            foreach (var dependency in dependencies)
            {
                if (!string.Equals(dependency, item.Path, StringComparison.Ordinal))
                {
                    others.Add(dependency);
                }
            }

            if (others.Count == 0)
            {
                return;
            }

            others.Sort(StringComparer.OrdinalIgnoreCase);
            AddInfoSection($"Dependencies ({others.Count})");

            var shown = Math.Min(others.Count, MaxDependencyRows);
            for (var i = 0; i < shown; i++)
            {
                var dependencyPath = others[i];
                var typeName = IndexedPathQueries.ResolveTypeName(OwnerController, dependencyPath)
                    ?? string.Empty;

                var row = new VisualElement { tooltip = dependencyPath };
                row.AddToClassList(ZepUiClasses.QuickLookDepRow);
                var icon = new Image
                {
                    scaleMode = ScaleMode.ScaleToFit,
                    image = OwnerIcons.GetAssetIcon(dependencyPath, typeName),
                };
                icon.AddToClassList(ZepUiClasses.QuickLookDepIcon);
                row.Add(icon);
                _dependencyThumbs.Add((icon, dependencyPath, typeName));
                var label = new Label(Path.GetFileName(dependencyPath));
                label.AddToClassList(ZepUiClasses.QuickLookDepLabel);
                row.Add(label);

                var captured = dependencyPath;
                var capturedType = typeName;
                row.RegisterCallback<ClickEvent>(_ => DrillInto(new Item
                {
                    Path = captured,
                    Name = Path.GetFileName(captured),
                    TypeName = capturedType,
                    IsFolder = IndexedPathQueries.IsFolder(OwnerController, captured),
                }));
                _infoScroll.Add(row);
            }

            if (others.Count > shown)
            {
                AddInfoRow(string.Empty, $"+{others.Count - shown} more…");
            }
        }

        // ------------------------------------------------------------------
        // Filmstrip (multi-selection)
        // ------------------------------------------------------------------

        private void RebuildFilmstrip()
        {
            _filmstrip.Clear();
            _stripThumbs.Clear();
            _stripTiles.Clear();
            _stripFirst = 0;
            if (_items.Count < 2)
            {
                _filmstrip.style.display = DisplayStyle.None;
                return;
            }

            _filmstrip.style.display = DisplayStyle.Flex;
            // Centre the window on the current item, then pin it inside the
            // selection so the last page is full rather than half empty.
            var current = Mathf.Clamp(_index, 0, _items.Count - 1);
            _stripFirst = Mathf.Clamp(
                current - FilmstripWindow / 2,
                0,
                Mathf.Max(0, _items.Count - FilmstripWindow));
            var last = Mathf.Min(_items.Count, _stripFirst + FilmstripWindow);
            if (_stripFirst > 0)
            {
                AddFilmstripClippedCount(_stripFirst);
            }

            for (var i = _stripFirst; i < last; i++)
            {
                var item = _items[i];
                var tile = new VisualElement { tooltip = item.Name };
                tile.AddToClassList(ZepUiClasses.QuickLookFilmstripTile);

                var icon = new Image
                {
                    scaleMode = ScaleMode.ScaleToFit,
                    image = OwnerIcons.GetAssetIcon(item.Path, item.TypeName),
                };
                icon.AddToClassList(ZepUiClasses.QuickLookFilmstripTileIcon);
                tile.Add(icon);
                _stripThumbs.Add((icon, item.Path, item.TypeName));

                var capturedIndex = i;
                tile.RegisterCallback<ClickEvent>(_ =>
                {
                    _index = capturedIndex;
                    _drillTrail.Clear();
                    ShowCurrent();
                });
                _stripTiles.Add(tile);
                _filmstrip.Add(tile);
            }

            if (last < _items.Count)
            {
                AddFilmstripClippedCount(_items.Count - last);
            }
        }

        // The count of selected items the strip is not rendering at one end.
        private void AddFilmstripClippedCount(int count)
        {
            var label = new Label("+" + count)
            {
                tooltip = count + " more selected",
            };
            label.AddToClassList(ZepUiClasses.QuickLookFilmstripMore);
            _filmstrip.Add(label);
        }

        private void UpdateFilmstripHighlight()
        {
            // Arrows walk the whole selection, not just the rendered window;
            // stepping out of it re-centres the strip on the new item first.
            if (_items.Count > 1
                && (_index < _stripFirst || _index >= _stripFirst + _stripTiles.Count))
            {
                RebuildFilmstrip();
            }

            for (var i = 0; i < _stripTiles.Count; i++)
            {
                _stripTiles[i].EnableInClassList(
                    ZepUiClasses.QuickLookFilmstripTileSelected,
                    _drillTrail.Count == 0 && _stripFirst + i == _index);
            }
        }

        private void DestroyEditor()
        {
            var editor = _editor;
            _editor = null;
            if (editor != null)
            {
                UnityEngine.Object.DestroyImmediate(editor);
            }
        }
    }
}
