using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Editor.UI.AssetsArea.Substrate;
using Zep.Editor.UI.ContextMenu;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    internal sealed class SmartFolderFeature : VisualElement, IAssetsPaneFeature
    {
        internal struct Options
        {
            // Element name, also used for
            // the surface / empty-state child names ("<name>-surface", …).
            public string ElementName;
            public string EmptyMessage;
            public string FilteredEmptyMessage;
            public Func<IReadOnlyList<ZepAssetEntry>> AssetSource;
            public Func<string, string, AssetPresentationData> PresentationLookup;
        }

        private readonly AssetCollectionView _collection;
        private readonly SurfaceGestureRouter _gestures;

        private readonly VisualElement _assetSurface;
        private readonly Label _emptyStateLabel;

        private readonly string _emptyMessage;
        private readonly string _filteredEmptyMessage;
        private readonly Func<IReadOnlyList<ZepAssetEntry>> _assetSource;

        private IReadOnlyList<ZepAssetEntry> _canonicalAssets =
            Array.Empty<ZepAssetEntry>();

        private bool _hasActiveFilter;

        public event Action<IReadOnlyList<ZepAssetEntry>> SelectionChanged
        {
            add => _collection.SelectionChanged += value;
            remove => _collection.SelectionChanged -= value;
        }
        public event Action OpenRequested
        {
            add => _collection.OpenRequested += value;
            remove => _collection.OpenRequested -= value;
        }

        public event Action<SubAssetDescriptor> SubAssetSelected
        {
            add => _collection.SubAssetSelected += value;
            remove => _collection.SubAssetSelected -= value;
        }

        public event Action<SubAssetDescriptor> SubAssetOpenRequested
        {
            add => _collection.SubAssetOpenRequested += value;
            remove => _collection.SubAssetOpenRequested -= value;
        }
        public event Action<Rect, ZepAssetEntry> ContextMenuRequested;

        internal SmartFolderFeature(
            UnityZepIconRepository iconRepository,
            AssetViewMode initialViewMode,
            int initialGridItemSize,
            Options options)
        {
            if (iconRepository == null) throw new ArgumentNullException(nameof(iconRepository));
            _assetSource = options.AssetSource;
            _emptyMessage = options.EmptyMessage ?? string.Empty;
            _filteredEmptyMessage = options.FilteredEmptyMessage ?? _emptyMessage;

            name = options.ElementName ?? "zep-smart-folder-feature";
            AddToClassList(ZepUiClasses.MainPane);

            // ---- Asset surface ----
            _assetSurface = new VisualElement { name = name + "-surface" };
            _assetSurface.AddToClassList(ZepUiClasses.AssetSurface);
            // Context gestures only; the mouse-down/move/up/capture-out
            // quartet belongs to SurfaceGestureRouter, registered on this same
            // element once the collection exists.
            _assetSurface.RegisterCallback<PointerDownEvent>(OnAssetSurfacePointerDown, TrickleDown.TrickleDown);

            _collection = new AssetCollectionView(
                iconRepository,
                // Smart folders have no inline-edit affordances; the substrate
                // requires a controller and an idle instance is fine.
                new AssetsAreaInlineEditController(),
                initialViewMode,
                initialGridItemSize,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize,
                options.PresentationLookup);
            _assetSurface.Add(_collection);

            _emptyStateLabel = new Label(_emptyMessage) { name = name + "-empty-state" };
            _emptyStateLabel.AddToClassList(ZepUiClasses.EmptyState);
            _assetSurface.Add(_emptyStateLabel);

            Add(_assetSurface);

            _gestures = new SurfaceGestureRouter(
                _assetSurface,
                _collection);

            RefreshChrome();
        }

        // -----------------------------------------------------------------------
        // IAssetsPaneFeature (routed via AssetsAreaController)
        // -----------------------------------------------------------------------

        public IReadOnlyList<ZepAssetEntry> CurrentAssets => _canonicalAssets;

        public IReadOnlyList<ZepAssetEntry> VisibleAssets => _collection.VisibleAssets;

        public void RefreshAssets()
        {
            if (_assetSource == null) return;
            SetAssets(_assetSource());
        }

        internal void SetAssets(IReadOnlyList<ZepAssetEntry> assets)
        {
            _canonicalAssets = assets ?? Array.Empty<ZepAssetEntry>();
            _collection.SetAssets(_canonicalAssets);
            RefreshChrome();
        }

        public void SetSelection(IReadOnlyCollection<AssetPath> selectedAssetPaths)
        {
            _collection.SetSelection(selectedAssetPaths);
        }

        public void SelectAllAssets() => _collection.SelectAllAssets();

        public bool MoveSelection(AssetSelectionMove move, bool extend)
            => _collection.MoveSelection(move, extend);

        public bool SelectFirstNameMatch(string prefix)
            => _collection.SelectFirstNameMatch(prefix);

        public void SetHasActiveFilter(bool hasActiveFilter)
        {
            if (_hasActiveFilter == hasActiveFilter) return;
            _hasActiveFilter = hasActiveFilter;
            RefreshChrome();
        }

        public void SetViewMode(AssetViewMode mode)
        {
            _collection.SetViewMode(mode);
            RefreshChrome();
        }

        internal AssetViewMode CurrentViewMode => _collection.CurrentViewMode;
        internal int CurrentGridItemSize => _collection.CurrentGridItemSize;

        public void SetGridItemSize(int size)
        {
            var clamped = Mathf.Clamp(size, AssetsAreaConstants.MinAssetGridItemSize, AssetsAreaConstants.MaxAssetGridItemSize);
            if (_collection.CurrentGridItemSize == clamped) return;

            _collection.SetGridItemSize(clamped, AssetsAreaConstants.MinAssetGridItemSize, AssetsAreaConstants.MaxAssetGridItemSize);
            ApplyResponsiveItemHeights();
        }

        public void ApplyResponsiveWidth(float assetPaneWidth)
        {
            if (assetPaneWidth <= 0f) return;

            EnableInClassList(ZepUiClasses.LayoutAssetCompact, assetPaneWidth <= AssetsAreaConstants.AssetPaneCompactBreakpoint);
            EnableInClassList(ZepUiClasses.LayoutAssetNarrow,  assetPaneWidth <= AssetsAreaConstants.AssetPaneNarrowBreakpoint);
            EnableInClassList(ZepUiClasses.LayoutAssetTight,   assetPaneWidth <= AssetsAreaConstants.AssetPaneTightBreakpoint);
        }

        public void ApplyResponsiveItemHeights()
        {
            _collection.ApplyResponsiveItemHeights(
                _collection.CurrentGridItemSize,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize);
        }

        public void NotifyIconPreviewsReady(IReadOnlyList<string> paths)
            => _collection.NotifyIconPreviewsReady(paths);

        public void CancelInteractions()
        {
            _gestures.Cancel();
        }

        internal bool TryGetAssetEntryFromTarget(VisualElement target, out ZepAssetEntry assetEntry)
            => _collection.TryGetAssetEntryFromTarget(target, out assetEntry);

        // -----------------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------------

        private void RefreshChrome()
        {
            var hasAssets = _canonicalAssets.Count > 0;

            _emptyStateLabel.style.display = hasAssets ? DisplayStyle.None : DisplayStyle.Flex;
            _emptyStateLabel.text = _hasActiveFilter ? _filteredEmptyMessage : _emptyMessage;
        }

        private void OnAssetSurfacePointerDown(PointerDownEvent evt)
        {
            if (!ZepContextMenuInput.IsPointerTrigger(evt))
            {
                return;
            }

            _gestures.CancelDragPrepare();
            // Sub-asset rows have no context menu (no rename/delete/tag ops
            // apply); consume the gesture so it doesn't read as empty space.
            if (_collection.TryGetSubAssetFromTarget(evt.target as VisualElement, out _))
            {
                ZepContextMenuInput.Consume(evt);
                return;
            }

            _collection.TryGetAssetEntryFromTarget(
                evt.target as VisualElement,
                out var contextEntry);
            if (contextEntry != null)
            {
                ContextMenuRequested?.Invoke(
                    ZepContextMenuInput.PointerAnchor(evt),
                    contextEntry);
            }

            ZepContextMenuInput.Consume(evt);
        }

    }
}
