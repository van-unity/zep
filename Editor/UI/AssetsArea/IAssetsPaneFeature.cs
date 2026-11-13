using System;
using System.Collections.Generic;
using Zep.Core.Domain;
using UnityEngine;

namespace Zep.Editor.UI.AssetsArea
{
    // The surface AssetsAreaController routes to the feature mounted for the
    // current AssetsPaneMode. Two real implementations: AssetsBrowserFeature
    // (folder browsing — breadcrumb, drops, inline-create) and
    // SmartFolderFeature (Tags flat views). The controller
    // indexes features by mode and talks to the active one through this;
    // browser-only affordances stay on the concrete AssetsBrowserFeature.
    internal interface IAssetsPaneFeature
    {
        IReadOnlyList<ZepAssetEntry> CurrentAssets { get; }

        // What is actually rendered and selectable right now: CurrentAssets
        // plus expanded-folder children in list mode. The pane's
        // visible-assets provider must read THIS, not CurrentAssets, or path
        // selections on expanded child rows resolve to no entries.
        IReadOnlyList<ZepAssetEntry> VisibleAssets { get; }

        // Pull from the wired asset source; no-op when none is wired (tests
        // push directly via the feature's SetAssets).
        void RefreshAssets();

        void SetSelection(IReadOnlyCollection<AssetPath> selectedAssetPaths);

        // Keyboard selection, forwarded to the feature's substrate: select-all,
        // one arrow/home/end/page step, and type-ahead prefix jump.
        void SelectAllAssets();
        bool MoveSelection(Substrate.AssetSelectionMove move, bool extend);
        bool SelectFirstNameMatch(string prefix);

        void SetHasActiveFilter(bool hasActiveFilter);
        void SetViewMode(AssetViewMode mode);
        void SetGridItemSize(int size);
        void ApplyResponsiveWidth(float assetPaneWidth);
        void ApplyResponsiveItemHeights();
        // A null batch means "which icons changed is unknown": rebind all.
        void NotifyIconPreviewsReady(IReadOnlyList<string> paths);
        void CancelInteractions();

        event Action<IReadOnlyList<ZepAssetEntry>> SelectionChanged;
        event Action OpenRequested;
        event Action<Rect, ZepAssetEntry> ContextMenuRequested;
        // Sub-asset rows (list mode): selection/open bypass the path-based
        // selection pipeline — the pane view resolves the sub-object.
        event Action<SubAssetDescriptor> SubAssetSelected;
        event Action<SubAssetDescriptor> SubAssetOpenRequested;
    }
}
