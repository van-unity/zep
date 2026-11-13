using System;
using System.Collections.Generic;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.UI.AssetsArea.AssetsBrowser;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    // AssetsAreaController is the composition root for the asset pane. It hosts
    // the per-mode feature widgets and routes inputs from ShellView to the
    // currently active one. It re-emits each feature's outward events under a
    // single shell-facing surface so ShellView wires once, regardless of mode.
    //
    // The mode enum lives in Core.State (AssetsPaneMode) because the state
    // owns per-mode folder paths; the controller mirrors whatever mode the
    // state reports so each feature can keep its own breadcrumb across mode
    // switches.
    //
    // View mode and grid-item size are single-owner state HERE (the toggle and
    // slider live in this controller's floating bar). The active feature gets
    // live updates; hidden features receive the current values when a mode
    // switch activates them. The previous design mirrored every change into
    // all four features eagerly — one slider tick used to trigger up to 8
    // ListView rebuilds (7 of them on hidden views) and one toggle click fired
    // ViewModeChanged five times through re-entrant mirror handlers.
    internal sealed class AssetsAreaController : VisualElement
    {
        private readonly AssetsBrowserFeature _browser;
        private readonly SmartFolderFeature _tags;

        private readonly VisualElement _floatingControls;
        private readonly BreadcrumbWidget _breadcrumb;
        private readonly VisualElement _breadcrumbsDivider;
        private readonly Label _assetCountLabel;
        private readonly SliderInt _assetGridSizeSlider;
        private readonly VisualElement _gridSizeDivider;
        private readonly VisualElement _gridSizeControlContainer;
        private readonly Button _viewListButton;
        private readonly Button _viewGridButton;

        private AssetsPaneMode _mode = AssetsPaneMode.Browser;
        private AssetViewMode _viewMode;
        private int _gridItemSize;
        private float _assetPaneWidth;
        // What the count label currently reads. Seeded to 0 to match the
        // label's construction text ("0 files"), so an empty first refresh
        // correctly formats nothing.
        private int _shownAssetCount;

        internal event Action<AssetViewMode> ViewModeChanged;
        internal event Action<int> GridItemSizeChanged;
        internal event Action<string> BreadcrumbSegmentClicked;
        internal event Action<IReadOnlyList<ZepAssetEntry>> SelectionChanged;
        internal event Action OpenRequested;
        internal event Action<Rect, ZepAssetEntry> ContextMenuRequested;
        internal event Action<SubAssetDescriptor> SubAssetSelected;
        internal event Action<SubAssetDescriptor> SubAssetOpenRequested;

        internal AssetsAreaController(
            AssetsPaneMode initialMode,
            AssetViewMode initialViewMode,
            int initialGridItemSize,
            AssetsBrowserFeature browser = null,
            SmartFolderFeature tags = null,
            bool showFloatingBreadcrumb = true)
        {
            _browser = browser;
            _tags = tags;
            _mode = initialMode;
            _viewMode = initialViewMode;
            _gridItemSize = Mathf.Clamp(
                initialGridItemSize,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize);

            if (FeatureFor(initialMode) == null)
            {
                throw new ArgumentException(
                    "The initial pane mode must have a mounted feature.",
                    nameof(initialMode));
            }

            name = "zep-assets-area";
            AddToClassList(ZepUiClasses.AssetsArea);

            AddFeature(_browser);
            AddFeature(_tags);

            // ---- Bottom bar ----
            // In-flow, not an overlay: it is the last child of this column, so
            // it claims its own 28px row and the active feature sizes to the
            // space above it. It used to be an absolutely-positioned strip that
            // floated over the features, which meant content (asset rows, the
            // empty-state banner) rendered underneath it and every scroll view
            // needed a 44px bottom padding to compensate.
            _floatingControls = new VisualElement { name = "zep-floating-controls" };
            _floatingControls.AddToClassList(ZepUiClasses.FloatingControls);

            var leftGroup = new VisualElement { name = "zep-bottom-left-group" };
            leftGroup.AddToClassList(ZepUiClasses.BottomLeftGroup);

            var rightGroup = new VisualElement { name = "zep-bottom-right-group" };
            rightGroup.AddToClassList(ZepUiClasses.BottomRightGroup);

            // View toggle (List / Grid) + Slider
            var viewToggle = BuildViewModeToggle(out _viewListButton, out _viewGridButton, _viewMode);
            _gridSizeDivider = new VisualElement();
            _gridSizeDivider.AddToClassList(ZepUiClasses.FloatingDivider);
            _gridSizeDivider.AddToClassList(ZepUiClasses.FloatingGridSizeDivider);
            _gridSizeControlContainer = BuildAssetGridSizeControl(out _assetGridSizeSlider, _gridItemSize);

            leftGroup.Add(viewToggle);
            leftGroup.Add(_gridSizeDivider);
            leftGroup.Add(_gridSizeControlContainer);

            if (showFloatingBreadcrumb)
            {
                _breadcrumbsDivider = new VisualElement();
                _breadcrumbsDivider.AddToClassList(ZepUiClasses.FloatingDivider);
                _breadcrumbsDivider.AddToClassList(ZepUiClasses.FloatingBreadcrumbDivider);

                _breadcrumb = new BreadcrumbWidget();
                _breadcrumb.SegmentClicked += OnBreadcrumbSegmentClicked;

                leftGroup.Add(_breadcrumbsDivider);
                leftGroup.Add(_breadcrumb);
            }

            // Count label
            _assetCountLabel = new Label("0 files") { name = "zep-floating-count" };
            _assetCountLabel.AddToClassList(ZepUiClasses.FloatingCount);

            rightGroup.Add(_assetCountLabel);

            // Add groups to controls container
            _floatingControls.Add(leftGroup);
            _floatingControls.Add(rightGroup);

            Add(_floatingControls); // Last child = bottom row of the column.

            ApplyMountVisibility();

            ApplyViewModeButtonState(_viewMode);
            UpdateCountLabel();

            // Forward feature events. Drops are not among them: they belong to
            // the browser feature alone (Tags have no drop targets),
            // so the pane view subscribes to BrowserFeature directly.
            SubscribeFeature(_browser);
            SubscribeFeature(_tags);
        }

        // The feature mounted for a mode. Browser-only affordances (breadcrumb,
        // drops, inline edit, reveal) stay on the typed _browser field.
        private IAssetsPaneFeature FeatureFor(AssetsPaneMode mode)
        {
            switch (mode)
            {
                case AssetsPaneMode.Tags: return _tags;
                default: return _browser;
            }
        }

        private IAssetsPaneFeature Active => FeatureFor(_mode)
            ?? throw new InvalidOperationException("The active pane mode has no mounted feature.");

        // -----------------------------------------------------------------------
        // Setters called by ShellView. SetActiveMode must be called before any
        // path-or-data setters during a refresh: it selects the active feature.
        // -----------------------------------------------------------------------

        internal void SetActiveMode(AssetsPaneMode mode)
        {
            if (FeatureFor(mode) == null)
            {
                return;
            }

            if (_mode == mode)
            {
                UpdateCountLabel();
                return;
            }

            _mode = mode;

            // Apply-on-activation: hidden features don't receive live
            // view-mode/size/width updates, so bring the newly-active one up
            // to date before it becomes visible. All four calls are
            // idempotent no-ops when the value already matches.
            var activated = Active;
            activated.SetViewMode(_viewMode);
            activated.SetGridItemSize(_gridItemSize);
            if (_assetPaneWidth > 0f)
            {
                activated.ApplyResponsiveWidth(_assetPaneWidth);
            }
            activated.ApplyResponsiveItemHeights();

            ApplyMountVisibility();
            UpdateCountLabel();
        }

        internal void SetBreadcrumbPath(string rootPath, string currentPath)
        {
            // Browser is the only mode with a breadcrumb. Tags dropped
            // theirs — their headers stay clean and navigation falls
            // back to back/forward + the sidebar section buttons.
            if (_mode == AssetsPaneMode.Browser)
            {
                _breadcrumb?.SetPath(rootPath, currentPath);
            }
        }

        // Tells the active feature to pull from its wired asset source. The
        // shell uses this in place of SetAssets after Step 7 — the centralized
        // viewModel.Assets stream is gone; each feature owns its data flow.
        internal void RefreshActiveAssets()
        {
            Active.RefreshAssets();
            UpdateCountLabel();
        }

        // Read-only view of the active feature's asset list. Used by the shell
        // for the type-filter chip count fallback and by ShellViewModel for
        // selection-entry resolution.
        internal IReadOnlyList<ZepAssetEntry> ActiveAssets => Active.CurrentAssets;

        // Expansion-aware counterpart of ActiveAssets — see
        // IAssetsPaneFeature.VisibleAssets.
        internal IReadOnlyList<ZepAssetEntry> ActiveVisibleAssets => Active.VisibleAssets;

        internal AssetViewMode CurrentViewMode => _viewMode;

        internal int CurrentGridItemSize => _gridItemSize;

        internal float ResponsiveWidth => _assetPaneWidth;

        internal AssetsBrowserFeature BrowserFeature => _browser;

        internal void SetSelection(IReadOnlyCollection<AssetPath> selectedAssetPaths)
        {
            Active.SetSelection(selectedAssetPaths);
        }

        internal void SelectAllActiveAssets() => Active.SelectAllAssets();

        internal bool MoveActiveSelection(Substrate.AssetSelectionMove move, bool extend)
            => Active.MoveSelection(move, extend);

        internal bool TypeToSelectActiveAsset(string prefix)
            => Active.SelectFirstNameMatch(prefix);

        // Frames an asset into view so a programmatic selection (e.g. the
        // search-exit reveal) is visible, not just selected off-screen. The
        // reveal always lands in the folder browser; other modes have no folder
        // to frame into, so this is a no-op there.
        internal void ScrollActivePathIntoView(string assetPath)
        {
            if (_mode == AssetsPaneMode.Browser)
            {
                _browser.ScrollPathIntoView(assetPath);
            }
        }

        internal void PingActivePath(string assetPath)
        {
            if (_mode == AssetsPaneMode.Browser)
            {
                _browser.PingPath(assetPath);
            }
        }

        internal void PingActiveSubAsset(string parentPath, long subAssetLocalId)
        {
            if (_mode == AssetsPaneMode.Browser)
            {
                _browser.PingSubAsset(parentPath, subAssetLocalId);
            }
        }

        internal void SetHasActiveFilter(bool hasActiveFilter)
        {
            Active.SetHasActiveFilter(hasActiveFilter);
        }

        internal void SetViewMode(AssetViewMode mode)
        {
            _viewMode = mode;
            Active.SetViewMode(mode);
            ApplyViewModeButtonState(mode);
        }

        internal void SetGridItemSize(int size)
        {
            var clamped = Mathf.Clamp(
                size,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize);
            _gridItemSize = clamped;
            _assetGridSizeSlider?.SetValueWithoutNotify(clamped);
            Active.SetGridItemSize(clamped);
        }

        internal void ApplyResponsiveWidth(float assetPaneWidth)
        {
            _assetPaneWidth = assetPaneWidth;
            Active.ApplyResponsiveWidth(assetPaneWidth);
            EnableInClassList(
                ZepUiClasses.AssetPaneChromeCompact,
                assetPaneWidth <= AssetsAreaConstants.SplitPaneChromeCompactBreakpoint);
            EnableInClassList(
                ZepUiClasses.AssetPaneChromeTight,
                assetPaneWidth <= AssetsAreaConstants.SplitPaneChromeTightBreakpoint);
        }

        internal void ApplyResponsiveItemHeights()
        {
            Active.ApplyResponsiveItemHeights();
        }

        // Only the visible feature rebinds; hidden features pick the upgraded
        // icons out of the repository cache the next time they bind anyway.
        // During preview streaming this fires on many consecutive editor
        // ticks, so the ×4 fan-out was the single hottest redundant path.
        internal void NotifyIconPreviewsReady(IReadOnlyList<string> paths)
        {
            Active.NotifyIconPreviewsReady(paths);
        }

        internal void CancelInlineEdit()
        {
            _browser?.CancelInlineEdit();
        }

        internal void CancelInteractions()
        {
            Active.CancelInteractions();
        }

        // -----------------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------------

        private void ApplyMountVisibility()
        {
            SetFeatureDisplay(_browser, _mode == AssetsPaneMode.Browser);
            SetFeatureDisplay(_tags, _mode == AssetsPaneMode.Tags);

            // Class, not inline display: the compact-chrome rule also hides the
            // breadcrumb divider, and an inline value would win over it forever
            // (styling-standards §4). USS composes mode x compact this way.
            var hideBreadcrumb = _mode != AssetsPaneMode.Browser;
            _breadcrumb?.EnableInClassList(ZepUiClasses.FloatingBreadcrumbHidden, hideBreadcrumb);
            _breadcrumbsDivider?.EnableInClassList(ZepUiClasses.FloatingBreadcrumbHidden, hideBreadcrumb);
        }

        private void OnSelectionChanged(IReadOnlyList<ZepAssetEntry> entries)
            => SelectionChanged?.Invoke(entries);

        private void OnOpenRequested() => OpenRequested?.Invoke();

        private void OnContextMenuRequested(Rect anchor, ZepAssetEntry entry)
            => ContextMenuRequested?.Invoke(anchor, entry);

        private void AddFeature(VisualElement feature)
        {
            if (feature != null)
            {
                Add(feature);
            }
        }

        private void SubscribeFeature(IAssetsPaneFeature feature)
        {
            if (feature == null)
            {
                return;
            }

            feature.SelectionChanged += OnSelectionChanged;
            feature.OpenRequested += OnOpenRequested;
            feature.ContextMenuRequested += OnContextMenuRequested;
            feature.SubAssetSelected += OnSubAssetSelected;
            feature.SubAssetOpenRequested += OnSubAssetOpenRequested;
        }

        private void OnSubAssetSelected(SubAssetDescriptor subAsset)
            => SubAssetSelected?.Invoke(subAsset);

        private void OnSubAssetOpenRequested(SubAssetDescriptor subAsset)
            => SubAssetOpenRequested?.Invoke(subAsset);

        private static void SetFeatureDisplay(VisualElement feature, bool visible)
        {
            if (feature != null)
            {
                feature.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void OnBreadcrumbSegmentClicked(string segmentPath)
            => BreadcrumbSegmentClicked?.Invoke(segmentPath);

        // Called from RefreshActiveAssets and SetActiveMode, i.e. on every pane
        // refresh tick. Label.text early-outs on an equal string, so an
        // unchanged count used to allocate an interpolated string purely to
        // throw it away; compare the count instead and format only on change.
        private void UpdateCountLabel()
        {
            var count = ActiveAssets?.Count ?? 0;
            if (count == _shownAssetCount)
            {
                return;
            }

            _shownAssetCount = count;
            _assetCountLabel.text = count == 1 ? "1 file" : $"{count} files";
        }

        private void ApplyViewModeButtonState(AssetViewMode mode)
        {
            _viewListButton?.EnableInClassList(ZepUiClasses.AssetViewToggleButtonActive, mode == AssetViewMode.List);
            _viewGridButton?.EnableInClassList(ZepUiClasses.AssetViewToggleButtonActive, mode == AssetViewMode.Grid);
        }

        private VisualElement BuildAssetGridSizeControl(out SliderInt slider, int initialValue)
        {
            var container = new VisualElement { name = "zep-floating-grid-size-control" };
            container.AddToClassList(ZepUiClasses.AssetGridSizeControl);

            slider = new SliderInt(AssetsAreaConstants.MinAssetGridItemSize, AssetsAreaConstants.MaxAssetGridItemSize)
            {
                name = "zep-floating-grid-size-slider",
                value = Mathf.Clamp(initialValue, AssetsAreaConstants.MinAssetGridItemSize, AssetsAreaConstants.MaxAssetGridItemSize),
                tooltip = "Thumbnail size"
            };
            slider.AddToClassList(ZepUiClasses.AssetGridSizeSlider);
            slider.RegisterValueChangedCallback(OnAssetGridSizeChanged);
            container.Add(slider);
            return container;
        }

        private void OnAssetGridSizeChanged(ChangeEvent<int> evt)
        {
            var clamped = Mathf.Clamp(evt.newValue, AssetsAreaConstants.MinAssetGridItemSize, AssetsAreaConstants.MaxAssetGridItemSize);
            if (_gridItemSize == clamped) return;

            _gridItemSize = clamped;
            Active.SetGridItemSize(clamped);
            GridItemSizeChanged?.Invoke(clamped);
        }

        private VisualElement BuildViewModeToggle(out Button listButton, out Button gridButton, AssetViewMode initialMode)
        {
            var group = new VisualElement { name = "zep-floating-view-toggle" };
            group.AddToClassList(ZepUiClasses.AssetViewToggle);

            listButton = new Button(() => OnViewModeButtonClicked(AssetViewMode.List))
            {
                name = "zep-floating-view-list-button",
                tooltip = "List view",
                text = string.Empty,
            };
            listButton.AddToClassList(ZepUiClasses.AssetViewToggleButton);
            listButton.AddToClassList(ZepUiClasses.FlatButton);
            listButton.AddToClassList(ZepUiClasses.MonochromeIcon);
            AttachPackageIcon(listButton, "Packages/com.van-unity.zep/Editor/UI/Icons/ViewList.png", ZepUiClasses.AssetViewToggleIcon);

            gridButton = new Button(() => OnViewModeButtonClicked(AssetViewMode.Grid))
            {
                name = "zep-floating-view-grid-button",
                tooltip = "Grid view",
                text = string.Empty,
            };
            gridButton.AddToClassList(ZepUiClasses.AssetViewToggleButton);
            gridButton.AddToClassList(ZepUiClasses.FlatButton);
            gridButton.AddToClassList(ZepUiClasses.MonochromeIcon);
            AttachPackageIcon(gridButton, "Packages/com.van-unity.zep/Editor/UI/Icons/ViewGrid.png", ZepUiClasses.AssetViewToggleIcon);

            group.Add(listButton);
            group.Add(gridButton);
            return group;
        }

        private void OnViewModeButtonClicked(AssetViewMode mode)
        {
            if (_viewMode == mode) return;

            SetViewMode(mode);
            ViewModeChanged?.Invoke(mode);
        }

        private static void AttachPackageIcon(VisualElement host, string assetPath, string iconClass)
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
