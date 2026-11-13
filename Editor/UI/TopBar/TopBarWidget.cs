using System;
using System.Collections.Generic;
using Zep.Core.Domain;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.TopBar
{
    internal sealed class TopBarWidget : VisualElement
    {
        private readonly VisualElement _headerRow;
        // Recovered seam: leftmost toggle that shows/hides the folder sidebar.
        // The -collapsed modifier dims its icon when the sidebar is hidden (see
        // Zep.uss .zep-sidebar-toggle-collapsed).
        private readonly Button _sidebarToggleButton;
        private readonly NavigationButtonsWidget _navigationButtons;
        private readonly Button _searchButton;
        private readonly Label _searchButtonLabel;
        private string _searchText;

        private readonly VisualElement _typeFilterButton;
        private readonly Label _typeFilterLabel;
        private readonly Button _plusButton;
        private readonly Button _toolbarOverflowButton;

        private readonly VisualElement _searchScopeRow;
        private readonly VisualElement _searchScopeButtons;

        // What the scope row currently shows. The shell recomputes and pushes
        // the row on every refresh tick, so these are what let SetSearchScope
        // tell a real change from the steady state.
        private readonly List<ZepSearchScopeOption> _shownSearchScopeOptions =
            new List<ZepSearchScopeOption>();
        private ZepSearchScope _shownSearchScopeSelection;
        private bool _searchScopeRowVisible;

        internal event Action BackClicked
        {
            add => _navigationButtons.BackClicked += value;
            remove => _navigationButtons.BackClicked -= value;
        }

        internal event Action ForwardClicked
        {
            add => _navigationButtons.ForwardClicked += value;
            remove => _navigationButtons.ForwardClicked -= value;
        }

        internal event Action SearchRequested;
        internal VisualElement SearchScopeSurface => _searchScopeRow;

        internal event Action<MouseDownEvent> TypeFilterButtonMouseDown;
        internal event Action<KeyDownEvent> TypeFilterButtonKeyDown;
        internal event Action PlusClicked;
        internal event Action ToolbarOverflowClicked;

        internal event Action<ZepSearchScope> SearchScopeSelected;

        // Recovered seam: raised when the user clicks the sidebar toggle; the
        // shell subscribes to this to show/hide the folder pane.
        internal event Action SidebarToggleClicked;

        internal TopBarWidget()
        {
            name = "zep-top-bar";
            AddToClassList(ZepUiClasses.TopBar);

            _headerRow = new VisualElement { name = "zep-header-row" };
            _headerRow.AddToClassList(ZepUiClasses.HeaderRow);

            // Recovered seam: build the sidebar toggle and route its click to the
            // SidebarToggleClicked event the shell wires up.
            _sidebarToggleButton = new Button(() => SidebarToggleClicked?.Invoke())
            {
                name = "zep-sidebar-toggle",
                text = string.Empty,
                tooltip = "Toggle sidebar"
            };
            _sidebarToggleButton.AddToClassList(ZepUiClasses.SidebarToggle);
            _sidebarToggleButton.AddToClassList(ZepUiClasses.FlatButton);
            _sidebarToggleButton.AddToClassList(ZepUiClasses.MonochromeIcon);
            AttachPackageIcon(
                _sidebarToggleButton,
                "Packages/com.van-unity.zep/Editor/UI/Icons/sidebar-icon.png",
                ZepUiClasses.CreateButtonIcon);

            _navigationButtons = new NavigationButtonsWidget(
                "zep-folder");
            _searchButton = new Button(() => SearchRequested?.Invoke())
            {
                name = "zep-search-button", text = string.Empty
            };
            _searchButton.AddToClassList(ZepUiClasses.FlatButton);
            _searchButton.AddToClassList(ZepUiClasses.SearchLauncher);
            AttachPackageIcon(_searchButton,
                "Packages/com.van-unity.zep/Editor/UI/Icons/search-icon.png",
                ZepUiClasses.CreateButtonIcon);
            _searchButtonLabel = new Label("Search") { pickingMode = PickingMode.Ignore };
            _searchButtonLabel.AddToClassList(ZepUiClasses.SearchLauncherLabel);
            _searchButton.Add(_searchButtonLabel);
            RefreshSearchTooltip();
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                ZepSearchShortcut.Changed += RefreshSearchTooltip;
                RefreshSearchTooltip();
            });
            RegisterCallback<DetachFromPanelEvent>(_ => ZepSearchShortcut.Changed -= RefreshSearchTooltip);

            var actionRow = new VisualElement { name = "zep-actions" };
            actionRow.AddToClassList(ZepUiClasses.ActionRow);

            var primaryActions = new VisualElement { name = "zep-actions-primary" };
            primaryActions.AddToClassList(ZepUiClasses.ActionPrimaryGroup);

            _typeFilterButton = new VisualElement { name = "zep-type-filter-button", focusable = true };
            _typeFilterButton.AddToClassList(ZepUiClasses.ActionButton);
            _typeFilterButton.AddToClassList(ZepUiClasses.ActionButtonSecondary);
            _typeFilterButton.AddToClassList(ZepUiClasses.CreateButton);
            _typeFilterButton.AddToClassList(ZepUiClasses.MonochromeIcon);
            _typeFilterButton.tooltip = "Filter by type";
            _typeFilterButton.RegisterCallback<MouseDownEvent>(evt => TypeFilterButtonMouseDown?.Invoke(evt));
            _typeFilterButton.RegisterCallback<KeyDownEvent>(evt => TypeFilterButtonKeyDown?.Invoke(evt));

            AttachPackageIcon(_typeFilterButton, "Packages/com.van-unity.zep/Editor/UI/Icons/filter-icon.png", ZepUiClasses.CreateButtonIcon);
            _typeFilterLabel = new Label("Type: All") { pickingMode = PickingMode.Ignore };
            _typeFilterLabel.AddToClassList(ZepUiClasses.ToolbarLabel);
            _typeFilterButton.Add(_typeFilterLabel);

            _plusButton = new Button(() => PlusClicked?.Invoke())
            {
                name = "zep-create-button",
                text = string.Empty,
                tooltip = "Create New..."
            };
            _plusButton.AddToClassList(ZepUiClasses.ActionButton);
            _plusButton.AddToClassList(ZepUiClasses.ActionButtonSecondary);
            _plusButton.AddToClassList(ZepUiClasses.CreateButton);
            _plusButton.AddToClassList(ZepUiClasses.MonochromeIcon);
            AttachPackageIcon(
                _plusButton,
                "Packages/com.van-unity.zep/Editor/UI/Icons/plus-icon.png",
                ZepUiClasses.CreateButtonIcon);
            var createLabel = new Label("Create") { pickingMode = PickingMode.Ignore };
            createLabel.AddToClassList(ZepUiClasses.ToolbarLabel);
            _plusButton.Add(createLabel);
            var createChevron = new VisualElement { pickingMode = PickingMode.Ignore };
            createChevron.AddToClassList(ZepUiClasses.ToolbarChevron);
            createChevron.style.backgroundImage = ZepChevronIcons.GetChevronBackground(false);
            _plusButton.Add(createChevron);

            _toolbarOverflowButton = new Button(() => ToolbarOverflowClicked?.Invoke())
            {
                name = "zep-toolbar-overflow-button",
                text = string.Empty,
                tooltip = "Settings"
            };
            _toolbarOverflowButton.AddToClassList(ZepUiClasses.FlatButton);
            _toolbarOverflowButton.AddToClassList(ZepUiClasses.MonochromeIcon);
            _toolbarOverflowButton.AddToClassList(ZepUiClasses.ToolbarOverflowButton);
            AttachPackageIcon(
                _toolbarOverflowButton,
                "Packages/com.van-unity.zep/Editor/UI/Icons/settings-icon.png",
                ZepUiClasses.CreateButtonIcon);
            _toolbarOverflowButton.style.display = DisplayStyle.None;

            primaryActions.Add(_typeFilterButton);
            primaryActions.Add(_plusButton);
            primaryActions.Add(_toolbarOverflowButton);

            actionRow.Add(primaryActions);

            _headerRow.Add(_sidebarToggleButton);
            _headerRow.Add(_navigationButtons);
            var spacer = new VisualElement();
            spacer.AddToClassList(ZepUiClasses.FlexSpacer);
            _headerRow.Add(spacer);
            _headerRow.Add(_searchButton);
            _headerRow.Add(actionRow);
            Add(_headerRow);
            _headerRow.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                EnableInClassList(ZepUiClasses.TopBarCompact, evt.newRect.width < 560f);
            });

            _searchScopeRow = new VisualElement { name = "zep-search-scope-row" };
            _searchScopeRow.AddToClassList(ZepUiClasses.SearchScopeRow);
            _searchScopeRow.style.display = DisplayStyle.None;
            _searchScopeRow.Add(new Label("Scope") { name = "zep-search-scope-label" }
                .WithClass(ZepUiClasses.FolderDropdownLabel));

            _searchScopeButtons = new VisualElement { name = "zep-search-scope-buttons" };
            _searchScopeButtons.AddToClassList(ZepUiClasses.SearchScopeButtons);
            _searchScopeRow.Add(_searchScopeButtons);
            Add(_searchScopeRow);
        }

        internal void SetCanGoBack(bool canGoBack) => _navigationButtons.SetCanGoBack(canGoBack);
        internal void SetCanGoForward(bool canGoForward) => _navigationButtons.SetCanGoForward(canGoForward);

        internal void SetNavigationVisible(bool visible)
        {
            _navigationButtons.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        internal void SetSearchText(string text)
        {
            _searchText = text;
            _searchButtonLabel.text = string.IsNullOrEmpty(text) ? "Search" : text;
            RefreshSearchTooltip();
        }

        private void RefreshSearchTooltip()
        {
            var current = ZepSearchShortcut.Current;
            var hint = ZepSearchShortcut.GetUnavailableReason(current) == null
                ? " (" + ZepSearchShortcut.Format(current) + ")" : string.Empty;
            _searchButton.tooltip = string.IsNullOrEmpty(_searchText)
                ? "Search assets" + hint : "Edit search: " + _searchText;
        }

        internal void SetTypeFilterButtonTooltip(string tooltip)
        {
            _typeFilterButton.tooltip = string.IsNullOrEmpty(tooltip) ? "Filter by type" : tooltip;
        }

        // Recovered seam: reflect whether the folder sidebar is visible on the
        // toggle. Active (sidebar shown) clears the -collapsed modifier; inactive
        // adds it so the icon dims (see Zep.uss).
        internal void SetSidebarActive(bool active)
        {
            _sidebarToggleButton?.EnableInClassList(ZepUiClasses.SidebarToggleCollapsed, !active);
        }

        // Reflect the active type filter on the type-filter button. The shell sets the
        // tooltip separately; this toggles the button's active-state class, which
        // Zep.uss (#zep-type-filter-button.…-active) paints with the
        // accent surface so a non-"All Types" filter is visibly distinct from the
        // unfiltered state. (Previously this toggled the search-scope-pill selected class,
        // which the button's own id-scoped background silently overrode — a no-op.)
        internal void SetSelectedType(string typeName)
        {
            var hasFilter = !string.IsNullOrWhiteSpace(typeName);
            _typeFilterLabel.text = hasFilter ? "Type: " + typeName : "Type: All";
            _typeFilterButton?.EnableInClassList(ZepUiClasses.TypeFilterButtonActive, hasFilter);
        }

        internal Rect GetTypeFilterButtonWorldBound() => _typeFilterButton.worldBound;

        // Key events target the focused element, and focus lands on the
        // button itself (its icon child is pickingMode.Ignore), so a
        // reference check is exact.
        internal bool IsTypeFilterButtonTarget(IEventHandler target) => ReferenceEquals(target, _typeFilterButton);

        internal bool IsTypeFilterButton(VisualElement target)
        {
            if (target == null)
            {
                return false;
            }

            return target == _typeFilterButton || _typeFilterButton.Contains(target);
        }

        internal void SetCreateEnabled(bool enabled) => _plusButton.SetEnabled(enabled);

        internal void SetPlusVisible(bool visible)
        {
            _plusButton.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        internal void SetToolbarOverflowVisible(bool visible)
        {
            _toolbarOverflowButton.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        internal void SetToolbarOverflowEnabled(bool enabled) => _toolbarOverflowButton.SetEnabled(enabled);

        internal Rect GetPlusButtonWorldBound() => _plusButton.worldBound;
        internal float GetPlusButtonLayoutHeight() => _plusButton.layout.height;

        // The shell pushes this from RefreshView, so it runs on every keystroke
        // while a search is live. Clearing and re-allocating a Button per option
        // each tick is the churn TagsFilterWidget.SetActiveTagId already refuses
        // — rebuild only when the row's content actually differs.
        internal void SetSearchScope(
            IReadOnlyList<ZepSearchScopeOption> options,
            bool hasScopedFilter,
            ZepSearchScope selectedScope)
        {
            var hasOptions = options != null && options.Count > 0;
            if (!hasScopedFilter || !hasOptions)
            {
                if (!_searchScopeRowVisible)
                {
                    return;
                }

                _searchScopeRowVisible = false;
                _searchScopeRow.style.display = DisplayStyle.None;
                _searchScopeButtons.Clear();
                _shownSearchScopeOptions.Clear();
                return;
            }

            if (_searchScopeRowVisible && ShowsSearchScopeOptions(options))
            {
                if (selectedScope != _shownSearchScopeSelection)
                {
                    _shownSearchScopeSelection = selectedScope;
                    ApplySearchScopeSelectedClass();
                }

                return;
            }

            _searchScopeRowVisible = true;
            _shownSearchScopeSelection = selectedScope;
            _searchScopeRow.style.display = DisplayStyle.Flex;

            _searchScopeButtons.Clear();
            _shownSearchScopeOptions.Clear();
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                var optionScope = option.Scope;
                var button = new Button(() => SearchScopeSelected?.Invoke(optionScope))
                {
                    name = $"zep-search-scope-button-{GetSearchScopeName(optionScope)}",
                    text = option.Label,
                    userData = optionScope
                };
                button.AddToClassList(ZepUiClasses.SearchScopeButton);
                if (optionScope == selectedScope)
                {
                    button.AddToClassList(ZepUiClasses.SearchScopeButtonSelected);
                }

                _searchScopeButtons.Add(button);
                _shownSearchScopeOptions.Add(option);
            }
        }

        private bool ShowsSearchScopeOptions(IReadOnlyList<ZepSearchScopeOption> options)
        {
            if (options.Count != _shownSearchScopeOptions.Count)
            {
                return false;
            }

            for (var i = 0; i < options.Count; i++)
            {
                if (!_shownSearchScopeOptions[i].Equals(options[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private void ApplySearchScopeSelectedClass()
        {
            foreach (var child in _searchScopeButtons.Children())
            {
                if (child.userData is not ZepSearchScope scope)
                {
                    continue;
                }

                child.EnableInClassList(
                    ZepUiClasses.SearchScopeButtonSelected,
                    scope == _shownSearchScopeSelection);
            }
        }

        private static string GetSearchScopeName(ZepSearchScope scope)
        {
            switch (scope)
            {
                case ZepSearchScope.All:
                    return "all";
                case ZepSearchScope.Packages:
                    return "packages";
                case ZepSearchScope.CurrentFolder:
                    return "current-folder";
                default:
                    return "assets";
            }
        }

        // Shared by the top-bar widget family (search toggle, nav chevrons).
        internal static void AttachPackageIcon(VisualElement host, string assetPath, string iconClass)
        {
            if (host == null) return;

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (texture == null)
            {
                return;
            }

            var icon = new VisualElement
            {
                name = (host.name ?? "icon") + "-icon",
                pickingMode = PickingMode.Ignore
            };
            icon.AddToClassList(iconClass);
            icon.style.backgroundImage = new StyleBackground(texture);
            host.Insert(0, icon);
        }
    }
}
