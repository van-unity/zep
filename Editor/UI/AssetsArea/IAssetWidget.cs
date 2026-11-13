using System;
using Zep.Core.State;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea.Substrate;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    // Contract for the two asset display modes (List, Grid) that
    // AssetCollectionView swaps between. Selection is NOT part of this
    // contract: the substrate owns canonical selection and the composed
    // AssetDragPrepareInteraction/MarqueeInteraction commit into it; widgets
    // only receive SetSelectedPaths pushes and toggle row/tile classes.
    //
    // Marquee (rubber-band) selection is likewise composed externally: the
    // interaction runs in the substrate and the view only answers geometry
    // questions (ComputeMarqueeHitRegion / TryGetMarqueePath) and paints the
    // preview (ApplyMarqueePreview).
    //
    // Everything the substrate asks of "the active view" belongs here, so it
    // can call through one _currentView reference instead of branching on the
    // mode at each site. Sub-asset expansion is NOT widget state: the
    // substrate owns the open set (audit §6.1a) and the ONE
    // VisibleOrderProjection both views read (audit §6.1b), so
    // IsSubAssetExpanded / CollapseSubAssets survive on the widgets only as
    // forwards into that shared projection.
    internal interface IAssetWidget
    {
        // Push new asset data into the view. Implementations may defer the actual
        // rebuild until they next become visible.
        void SetAssets(IReadOnlyList<ZepAssetEntry> assets);

        // Push selection state down; must not raise events.
        void SetSelectedPaths(IReadOnlyCollection<AssetPath> paths);

        // Called when the icon repository has finished rendering async previews.
        // The paths whose icon texture changed, or null when the source of
        // the change cannot name them (rebind everything then).
        void NotifyIconPreviewsReady(IReadOnlyList<string> paths);

        // Recompute row / cell heights from the shared grid-item size state.
        void ApplyResponsiveItemHeights(int gridItemSize, int minGridItemSize, int maxGridItemSize);

        // Raised when the user double-clicks or presses Enter on the selection.
        event Action OpenRequested;

        // Raised on DragUpdated events from the view surface.
        event Action<DragUpdatedEvent> DragUpdated;

        // Raised on DragPerform events from the view surface.
        event Action<DragPerformEvent> DragPerformed;

        // Scroll the row matching path into view; no-op when path is empty or not found.
        void ScrollPathIntoView(string path);

        // Scroll and play the transient Inspector-ping bloom on the matching row.
        void PingPath(string path);

        // Walk up the target element's ancestor chain and resolve the nearest
        // asset entry. Returns false when target is empty space.
        bool TryGetAssetEntryFromTarget(VisualElement target, out ZepAssetEntry assetEntry);

        // The shared VisibleOrderProjection changed under this view: the
        // substrate reconciled it against a new open set, so re-project and
        // REBIND. Both views hold the same projection instance (audit §6.1b),
        // so the substrate reconciles once and tells each view to catch up —
        // which is also why an expansion made in one mode is already open when
        // the other becomes current, and why neither can count the other's
        // cells. A rebind is required, not optional: a changed expansion
        // renumbers every cell after it and each realized row/tile carries a
        // now-stale bound visible index.
        void RefreshVisibleOrder();

        // The view asking the substrate to open (true) or close (false) one
        // disclosure — the row/tile chevron, and the prune of an expansion
        // whose parent resolved to no sub-assets at all. The set is the
        // substrate's, so the answer comes back as RefreshVisibleOrder.
        event Action<string, bool> SubAssetExpansionRequested;

        // The one highlighted sub-asset (by SubAssetDescriptor key), or null /
        // empty to clear it. Mirrored into BOTH views by the substrate so the
        // highlight survives a mode switch.
        void SetSubAssetHighlight(string subAssetKey);

        // Scroll to and bloom the sub-asset row/tile of parentPath. Deferred
        // by the view until the row binds when the children are not there yet.
        void PingSubAsset(string parentPath, string subAssetKey);

        // Walk up target's ancestors and resolve the sub-asset cell it sits
        // in. False when target is a main row/tile or empty space.
        bool TryGetSubAssetFromTarget(VisualElement target, out SubAssetDescriptor subAsset);

        // True when target is (inside) the disclosure chevron, which owns the
        // click instead of the row's selection.
        bool IsDisclosureTarget(VisualElement target);

        // Paint the drag-over folder highlight; empty clears it.
        void SetDropTargetFolderPath(string folderPath);

        // --- Visible order -------------------------------------------------
        // The flat VISIBLE order the keyboard cursor, the marquee and the
        // scroll calls move in. The order itself is the shared
        // VisibleOrderProjection's; these members are the view's own reading of
        // it (the cursor one folds in the sub-asset highlight the view paints).
        // The substrate's own order is the path-keyed asset list; these
        // translate between the two.

        // Cells in the visible order (assets plus expanded sub-asset cells).
        int VisibleItemCount { get; }

        // Visible index → main-asset index. subAsset is non-null (and the
        // return value its parent's asset index) for a sub-asset cell.
        int ResolveVisibleIndex(int visibleIndex, out SubAssetDescriptor subAsset);

        // Where the cursor sits: on the highlighted sub-asset cell when one is
        // selected (a sub-asset has no path for the substrate's lead to point
        // at), otherwise on leadAssetIndex's own cell. -1 when neither
        // resolves; pass -1 to ask for the sub cursor alone.
        int ResolveCursorVisibleIndex(int leadAssetIndex);

        void ScrollVisibleIndexIntoView(int visibleIndex);

        // Row/tile pitch, and the view's scroller — the substrate's page step
        // and its drag auto-scroll read both.
        float FixedItemHeight { get; }
        ScrollView ScrollView { get; }

        // The cells a marquee rectangle covers, in visible-index space.
        MarqueeHitRegion ComputeMarqueeHitRegion(Rect worldRect);

        // The selectable path at one visible cell; false for a sub-asset cell
        // (they occupy geometry but never join a path selection) or an empty one.
        bool TryGetMarqueePath(int visibleIndex, out string path);

        // Mid-drag marquee preview, as the delta since the previous move.
        void ApplyMarqueePreview(IReadOnlyList<string> added, IReadOnlyList<string> removed);
    }

}
