using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.ContextMenu
{
    // One level of the custom context menu: a frameless popup window that draws
    // a list of ZepContextMenuItem rows with the panel's own style
    // sheets and active theme. It reports hover, click and key input to the
    // owning ContextMenuPopupRenderer session and never decides anything itself.
    internal sealed class ContextMenuPopupWindow : EditorWindow
    {
        // Mirrors the .zep-menu* rules in Zep.uss. The window has to be
        // sized before UI Toolkit lays anything out, so the estimate is computed
        // from these; the first geometry pass corrects any drift.
        private const float RowHeight = 24f;
        private const float SeparatorHeight = 11f;
        private const float ContentPadding = 6f; // 1px border + 5px padding
        private const float RowSidePadding = 12f; // 4px left + 8px right
        private const float CheckColumnWidth = 16f;
        private const float SwatchWidth = 17f;
        private const float IconWidth = 20f;
        private const float LabelToHotkeyGap = 16f;
        private const float ChevronWidth = 12f;
        private const float MinWidth = 180f;
        private const float MaxWidth = 440f;

        internal const float TopPadding = ContentPadding;

        private const string MenuClass = "zep-menu";
        private const string ScrollClass = "zep-menu__scroll";
        private const string ItemClass = "zep-menu-item";
        private const string ItemActiveClass = "zep-menu-item--active";
        private const string ItemDisabledClass = "zep-menu-item--disabled";
        private const string ItemDestructiveClass = "zep-menu-item--destructive";
        private const string CheckClass = "zep-menu-item__check";
        private const string SwatchClass = "zep-menu-item__swatch";
        private const string IconClass = "zep-menu-item__icon";
        private const string LabelClass = "zep-menu-item__label";
        private const string HotkeyClass = "zep-menu-item__hotkey";
        private const string ChevronClass = "zep-menu-item__chevron";
        private const string SeparatorClass = "zep-menu-separator";

        private ContextMenuPopupRenderer.Session _session;
        private ZepShellView _shell;
        private ZepPaneWindow _paneWindow;
        private ZepThemeSwitcher _themeSwitcher;
        private VisualElement _root;
        // Holds the rows. A level taller than the screen is capped to it by the
        // screen fit, and the rows that no longer fit scroll here.
        private ScrollView _scroll;
        private readonly List<VisualElement> _rows = new List<VisualElement>();
        private IReadOnlyList<ZepContextMenuItem> _items = Array.Empty<ZepContextMenuItem>();
        // The displayed hotkey per item, formatted once when the level's items
        // are handed over: both the size estimate and the row build need it.
        private string[] _hotkeyTexts = Array.Empty<string>();
        private int _activeIndex = -1;

        // Reused across every measured string of this level: the style differs
        // only by font size, and building one per string allocated a GUIStyle
        // per menu row. Per window, so a skin change cannot leave it stale.
        private GUIStyle _measureStyle;

        internal int Level { get; private set; }

        // The desktop rect the session placed this level at. Read this, never
        // position: right after ShowPopup Unity reports a popup's position as
        // (0, 0), and re-fitting from that threw a tall menu to the top-left
        // corner of the screen.
        internal Rect ScreenRect { get; set; }
        internal int OpenChildIndex { get; set; } = -1;
        internal int ActiveIndex => _activeIndex;
        internal IReadOnlyList<ZepContextMenuItem> Items => _items;

        internal static ContextMenuPopupWindow Create(
            ContextMenuPopupRenderer.Session session,
            ZepShellView shell,
            IReadOnlyList<ZepContextMenuItem> items,
            int level,
            ZepPaneWindow paneWindow = null)
        {
            var window = CreateInstance<ContextMenuPopupWindow>();
            window._session = session;
            window._shell = shell;
            window._paneWindow = paneWindow;
            window._items = items ?? Array.Empty<ZepContextMenuItem>();
            window._hotkeyTexts = FormatHotkeys(window._items);
            window.Level = level;
            window.titleContent = new GUIContent("Context Menu");
            window.BuildUi();
            return window;
        }

        internal Vector2 EstimateSize()
        {
            var height = ContentPadding * 2f;
            var widest = 0f;
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.IsSeparator)
                {
                    height += SeparatorHeight;
                    continue;
                }

                height += RowHeight;
                var width = RowSidePadding + CheckColumnWidth + MeasureText(item.Label, 12);
                if (item.IconTagColor.HasValue)
                {
                    width += SwatchWidth;
                }
                else if (!string.IsNullOrEmpty(item.LeadingIcon))
                {
                    width += IconWidth;
                }

                if (item.HasChildren)
                {
                    width += LabelToHotkeyGap + ChevronWidth;
                }
                else
                {
                    var hotkey = _hotkeyTexts[i];
                    if (hotkey.Length > 0)
                    {
                        width += LabelToHotkeyGap + MeasureText(hotkey, 11);
                    }
                }

                widest = Mathf.Max(widest, width);
            }

            var total = Mathf.Clamp(Mathf.Ceil(widest + ContentPadding * 2f), MinWidth, MaxWidth);
            return new Vector2(total, Mathf.Ceil(height));
        }

        private static string[] FormatHotkeys(IReadOnlyList<ZepContextMenuItem> items)
        {
            if (items.Count == 0)
            {
                return Array.Empty<string>();
            }

            var texts = new string[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                texts[i] = item.IsSeparator || item.HasChildren
                    ? string.Empty
                    : ContextMenuHotkeyText.Format(item.Hotkey);
            }

            return texts;
        }

        private float MeasureText(string text, int fontSize)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0f;
            }

            try
            {
                var style = _measureStyle ??= new GUIStyle(EditorStyles.label);
                style.fontSize = fontSize;
                // Menu glyphs (⌘, ⇧, ⌫) fall back to a wider font than the
                // label face; pad so the hotkey column never wraps.
                return style.CalcSize(new GUIContent(text)).x + 4f;
            }
            catch (Exception)
            {
                return text.Length * (fontSize * 0.6f) + 4f;
            }
        }

        internal void SetActive(int index)
        {
            if (index == _activeIndex)
            {
                return;
            }

            if (_activeIndex >= 0 && _activeIndex < _rows.Count)
            {
                _rows[_activeIndex]?.RemoveFromClassList(ItemActiveClass);
            }

            _activeIndex = index;
            if (_activeIndex >= 0 && _activeIndex < _rows.Count)
            {
                _rows[_activeIndex]?.AddToClassList(ItemActiveClass);
            }
        }

        internal bool IsSelectable(int index)
        {
            return index >= 0
                && index < _items.Count
                && !_items[index].IsSeparator
                && _items[index].IsEnabled;
        }

        // Desktop-space top of a row; the popup's UI Toolkit origin is the
        // window origin because it has no tab bar.
        internal float RowScreenTop(int index)
        {
            if (index < 0 || index >= _rows.Count || _rows[index] == null)
            {
                return ScreenRect.y;
            }

            return ScreenRect.y + _rows[index].worldBound.y;
        }

        // Keyboard navigation can land on a row scrolled out of view.
        internal void RevealActive()
        {
            if (_activeIndex >= 0 && _activeIndex < _rows.Count && _rows[_activeIndex] != null)
            {
                _scroll?.ScrollTo(_rows[_activeIndex]);
            }
        }

        internal void FocusRoot()
        {
            _root?.Focus();
        }

        private void OnEnable()
        {
            // Layout restoration does not restore the menu session. Wait until
            // Create has had a chance to bind it before dismissing an orphan.
            EditorApplication.update += CloseIfSessionMissing;
        }

        private void CloseIfSessionMissing()
        {
            EditorApplication.update -= CloseIfSessionMissing;
            if (this != null && _session == null)
            {
                Close();
            }
        }

        // A user theme has no sheet on disk, so the switcher builds one in
        // memory: a HideAndDontSave StyleSheet only DestroyImmediate frees.
        // Release it here, not in OnDestroy, because a domain reload disables
        // the window without destroying it (same reason as
        // ZepQuickLookWindow.OnDisable). OnPanelThemeChanged is
        // null-safe afterwards.
        private void OnDisable()
        {
            EditorApplication.update -= CloseIfSessionMissing;
            _themeSwitcher?.Teardown();
            _themeSwitcher = null;
        }

        // The ThemeApplied unsubscribe and the session callback stay on the
        // destroy leg: the subscription is made once in BuildUi (which OnEnable
        // never re-runs), and OnWindowDestroyed is the session's "this level is
        // gone" bookkeeping — it closes the whole menu once the last window
        // goes, which a disable must not do.
        private void OnDestroy()
        {
            if (_shell != null)
            {
                _shell.ThemeApplied -= OnPanelThemeChanged;
            }
            if (_paneWindow != null)
            {
                _paneWindow.ThemeApplied -= OnPanelThemeChanged;
            }

            _session?.OnWindowDestroyed(this);
        }

        private void OnLostFocus()
        {
            _session?.OnWindowLostFocus();
        }

        private void OnFocus()
        {
            _root?.Focus();
        }

        private void OnPanelThemeChanged(string themeId)
        {
            _themeSwitcher?.SetTheme(themeId, persistPreference: false);
        }

        private void BuildUi()
        {
            rootVisualElement.Clear();

            _root = new VisualElement { focusable = true };
            _root.AddToClassList(ZepUiClasses.Root);
            _root.AddToClassList(MenuClass);
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

            // Own switcher, same registry as the shell: mirrors the active theme
            // and follows live changes. Never persists a preference.
            _themeSwitcher = new ZepThemeSwitcher(
                _root, _shell?.Registry ?? _paneWindow.ThemeRegistry, null, null);
            _themeSwitcher.SetTheme(
                (_shell?.ThemeSwitcher ?? _paneWindow.ThemeSwitcher).CurrentTheme,
                persistPreference: false);
            if (_shell != null) _shell.ThemeApplied += OnPanelThemeChanged;
            if (_paneWindow != null) _paneWindow.ThemeApplied += OnPanelThemeChanged;

            _root.RegisterCallback<KeyDownEvent>(evt => _session?.OnKey(evt), TrickleDown.TrickleDown);
            _root.RegisterCallback<PointerEnterEvent>(_ => _session?.OnPointerEnteredWindow(this));
            _root.RegisterCallback<PointerLeaveEvent>(_ => _session?.OnPointerLeftWindow(this));

            _scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
            };
            _scroll.AddToClassList(ScrollClass);
            _root.Add(_scroll);
            // The content container keeps the rows' natural height even when
            // the window is capped shorter, so the session can compare it with
            // the window height it chose.
            _scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(evt =>
                _session?.OnWindowLaidOut(this, evt.newRect.height + ContentPadding * 2f));

            rootVisualElement.Add(_root);

            for (var i = 0; i < _items.Count; i++)
            {
                _rows.Add(BuildRow(_items[i], i));
            }
        }

        private VisualElement BuildRow(ZepContextMenuItem item, int index)
        {
            if (item.IsSeparator)
            {
                var separator = new VisualElement();
                separator.AddToClassList(SeparatorClass);
                separator.pickingMode = PickingMode.Ignore;
                _scroll.Add(separator);
                return separator;
            }

            var row = new VisualElement();
            row.AddToClassList(ItemClass);
            if (!item.IsEnabled)
            {
                row.AddToClassList(ItemDisabledClass);
            }

            if (item.IsDestructive)
            {
                row.AddToClassList(ItemDestructiveClass);
            }

            var check = new Label(item.IsChecked ? "✓" : string.Empty);
            check.AddToClassList(CheckClass);
            check.pickingMode = PickingMode.Ignore;
            row.Add(check);

            if (item.IconTagColor.HasValue)
            {
                var dot = new VisualElement();
                dot.AddToClassList(SwatchClass);
                dot.pickingMode = PickingMode.Ignore;
                ZepTagColorPalette.ApplyBackgroundColor(dot, item.IconTagColor.Value);
                row.Add(dot);
            }
            else if (!string.IsNullOrEmpty(item.LeadingIcon))
            {
                var icon = new Label(item.LeadingIcon);
                icon.AddToClassList(IconClass);
                icon.pickingMode = PickingMode.Ignore;
                row.Add(icon);
            }

            var label = new Label(item.Label);
            label.AddToClassList(LabelClass);
            label.pickingMode = PickingMode.Ignore;
            row.Add(label);

            if (item.HasChildren)
            {
                var chevron = new VisualElement();
                chevron.AddToClassList(ChevronClass);
                chevron.pickingMode = PickingMode.Ignore;
                row.Add(chevron);
            }
            else
            {
                var hotkey = _hotkeyTexts[index];
                if (hotkey.Length > 0)
                {
                    var key = new Label(hotkey);
                    key.AddToClassList(HotkeyClass);
                    key.pickingMode = PickingMode.Ignore;
                    row.Add(key);
                }
            }

            row.RegisterCallback<PointerEnterEvent>(_ => _session?.OnRowHover(this, index));
            row.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                _session?.OnRowActivate(this, index);
            });

            _scroll.Add(row);
            return row;
        }
    }
}
