using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.UI.AssetsArea.Substrate;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    internal enum ZepSplitPaneLifecycle
    {
        Opening,
        Open,
        Closing,
        Replacing
    }

    // Owns the ordered visual slots for the permanent primary pane and every
    // auxiliary. Pane state remains in ZepState; this type owns only
    // UI lifecycle, normalized geometry, focus transfer, and pane-view wiring.
    internal sealed class ZepSplitWorkspaceView : VisualElement
    {
        internal const string WidthFeedback =
            "Widen the window or hide the sidebar to use Split View.";
        internal const int OpenMotionMs = 200;
        internal const int CloseMotionMs = 170;
        internal const int ReplaceOutMotionMs = 100;
        internal const int ReplaceInMotionMs = 160;
        internal const int IndicatorMotionMs = 120;
        // Must match the split-drop-ghost width transition in Zep.uss.
        internal const int DropGhostMotionMs = 180;

        // Also read by SplitDropPresentation: the drop ghost and the width
        // motion present on the same next-tick and fallback schedule.
        internal const int NextPanelTickMs = 16;
        internal const int TransitionFallbackBufferMs = 80;
        private static readonly StylePropertyName WidthStyleProperty =
            new StylePropertyName("width");
        private static readonly StylePropertyName OpacityStyleProperty =
            new StylePropertyName("opacity");

        // Counts the slots whose chrome RefreshChrome actually repainted. The
        // shell pushes chrome on every global publication and the view pushes
        // it again on pane activation and update, so "how many of those pushes
        // wrote anything" is the discriminating measurement.
        internal static int SlotChromeWritesForTests;

        private readonly ZepState _panelState;
        private readonly UnityZepIconRepository _iconRepository;
        private readonly ZepController _controller;
        private readonly Action<ZepPaneState, IReadOnlyList<ZepAssetEntry>>
            _selectionAction;
        private readonly Action<string> _showFeedback;
        private readonly AssetDropOperations _dropOps;
        private readonly List<PaneSlot> _slots = new List<PaneSlot>();
        private readonly List<ZepPaneSplitterWidget> _dividers =
            new List<ZepPaneSplitterWidget>();
        // The pane width shares and every rule about them live in Core.
        private readonly SplitPaneWeights _weights = new SplitPaneWeights();
        private readonly Dictionary<ZepPaneState, PaneSlot> _retiringSlots =
            new Dictionary<ZepPaneState, PaneSlot>();
        private readonly Dictionary<ZepPaneState, ZepPaneView>
            _retiringViews =
                new Dictionary<ZepPaneState, ZepPaneView>();
        // Owns the drop overlay, the drop ghost and the drop pulse: their
        // elements, their present/retire schedules and the generations that
        // keep an interrupted step from resurrecting a newer one.
        private readonly SplitDropPresentation _dropPresentation;

        private bool _isSubscribed;
        private bool _isTornDown;
        private bool _hasPendingPresentation;
        private AssetViewMode _pendingViewMode;
        private int _pendingGridItemSize;
        private float _availableWidth;
        private int _activeDividerIndex = -1;
        private float _dividerLeftWidthAtStart;
        private float _dividerPairWidthAtStart;
        // The pane that was active BEFORE the structural change currently
        // being handled. ZepState assigns ActivePane before it raises
        // Replaced/Closed, so by the time those reach us the state already
        // holds the successor: only this mirror can still answer "was the pane
        // that is going away the active one?" (the focus transfer below) and
        // "which live pane's presentation should the arriving pane inherit?".
        // Every other active-pane question reads ZepState directly.
        private ZepPaneState _paneActiveBeforeChange;
        private ZepSplitDragPayload _previewPayload;
        private ZepPaneSource _previewSource;
        private bool _previewSourceIsValid;
        private VisualElement _terminalEventRoot;
        private IVisualElementScheduledItem _presentationSnapshotTask;
        private IVisualElementScheduledItem _widthStartTask;
        private IVisualElementScheduledItem _widthFallbackTask;
        private PaneSlot _widthMotionAnchor;
        private ZepPaneState _widthRetiredPane;
        private bool _isWidthMotionActive;
        private int _widthMotionGeneration;
        private int _presentationSnapshotGeneration;

        internal ZepSplitWorkspaceView(
            ZepState panelState,
            UnityZepIconRepository iconRepository,
            ZepController controller = null,
            Action<ZepPaneState, IReadOnlyList<ZepAssetEntry>>
                selectionAction = null,
            Action<string> showFeedback = null,
            AssetDropOperations dropOps = null)
        {
            _panelState = panelState ?? throw new ArgumentNullException(nameof(panelState));
            _iconRepository = iconRepository
                ?? throw new ArgumentNullException(nameof(iconRepository));
            _controller = controller;
            _selectionAction = selectionAction;
            _showFeedback = showFeedback;
            // Handed straight to every pane view: the panes execute their own
            // drops, so this view has no drop relay of its own to maintain.
            _dropOps = dropOps;

            name = "zep-split-workspace";
            AddToClassList(ZepUiClasses.SplitWorkspace);

            InsertSlot(0, _panelState.PrimaryPane, null, null);
            for (var i = 0; i < _panelState.AuxiliaryPanes.Count; i++)
            {
                var inherited = _slots[_slots.Count - 1].View;
                InsertSlot(
                    _slots.Count,
                    _panelState.AuxiliaryPanes[i],
                    inherited.ViewMode,
                    inherited.GridItemSize);
            }

            _paneActiveBeforeChange = IndexOfPane(_panelState.ActivePane) >= 0
                ? _panelState.ActivePane
                : _panelState.PrimaryPane;
            ApplyWeightStyles();
            // Built after the initial slots, so its overlay is appended after
            // the same children as before, and before the first RefreshChrome,
            // which asks it whether a ghost is attached.
            _dropPresentation = new SplitDropPresentation(
                this,
                () => _slots.Count,
                () => _dividers.Count,
                () => _availableWidth,
                SettleUnsettledMotion,
                OnSplitDropGhostRetired);
            RefreshChrome();
            Bind();

            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RegisterCallback<DragEnterEvent>(
                OnSplitDragEnter,
                TrickleDown.TrickleDown);
            RegisterCallback<DragUpdatedEvent>(
                OnSplitDragUpdated,
                TrickleDown.TrickleDown);
            RegisterCallback<DragPerformEvent>(
                OnSplitDragPerform,
                TrickleDown.TrickleDown);
            RegisterCallback<DragLeaveEvent>(
                OnSplitDragLeave,
                TrickleDown.TrickleDown);
            RegisterCallback<DragExitedEvent>(
                OnSplitDragExited,
                TrickleDown.TrickleDown);
            RegisterCallback<MouseLeaveWindowEvent>(
                OnSplitMouseLeaveWindow,
                TrickleDown.TrickleDown);
            RegisterCallback<KeyDownEvent>(
                OnSplitCancelKeyDown,
                TrickleDown.TrickleDown);
        }

        internal event Action<ZepPaneState> OpenRequested;
        internal event Action ArrangementChanged;
        internal event Action<ZepPaneState, Rect, ZepAssetEntry>
            ContextMenuRequested;

        internal int PaneCount => _slots.Count;

        internal int DividerCount => _dividers.Count;

        internal int RetiringPaneCount => _retiringSlots.Count + _retiringViews.Count;

        internal bool HasPendingMotion => _isWidthMotionActive
            || _dropPresentation.HasPendingWork
            || HasReplacingSlot();

        internal IReadOnlyList<float> PaneWeights => _weights.Values;

        internal bool IsSplitDropPreviewVisible =>
            _dropPresentation.IsPreviewVisible;

        internal bool IsSplitDropPreviewRejected =>
            _dropPresentation.IsPreviewRejected;

        internal string SplitDropPreviewText => _dropPresentation.PreviewText;

        internal ZepPaneView PrimaryPaneView => GetPaneView(_panelState.PrimaryPane);

        internal ZepSplitPaneLifecycle GetPaneLifecycle(
            ZepPaneState pane)
        {
            if (pane == null)
            {
                throw new ArgumentNullException(nameof(pane));
            }

            var index = IndexOfPane(pane);
            if (index >= 0)
            {
                return _slots[index].Lifecycle;
            }

            if (_retiringSlots.TryGetValue(pane, out var retiring))
            {
                return retiring.Lifecycle;
            }

            throw new ArgumentException(
                "The pane has no live or retiring visual slot.",
                nameof(pane));
        }

        internal ZepPaneView GetPaneView(ZepPaneState pane)
        {
            var index = IndexOfPane(pane);
            return index >= 0 ? _slots[index].View : null;
        }

        internal bool TryGetPanePresentation(
            ZepPaneState pane,
            out AssetViewMode viewMode,
            out int gridItemSize)
        {
            var view = GetPaneView(pane);
            if (view == null)
            {
                viewMode = default;
                gridItemSize = 0;
                return false;
            }

            viewMode = view.ViewMode;
            gridItemSize = view.GridItemSize;
            return true;
        }

        internal void ApplyRestoredLayout(
            IReadOnlyDictionary<ZepPaneState, AssetViewMode> viewModes,
            IReadOnlyDictionary<ZepPaneState, int> gridItemSizes,
            IReadOnlyList<float> weights)
        {
            for (var i = 1; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (viewModes != null
                    && viewModes.TryGetValue(slot.Pane, out var viewMode)
                    && gridItemSizes != null
                    && gridItemSizes.TryGetValue(slot.Pane, out var gridItemSize))
                {
                    slot.View.ApplyPresentation(viewMode, gridItemSize);
                }
            }

            _weights.Restore(weights);
            ClampWeightsToAvailableWidth();
            ApplyWeightStyles();
            ApplyEstimatedPaneWidths();
        }

        internal bool TryGetPaneAtTarget(
            VisualElement target,
            out ZepPaneState pane)
        {
            for (var i = 0; i < _slots.Count; i++)
            {
                if (target != null && _slots[i].Wrapper.Contains(target))
                {
                    pane = _slots[i].Pane;
                    return true;
                }
            }

            pane = null;
            return false;
        }

        internal ZepSplitOperationResult OpenAuxiliary(
            ZepPaneSource source,
            ZepPaneState replacementAuxiliary = null)
        {
            if (_panelState.WouldOpenAuxiliaryAddPane(source)
                && !CanFitPaneCount(_slots.Count + 1))
            {
                _showFeedback?.Invoke(WidthFeedback);
                return ZepSplitOperationResult.Rejected();
            }

            CaptureActivePresentation();
            try
            {
                return _panelState.OpenAuxiliary(source, replacementAuxiliary);
            }
            finally
            {
                _hasPendingPresentation = false;
            }
        }

        internal ZepSplitOperationResult CloseAuxiliary(
            ZepPaneState auxiliaryPane)
        {
            return _panelState.CloseAuxiliary(auxiliaryPane);
        }

        internal void ApplyResponsiveWidth(float width)
        {
            if (width <= 0f || float.IsNaN(width) || float.IsInfinity(width))
            {
                return;
            }

            if (_isWidthMotionActive)
            {
                SettleWidthMotion(_widthMotionGeneration);
            }

            _availableWidth = width;
            ClampWeightsToAvailableWidth();
            ApplyWeightStyles();
            ApplyEstimatedPaneWidths();
            _dropPresentation.RefreshGhostWidth();
        }

        internal void ApplyResponsiveItemHeights()
        {
            for (var i = 0; i < _slots.Count; i++)
            {
                _slots[i].View.ApplyResponsiveItemHeights();
            }
        }

        // A null batch means "which icons changed is unknown" — see
        // ZepShellView.OnMaterialThumbnailsReady.
        internal void NotifyIconPreviewsReady(IReadOnlyList<string> paths)
        {
            for (var i = 0; i < _slots.Count; i++)
            {
                _slots[i].View.NotifyIconPreviewsReady(paths);
            }
        }

        internal void RefreshChrome()
        {
            EnableInClassList(
                ZepUiClasses.SplitWorkspaceMultiple,
                _slots.Count > 1
                || _retiringSlots.Count > 0
                || _dropPresentation.IsGhostAttached);

            for (var i = 0; i < _slots.Count; i++)
            {
                RefreshSlotChrome(_slots[i]);
            }
        }

        // Every input the slot's chrome is derived from, compared before
        // anything is resolved: ResolvePaneTitle and the close tooltip both
        // allocate, and this runs for every slot on every global publication.
        // The tag and the root folder path are references the state already
        // holds, so reading them costs nothing.
        private void RefreshSlotChrome(PaneSlot slot)
        {
            var sourceKind = slot.Pane.Source.Kind;
            var sourceKey = sourceKind == ZepPaneSourceKind.Folder
                ? slot.Pane.Source.RootFolderPath.Value
                : slot.Pane.Source.TagId;
            string tagName = null;
            var tagColor = ZepTagColor.None;
            if (sourceKind == ZepPaneSourceKind.Tag)
            {
                ResolveTagIdentity(slot.Pane.Source.TagId, out tagName, out tagColor);
            }

            var canClose = _panelState.IsLivePane(slot.Pane);
            var isActive = ReferenceEquals(_panelState.ActivePane, slot.Pane);

            if (slot.HasPaintedChrome
                && slot.ChromeSourceKind == sourceKind
                && string.Equals(slot.ChromeSourceKey, sourceKey, StringComparison.Ordinal)
                && string.Equals(slot.ChromeTagName, tagName, StringComparison.Ordinal)
                && slot.ChromeTagColor == tagColor
                && slot.ChromeCanClose == canClose
                && slot.ChromeIsActive == isActive)
            {
                return;
            }

            // The tag crumb is the pane's identity, and a rename or recolour
            // is a global change that never reaches the pane's own PaneChanged
            // subscription, so this push is what repaints it.
            var tagIdentityChanged = sourceKind == ZepPaneSourceKind.Tag
                && (!string.Equals(slot.ChromeTagName, tagName, StringComparison.Ordinal)
                    || slot.ChromeTagColor != tagColor);

            slot.HasPaintedChrome = true;
            slot.ChromeSourceKind = sourceKind;
            slot.ChromeSourceKey = sourceKey;
            slot.ChromeTagName = tagName;
            slot.ChromeTagColor = tagColor;
            slot.ChromeCanClose = canClose;
            slot.ChromeIsActive = isActive;
            SlotChromeWritesForTests++;

            if (slot.CloseButton != null)
            {
                slot.CloseButton.SetEnabled(canClose);
                slot.CloseButton.tooltip = $"Close {ResolvePaneTitle(slot.Pane)} split";
            }

            if (tagIdentityChanged)
            {
                slot.View.RefreshSplitNavigation();
            }

            slot.Wrapper.EnableInClassList(
                ZepUiClasses.SplitPaneActive,
                isActive);
        }

        internal void CancelDividerDrags()
        {
            for (var i = 0; i < _dividers.Count; i++)
            {
                _dividers[i].CancelDrag();
            }

            _activeDividerIndex = -1;
            RemoveFromClassList(ZepUiClasses.SplitWorkspaceResizing);
        }

        internal void CancelSplitDrag()
        {
            ClearSplitDropSession();
            _dropPresentation.ClearPulse();
        }

        // The one hierarchy-settlement seam for close and replacement motion.
        // TransitionEnd and the bounded fallback both arrive here through a
        // generation check, so an interrupted operation cannot remove a newer
        // visual occupying the same logical slot.
        internal void CompleteRemoval(ZepPaneState retiredPane)
        {
            if (retiredPane == null)
            {
                return;
            }

            if (_retiringSlots.TryGetValue(retiredPane, out var retiringSlot))
            {
                SettleWidthMotion(retiringSlot.MotionGeneration);
                return;
            }

            for (var i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (ReferenceEquals(slot.RetiringPane, retiredPane)
                    && slot.Lifecycle == ZepSplitPaneLifecycle.Replacing)
                {
                    BeginReplacementEntrance(slot, slot.MotionGeneration);
                    return;
                }
            }
        }

        internal void Teardown()
        {
            if (_isTornDown)
            {
                return;
            }

            _isTornDown = true;
            CancelSplitDrag();
            UnbindTerminalEvents();
            Unbind();
            CancelDividerDrags();
            SettleAllMotion();

            for (var i = 0; i < _slots.Count; i++)
            {
                UnwirePaneView(_slots[i].View);
                _slots[i].View.Teardown();
            }

            foreach (var retiredView in _retiringViews.Values)
            {
                UnwirePaneView(retiredView);
                retiredView.Teardown();
            }

            _retiringViews.Clear();
            _retiringSlots.Clear();
        }

        private void OnSplitDragEnter(DragEnterEvent evt)
        {
            if (!ZepSplitDragAndDrop.TryRead(out var payload))
            {
                return;
            }

            evt.StopImmediatePropagation();
            ShowSplitDropPreview(payload, evt.target as VisualElement);
        }

        private void OnSplitDragUpdated(DragUpdatedEvent evt)
        {
            if (!ZepSplitDragAndDrop.TryRead(out var payload))
            {
                return;
            }

            evt.StopImmediatePropagation();
            ShowSplitDropPreview(payload, evt.target as VisualElement);
        }

        private void OnSplitDragPerform(DragPerformEvent evt)
        {
            if (!ZepSplitDragAndDrop.TryRead(out var publishedPayload))
            {
                return;
            }

            evt.StopImmediatePropagation();

            // The drag bus is global and AcceptDrag may clear it. Copy the
            // immutable source/display snapshot first, then revalidate against
            // the controller's authoritative folders/tags before accepting.
            var payload = new ZepSplitDragPayload(
                publishedPayload.Source,
                publishedPayload.DisplayName);
            if (!TryValidateSplitSource(payload.Source, out var source))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                _showFeedback?.Invoke(FormatUnavailableFeedback(payload.DisplayName));
                ClearSplitDropSession();
                return;
            }

            var target = ResolveSplitDropTarget(
                payload,
                source,
                evt.target as VisualElement,
                sourceIsValid: true);
            if (!target.IsValid)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                _showFeedback?.Invoke(target.Message);
                ClearSplitDropSession();
                return;
            }

            DragAndDrop.AcceptDrag();
            var result = OpenAuxiliary(source, target.ReplacementAuxiliary);
            ClearSplitDropSession();

            if (result.Kind == ZepSplitOperationKind.Rejected
                || result.NewPane == null)
            {
                return;
            }

            FocusSlotAtPane(result.NewPane);
            if (result.Kind == ZepSplitOperationKind.Added
                || result.Kind == ZepSplitOperationKind.Activated)
            {
                PulseSplitPane(result.NewPane);
            }
        }

        private void OnSplitDragLeave(DragLeaveEvent evt)
        {
            if (!ZepSplitDragAndDrop.TryRead(out _))
            {
                return;
            }

            evt.StopImmediatePropagation();
            if (!worldBound.Contains(evt.mousePosition))
            {
                HideSplitDropPreview(resetValidation: true);
            }
        }

        private void OnSplitDragExited(DragExitedEvent evt)
        {
            if (!IsSplitDropSessionLive())
            {
                return;
            }

            evt.StopImmediatePropagation();
            if (ZepWindowDocking.IsActive)
            {
                HideSplitDropPreview(resetValidation: true);
                return;
            }
            CancelSplitDrag();
        }

        private void OnSplitMouseLeaveWindow(MouseLeaveWindowEvent evt)
        {
            if (!IsSplitDropSessionLive())
            {
                return;
            }

            evt.StopImmediatePropagation();
            if (ZepWindowDocking.IsActive)
            {
                HideSplitDropPreview(resetValidation: true);
                return;
            }
            CancelSplitDrag();
        }

        private void OnSplitCancelKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape || !IsSplitDropSessionLive())
            {
                return;
            }

            evt.StopImmediatePropagation();
            CancelSplitDrag();
        }

        private void ShowSplitDropPreview(
            ZepSplitDragPayload payload,
            VisualElement eventTarget)
        {
            if (!ReferenceEquals(_previewPayload, payload))
            {
                _previewPayload = payload;
                _previewSourceIsValid = TryValidateSplitSource(
                    payload.Source,
                    out _previewSource);
            }

            var target = ResolveSplitDropTarget(
                payload,
                _previewSource,
                eventTarget,
                _previewSourceIsValid);
            _dropPresentation.ShowPreview(
                target.Kind,
                target.Message,
                payload);

            DragAndDrop.visualMode = target.IsValid
                ? DragAndDropVisualMode.Link
                : DragAndDropVisualMode.Rejected;
        }

        private SplitDropTarget ResolveSplitDropTarget(
            ZepSplitDragPayload payload,
            ZepPaneSource source,
            VisualElement eventTarget,
            bool sourceIsValid)
        {
            if (!sourceIsValid)
            {
                return SplitDropTarget.Rejected(
                    FormatUnavailableFeedback(payload.DisplayName));
            }

            var existing = FindAuxiliary(source);
            if (existing != null)
            {
                return SplitDropTarget.Activate(
                    existing,
                    $"Focus \u201c{payload.DisplayName}\u201d Split");
            }

            if (_panelState.WouldOpenAuxiliaryAddPane(source))
            {
                return CanFitPaneCount(_slots.Count + 1)
                    ? SplitDropTarget.Add(
                        $"Release to open \u201c{payload.DisplayName}\u201d in Split View")
                    : SplitDropTarget.Rejected(WidthFeedback);
            }

            var replacement = ResolveReplacementAuxiliary(eventTarget);
            return replacement != null
                ? SplitDropTarget.Replace(
                    replacement,
                    $"Replace Split with \u201c{payload.DisplayName}\u201d")
                : SplitDropTarget.Rejected("Split View is unavailable.");
        }

        private bool TryValidateSplitSource(
            ZepPaneSource source,
            out ZepPaneSource reconciledSource)
        {
            if (_controller != null)
            {
                return _controller.TryReconcilePaneSource(
                    source,
                    out reconciledSource);
            }

            // Controllerless shells are preview/test compositions with no
            // authoritative project index. They may exercise already-valid
            // immutable source shapes; the connected production window always
            // takes the controller path above.
            reconciledSource = source;
            return source.IsAuxiliary;
        }

        private ZepPaneState FindAuxiliary(ZepPaneSource source)
        {
            for (var i = 0; i < _panelState.AuxiliaryPanes.Count; i++)
            {
                var pane = _panelState.AuxiliaryPanes[i];
                if (pane.Source == source)
                {
                    return pane;
                }
            }

            return null;
        }

        private ZepPaneState ResolveReplacementAuxiliary(
            VisualElement eventTarget)
        {
            if (TryGetPaneAtTarget(eventTarget, out var hoveredPane)
                && hoveredPane.Source.IsAuxiliary)
            {
                return hoveredPane;
            }

            if (ReferenceEquals(
                    FindAuxiliary(_panelState.ActivePane.Source),
                    _panelState.ActivePane))
            {
                return _panelState.ActivePane;
            }

            var auxiliaries = _panelState.AuxiliaryPanes;
            return auxiliaries.Count > 0
                ? auxiliaries[auxiliaries.Count - 1]
                : null;
        }

        private static string FormatUnavailableFeedback(string displayName)
        {
            return $"\u201c{displayName}\u201d is no longer available.";
        }

        private void HideSplitDropPreview(bool resetValidation)
        {
            _dropPresentation.HidePreview();
            if (resetValidation)
            {
                _previewPayload = null;
                _previewSource = default;
                _previewSourceIsValid = false;
            }
        }

        private void ClearSplitDropSession()
        {
            HideSplitDropPreview(resetValidation: true);
            ZepSplitDragAndDrop.Clear();
            ZepWindowDocking.Clear();
        }

        private bool IsSplitDropSessionLive()
        {
            return IsSplitDropPreviewVisible
                || ZepSplitDragAndDrop.TryRead(out _);
        }

        private void PulseSplitPane(ZepPaneState pane)
        {
            var index = IndexOfPane(pane);
            if (index < 0)
            {
                return;
            }

            _dropPresentation.Pulse(_slots[index].Wrapper);
        }

        private void BindTerminalEvents(VisualElement terminalRoot)
        {
            if (terminalRoot == null
                || ReferenceEquals(terminalRoot, this)
                || ReferenceEquals(_terminalEventRoot, terminalRoot))
            {
                return;
            }

            UnbindTerminalEvents();
            _terminalEventRoot = terminalRoot;
            _terminalEventRoot.RegisterCallback<DragExitedEvent>(
                OnTerminalDragExited,
                TrickleDown.TrickleDown);
            _terminalEventRoot.RegisterCallback<MouseLeaveWindowEvent>(
                OnTerminalMouseLeaveWindow,
                TrickleDown.TrickleDown);
            _terminalEventRoot.RegisterCallback<PointerDownEvent>(
                OnTerminalFreshPointerDown,
                TrickleDown.TrickleDown);
            _terminalEventRoot.RegisterCallback<KeyDownEvent>(
                OnTerminalCancelKeyDown,
                TrickleDown.TrickleDown);
        }

        private void UnbindTerminalEvents()
        {
            if (_terminalEventRoot == null)
            {
                return;
            }

            _terminalEventRoot.UnregisterCallback<DragExitedEvent>(
                OnTerminalDragExited,
                TrickleDown.TrickleDown);
            _terminalEventRoot.UnregisterCallback<MouseLeaveWindowEvent>(
                OnTerminalMouseLeaveWindow,
                TrickleDown.TrickleDown);
            _terminalEventRoot.UnregisterCallback<PointerDownEvent>(
                OnTerminalFreshPointerDown,
                TrickleDown.TrickleDown);
            _terminalEventRoot.UnregisterCallback<KeyDownEvent>(
                OnTerminalCancelKeyDown,
                TrickleDown.TrickleDown);
            _terminalEventRoot = null;
        }

        private void OnTerminalDragExited(DragExitedEvent evt)
        {
            if (!IsSplitDropSessionLive())
            {
                return;
            }

            evt.StopImmediatePropagation();
            if (ZepWindowDocking.IsActive)
            {
                HideSplitDropPreview(resetValidation: true);
                return;
            }
            CancelSplitDrag();
        }

        private void OnTerminalMouseLeaveWindow(MouseLeaveWindowEvent evt)
        {
            if (!IsSplitDropSessionLive())
            {
                return;
            }

            evt.StopImmediatePropagation();
            if (ZepWindowDocking.IsActive)
            {
                HideSplitDropPreview(resetValidation: true);
                return;
            }
            CancelSplitDrag();
        }

        private void OnTerminalFreshPointerDown(PointerDownEvent evt)
        {
            if (IsSplitDropSessionLive())
            {
                CancelSplitDrag();
            }
        }

        private void OnTerminalCancelKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape || !IsSplitDropSessionLive())
            {
                return;
            }

            evt.StopImmediatePropagation();
            CancelSplitDrag();
        }

        private void OnPaneChanged(ZepPaneChange change)
        {
            // Same contract ZepShellView.OnPaneChanged applies: while
            // the controller is reacting to its own view-model publication the
            // outer PaneChanged dispatch is still in flight and reaches us
            // again afterwards, so the chrome push here is duplicate work. The
            // structural kinds below are not chrome and still run — they add,
            // replace, close or restore the slots themselves.
            var isReactingToViewModelChange =
                _controller != null && _controller.IsReactingToViewModelChange;

            switch (change.Kind)
            {
                case ZepPaneChangeKind.Added:
                    if (!ReferenceEquals(
                            FindAuxiliary(change.NewPane.Source),
                            change.NewPane))
                    {
                        return;
                    }
                    AddPane(change.NewPane);
                    break;
                case ZepPaneChangeKind.Replaced:
                    ReplacePane(change.OldPane, change.NewPane);
                    break;
                case ZepPaneChangeKind.Activated:
                    if (IndexOfPane(change.NewPane) < 0)
                    {
                        if (!isReactingToViewModelChange)
                        {
                            RefreshChrome();
                        }
                        return;
                    }
                    _paneActiveBeforeChange = change.NewPane;
                    if (!isReactingToViewModelChange)
                    {
                        RefreshChrome();
                    }
                    break;
                case ZepPaneChangeKind.Closed:
                    if (IndexOfPane(change.OldPane) < 0)
                    {
                        return;
                    }
                    ClosePaneVisual(change.OldPane, change.NewPane);
                    break;
                case ZepPaneChangeKind.Updated:
                    if (IndexOfPane(change.NewPane) < 0)
                    {
                        return;
                    }
                    if (ReferenceEquals(change.NewPane, _panelState.ActivePane)
                        && !isReactingToViewModelChange)
                    {
                        RefreshChrome();
                    }
                    break;
                case ZepPaneChangeKind.Restored:
                    ReconcileCollection();
                    break;
            }

            // Only the auxiliaries are serialized (ZepWindow
            // .CaptureSplitSnapshot walks state.AuxiliaryPanes, the active
            // auxiliary index and the pane weights), so browsing the primary
            // cannot change the snapshot and must not re-save it. Structural
            // kinds still raise: they add, replace, close, activate or restore
            // panes, all of which the snapshot records.
            var changesSnapshottedArrangement =
                (change.Update & ZepPaneUpdate.Navigation) != 0
                && !ReferenceEquals(change.NewPane, _panelState.PrimaryPane)
                && _panelState.IsLivePane(change.NewPane);

            if (change.Kind != ZepPaneChangeKind.Updated
                || changesSnapshottedArrangement)
            {
                ArrangementChanged?.Invoke();
            }
        }

        private void AddPane(ZepPaneState pane)
        {
            var useDropGhostHandoff = _dropPresentation.IsGhostStandingIn;
            var dropGhostPaneWidth = _dropPresentation.VisibleGhostPaneWidth;
            var startingWidths = PrepareStructuralMotion();
            ResolveInheritedPresentation(out var viewMode, out var gridItemSize);
            InsertSlot(_slots.Count, pane, viewMode, gridItemSize);
            var slot = _slots[_slots.Count - 1];
            if (useDropGhostHandoff)
            {
                startingWidths[slot.Wrapper] = dropGhostPaneWidth;
            }

            _paneActiveBeforeChange = _panelState.ActivePane;
            ClampWeightsToAvailableWidth();
            RefreshChrome();
            BeginWidthMotion(
                ZepSplitPaneLifecycle.Opening,
                slot,
                null,
                startingWidths,
                animateOpeningContent: !useDropGhostHandoff);
        }

        private void ReplacePane(
            ZepPaneState oldPane,
            ZepPaneState newPane)
        {
            var index = IndexOfPane(oldPane);
            if (index < 0)
            {
                ReconcileCollection();
                return;
            }

            PrepareStructuralMotion();
            var slot = _slots[index];
            var wasActive = ReferenceEquals(_paneActiveBeforeChange, oldPane);
            BlurFocusedDescendant(slot);
            RetirePaneView(slot, oldPane);

            ResolveInheritedPresentation(out var viewMode, out var gridItemSize);
            slot.Pane = newPane;
            slot.Content = CreatePaneContent(
                slot,
                newPane,
                viewMode,
                gridItemSize,
                out var replacementView,
                out var closeButton);
            slot.View = replacementView;
            slot.CloseButton = closeButton;
            // Fresh chrome elements: whatever the slot painted before belongs
            // to the retired ones, so the next RefreshChrome must write.
            slot.HasPaintedChrome = false;
            slot.LastFocusedChild = null;
            slot.Lifecycle = ZepSplitPaneLifecycle.Replacing;
            slot.MotionGeneration++;
            slot.Content.AddToClassList(
                ZepUiClasses.SplitPaneContentEntering);
            slot.Wrapper.Add(slot.Content);
            AddToClassList(ZepUiClasses.SplitWorkspaceReplacing);

            _paneActiveBeforeChange = _panelState.ActivePane;
            RefreshChrome();
            ApplyPaneWidth(slot, ResolvePaneWidth(index));
            BeginReplacementExit(slot, slot.MotionGeneration);

            if (wasActive)
            {
                FocusSlot(slot);
            }
        }

        private void ClosePaneVisual(
            ZepPaneState closedPane,
            ZepPaneState activePane)
        {
            var index = IndexOfPane(closedPane);
            if (index < 0)
            {
                ReconcileCollection();
                return;
            }

            var startingWidths = PrepareStructuralMotion();
            var wasActive = ReferenceEquals(_paneActiveBeforeChange, closedPane);
            var slot = _slots[index];
            BlurFocusedDescendant(slot);
            slot.View.CancelInteractions();
            slot.View.Teardown();
            UnwirePaneView(slot.View);
            slot.Wrapper.pickingMode = PickingMode.Ignore;
            slot.Wrapper.SetEnabled(false);

            _slots.RemoveAt(index);
            // Divider bookkeeping below reads no weights, so removing and
            // re-normalizing in one step here is the same as doing them apart.
            _weights.RemoveAt(index);
            RemoveDividerForClosedSlot(index);
            _retiringSlots[closedPane] = slot;
            _paneActiveBeforeChange = IndexOfPane(activePane) >= 0
                ? activePane
                : _panelState.PrimaryPane;

            ClampWeightsToAvailableWidth();
            RefreshChrome();

            if (wasActive)
            {
                FocusSlotAtPane(activePane);
            }

            BeginWidthMotion(
                ZepSplitPaneLifecycle.Closing,
                slot,
                closedPane,
                startingWidths,
                animateOpeningContent: true);
        }

        private void RetirePaneView(PaneSlot slot, ZepPaneState oldPane)
        {
            var oldView = slot.View;
            oldView.CancelInteractions();
            oldView.Teardown();
            UnwirePaneView(oldView);
            oldView.pickingMode = PickingMode.Ignore;
            oldView.SetEnabled(false);
            _retiringViews[oldPane] = oldView;
            slot.RetiringPane = oldPane;
            slot.RetiringContent = slot.Content;
            slot.RetiringContent.pickingMode = PickingMode.Ignore;
            slot.RetiringContent.SetEnabled(false);
            slot.RetiringContent.AddToClassList(
                ZepUiClasses.SplitPaneContentRetiring);
            var retiredWidth = Mathf.Max(
                slot.Content.resolvedStyle.width,
                ResolvePaneWidth(IndexOfPane(oldPane)),
                AssetsAreaConstants.SplitPaneMinimumWidth);
            slot.RetiringContent.style.width = retiredWidth;
        }

        private Dictionary<VisualElement, float> PrepareStructuralMotion()
        {
            var widths = CaptureLiveWidths();
            _dropPresentation.ConsumeGhost();

            var widthStartTask = _widthStartTask;
            var widthFallbackTask = _widthFallbackTask;
            _widthStartTask = null;
            _widthFallbackTask = null;
            widthStartTask?.Pause();
            widthFallbackTask?.Pause();
            _isWidthMotionActive = false;
            _widthMotionGeneration++;
            _widthMotionAnchor = null;
            _widthRetiredPane = null;
            RemoveFromClassList(ZepUiClasses.SplitWorkspaceOpening);
            RemoveFromClassList(ZepUiClasses.SplitWorkspaceClosing);

            for (var i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (widths.TryGetValue(slot.Wrapper, out var width))
                {
                    slot.Wrapper.style.flexGrow = 0f;
                    slot.Wrapper.style.width = width;
                }

                slot.Wrapper.RemoveFromClassList(
                    ZepUiClasses.SplitPaneWidthMotion);
                if (slot.Lifecycle == ZepSplitPaneLifecycle.Opening)
                {
                    ClearContentMotion(slot.Content);
                    slot.Lifecycle = ZepSplitPaneLifecycle.Open;
                }

                if (slot.Lifecycle == ZepSplitPaneLifecycle.Replacing)
                {
                    SettleReplacementImmediately(slot);
                }
            }

            if (_retiringSlots.Count > 0)
            {
                var retiredPanes = new List<ZepPaneState>(
                    _retiringSlots.Keys);
                for (var i = 0; i < retiredPanes.Count; i++)
                {
                    RemoveRetiredSlot(retiredPanes[i]);
                }
            }

            return widths;
        }

        private Dictionary<VisualElement, float> CaptureLiveWidths()
        {
            var widths = new Dictionary<VisualElement, float>();
            for (var i = 0; i < _slots.Count; i++)
            {
                var width = ResolvePaneWidth(i);
                if (width > 0f)
                {
                    widths[_slots[i].Wrapper] = width;
                }
            }

            return widths;
        }

        private void BeginWidthMotion(
            ZepSplitPaneLifecycle lifecycle,
            PaneSlot anchor,
            ZepPaneState retiredPane,
            IReadOnlyDictionary<VisualElement, float> startingWidths,
            bool animateOpeningContent)
        {
            var generation = ++_widthMotionGeneration;
            _isWidthMotionActive = true;
            _widthMotionAnchor = anchor;
            _widthRetiredPane = retiredPane;
            anchor.Lifecycle = lifecycle;
            anchor.MotionGeneration = generation;

            EnableInClassList(
                ZepUiClasses.SplitWorkspaceOpening,
                lifecycle == ZepSplitPaneLifecycle.Opening);
            EnableInClassList(
                ZepUiClasses.SplitWorkspaceClosing,
                lifecycle == ZepSplitPaneLifecycle.Closing);

            for (var i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                var targetWidth = ResolveTargetPaneWidth(i);
                var startWidth = startingWidths.TryGetValue(
                    slot.Wrapper,
                    out var capturedWidth)
                    ? capturedWidth
                    : ReferenceEquals(slot, anchor)
                        && lifecycle == ZepSplitPaneLifecycle.Opening
                            ? 0f
                            : targetWidth;
                FreezePaneForWidthMotion(slot, startWidth, targetWidth);
            }

            if (lifecycle == ZepSplitPaneLifecycle.Closing)
            {
                var closingStartWidth = startingWidths.TryGetValue(
                    anchor.Wrapper,
                    out var capturedWidth)
                    ? capturedWidth
                    : Mathf.Max(
                        anchor.Wrapper.resolvedStyle.width,
                        AssetsAreaConstants.SplitPaneMinimumWidth);
                anchor.Wrapper.style.flexGrow = 0f;
                anchor.Wrapper.style.width = closingStartWidth;
                anchor.Wrapper.AddToClassList(
                    ZepUiClasses.SplitPaneWidthMotion);
                anchor.Content.style.width = Mathf.Max(
                    anchor.Content.resolvedStyle.width,
                    closingStartWidth);
            }
            else if (animateOpeningContent)
            {
                anchor.Content.AddToClassList(
                    ZepUiClasses.SplitPaneContentEntering);
            }

            _widthStartTask = schedule.Execute(() =>
            {
                if (!_isWidthMotionActive || generation != _widthMotionGeneration)
                {
                    return;
                }

                _widthStartTask = null;
                for (var i = 0; i < _slots.Count; i++)
                {
                    _slots[i].Wrapper.style.width = ResolveTargetPaneWidth(i);
                }

                if (lifecycle == ZepSplitPaneLifecycle.Opening)
                {
                    if (animateOpeningContent)
                    {
                        anchor.Content.AddToClassList(
                            ZepUiClasses.SplitPaneContentPresented);
                    }
                }
                else
                {
                    anchor.Wrapper.style.width = 0f;
                    anchor.Content.AddToClassList(
                        ZepUiClasses.SplitPaneContentClosing);
                }
            });
            _widthStartTask.ExecuteLater(NextPanelTickMs);

            var duration = lifecycle == ZepSplitPaneLifecycle.Opening
                ? OpenMotionMs
                : CloseMotionMs;
            _widthFallbackTask = schedule.Execute(() =>
                SettleWidthMotion(generation));
            _widthFallbackTask.ExecuteLater(
                NextPanelTickMs + duration + TransitionFallbackBufferMs);
        }

        private void FreezePaneForWidthMotion(
            PaneSlot slot,
            float startWidth,
            float targetWidth)
        {
            slot.Wrapper.style.flexGrow = 0f;
            slot.Wrapper.style.width = Mathf.Max(0f, startWidth);
            slot.Wrapper.AddToClassList(
                ZepUiClasses.SplitPaneWidthMotion);
            if (targetWidth > 0f)
            {
                slot.Content.style.width = targetWidth;
                ApplyPaneWidth(slot, targetWidth);
            }
        }

        private float ResolveTargetPaneWidth(int index)
        {
            var width = _availableWidth > 0f
                ? _availableWidth
                : resolvedStyle.width;
            var paneWidth = Mathf.Max(
                0f,
                width - (_dividers.Count * ZepPaneSplitterWidget.Width));
            return index >= 0 && index < _weights.Count
                ? paneWidth * _weights[index]
                : 0f;
        }

        private void SettleWidthMotion(int generation)
        {
            if (!_isWidthMotionActive || generation != _widthMotionGeneration)
            {
                return;
            }

            var lifecycle = _widthMotionAnchor?.Lifecycle
                ?? ZepSplitPaneLifecycle.Open;
            var retiredPane = _widthRetiredPane;
            var anchor = _widthMotionAnchor;
            var widthStartTask = _widthStartTask;
            var widthFallbackTask = _widthFallbackTask;
            _widthStartTask = null;
            _widthFallbackTask = null;
            widthStartTask?.Pause();
            widthFallbackTask?.Pause();
            _isWidthMotionActive = false;
            _widthMotionAnchor = null;
            _widthRetiredPane = null;
            RemoveFromClassList(ZepUiClasses.SplitWorkspaceOpening);
            RemoveFromClassList(ZepUiClasses.SplitWorkspaceClosing);

            if (lifecycle == ZepSplitPaneLifecycle.Closing)
            {
                RemoveRetiredSlot(retiredPane);
            }

            for (var i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                slot.Wrapper.RemoveFromClassList(
                    ZepUiClasses.SplitPaneWidthMotion);
                slot.Wrapper.style.width = StyleKeyword.Null;
                slot.Content.style.width = StyleKeyword.Null;
                ClearContentMotion(slot.Content);
                if (ReferenceEquals(slot, anchor)
                    && lifecycle == ZepSplitPaneLifecycle.Opening)
                {
                    slot.Lifecycle = ZepSplitPaneLifecycle.Open;
                }
            }

            ApplyWeightStyles();
            ApplyEstimatedPaneWidths();
            RefreshChrome();
        }

        private void BeginReplacementExit(PaneSlot slot, int generation)
        {
            PauseSlotMotionTasks(slot);
            slot.RetiringContent.BringToFront();
            slot.NextMotionTask = schedule.Execute(() =>
            {
                if (slot.Lifecycle != ZepSplitPaneLifecycle.Replacing
                    || generation != slot.MotionGeneration)
                {
                    return;
                }

                slot.NextMotionTask = null;
                slot.RetiringContent?.AddToClassList(
                    ZepUiClasses.SplitPaneContentRetiringHidden);
            });
            slot.NextMotionTask.ExecuteLater(NextPanelTickMs);
            slot.FallbackMotionTask = schedule.Execute(() =>
                BeginReplacementEntrance(slot, generation));
            slot.FallbackMotionTask.ExecuteLater(
                NextPanelTickMs
                + ReplaceOutMotionMs
                + TransitionFallbackBufferMs);
        }

        private void BeginReplacementEntrance(PaneSlot slot, int generation)
        {
            if (slot.Lifecycle != ZepSplitPaneLifecycle.Replacing
                || generation != slot.MotionGeneration
                || slot.RetiringContent == null)
            {
                return;
            }

            PauseSlotMotionTasks(slot);
            RemoveRetiringView(slot);
            slot.Content.BringToFront();
            slot.Content.AddToClassList(
                ZepUiClasses.SplitPaneContentPresented);
            slot.FallbackMotionTask = schedule.Execute(() =>
                CompleteReplacement(slot, generation));
            slot.FallbackMotionTask.ExecuteLater(
                ReplaceInMotionMs + TransitionFallbackBufferMs);
        }

        private void CompleteReplacement(PaneSlot slot, int generation)
        {
            if (slot.Lifecycle != ZepSplitPaneLifecycle.Replacing
                || generation != slot.MotionGeneration)
            {
                return;
            }

            PauseSlotMotionTasks(slot);
            RemoveRetiringView(slot);
            ClearContentMotion(slot.Content);
            slot.Lifecycle = ZepSplitPaneLifecycle.Open;
            ReleaseFrozenWidths();
            if (!HasReplacingSlot())
            {
                RemoveFromClassList(ZepUiClasses.SplitWorkspaceReplacing);
            }
        }

        private void SettleReplacementImmediately(PaneSlot slot)
        {
            slot.MotionGeneration++;
            PauseSlotMotionTasks(slot);
            RemoveRetiringView(slot);
            ClearContentMotion(slot.Content);
            slot.Lifecycle = ZepSplitPaneLifecycle.Open;
            ReleaseFrozenWidths();
            if (!HasReplacingSlot())
            {
                RemoveFromClassList(ZepUiClasses.SplitWorkspaceReplacing);
            }
        }

        private void RemoveRetiringView(PaneSlot slot)
        {
            var retiringPane = slot.RetiringPane;
            if (retiringPane != null)
            {
                _retiringViews.Remove(retiringPane);
            }

            slot.RetiringContent?.RemoveFromHierarchy();
            slot.RetiringPane = null;
            slot.RetiringContent = null;
        }

        private void RemoveRetiredSlot(ZepPaneState retiredPane)
        {
            if (retiredPane == null
                || !_retiringSlots.TryGetValue(retiredPane, out var retiredSlot))
            {
                return;
            }

            _retiringSlots.Remove(retiredPane);
            retiredSlot.Wrapper.RemoveFromHierarchy();
        }

        private void OnSlotTransitionEnd(PaneSlot slot, TransitionEndEvent evt)
        {
            if (_isWidthMotionActive
                && ReferenceEquals(slot, _widthMotionAnchor)
                && ReferenceEquals(evt.target, slot.Wrapper)
                && evt.AffectsProperty(WidthStyleProperty))
            {
                SettleWidthMotion(slot.MotionGeneration);
                return;
            }

            if (slot.Lifecycle != ZepSplitPaneLifecycle.Replacing
                || !evt.AffectsProperty(OpacityStyleProperty))
            {
                return;
            }

            if (ReferenceEquals(evt.target, slot.RetiringContent))
            {
                BeginReplacementEntrance(slot, slot.MotionGeneration);
            }
            else if (slot.RetiringContent == null
                && ReferenceEquals(evt.target, slot.Content))
            {
                CompleteReplacement(slot, slot.MotionGeneration);
            }
        }

        private static void PauseSlotMotionTasks(PaneSlot slot)
        {
            var nextTask = slot.NextMotionTask;
            var fallbackTask = slot.FallbackMotionTask;
            slot.NextMotionTask = null;
            slot.FallbackMotionTask = null;
            nextTask?.Pause();
            fallbackTask?.Pause();
        }

        private static void ClearContentMotion(VisualElement content)
        {
            if (content == null)
            {
                return;
            }

            content.RemoveFromClassList(
                ZepUiClasses.SplitPaneContentEntering);
            content.RemoveFromClassList(
                ZepUiClasses.SplitPaneContentPresented);
            content.RemoveFromClassList(
                ZepUiClasses.SplitPaneContentClosing);
            content.style.opacity = StyleKeyword.Null;
            content.style.translate = StyleKeyword.Null;
            content.style.scale = StyleKeyword.Null;
        }

        private void ReleaseFrozenWidths()
        {
            if (_isWidthMotionActive)
            {
                return;
            }

            for (var i = 0; i < _slots.Count; i++)
            {
                _slots[i].Wrapper.RemoveFromClassList(
                    ZepUiClasses.SplitPaneWidthMotion);
                _slots[i].Wrapper.style.width = StyleKeyword.Null;
                _slots[i].Content.style.width = StyleKeyword.Null;
            }

            ApplyWeightStyles();
            ApplyEstimatedPaneWidths();
        }

        // The drop ghost stands for one more pane, so it can only be measured
        // once every width the composition is animating has come to rest.
        private void SettleUnsettledMotion()
        {
            if (_isWidthMotionActive
                || _retiringSlots.Count > 0
                || HasReplacingSlot())
            {
                SettleAllMotion();
            }
        }

        // The ghost is gone: the workspace is back to its own pane count and
        // the estimated widths no longer have to leave room for it.
        private void OnSplitDropGhostRetired()
        {
            RefreshChrome();
            ApplyEstimatedPaneWidths();
        }

        private bool HasReplacingSlot()
        {
            for (var i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Lifecycle == ZepSplitPaneLifecycle.Replacing)
                {
                    return true;
                }
            }

            return false;
        }

        private void SettleAllMotion()
        {
            _dropPresentation.SettleGhost();

            if (_isWidthMotionActive)
            {
                SettleWidthMotion(_widthMotionGeneration);
            }

            for (var i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Lifecycle == ZepSplitPaneLifecycle.Replacing)
                {
                    SettleReplacementImmediately(_slots[i]);
                }
                else
                {
                    PauseSlotMotionTasks(_slots[i]);
                    ClearContentMotion(_slots[i].Content);
                    _slots[i].Lifecycle = ZepSplitPaneLifecycle.Open;
                }
            }

            if (_retiringSlots.Count > 0)
            {
                var retiredPanes = new List<ZepPaneState>(
                    _retiringSlots.Keys);
                for (var i = 0; i < retiredPanes.Count; i++)
                {
                    RemoveRetiredSlot(retiredPanes[i]);
                }
            }

            RemoveFromClassList(ZepUiClasses.SplitWorkspaceOpening);
            RemoveFromClassList(ZepUiClasses.SplitWorkspaceClosing);
            RemoveFromClassList(ZepUiClasses.SplitWorkspaceReplacing);
            RefreshChrome();
        }

        private void InsertSlot(
            int index,
            ZepPaneState pane,
            AssetViewMode? inheritedViewMode,
            int? inheritedGridItemSize)
        {
            var slot = CreateSlot(pane, inheritedViewMode, inheritedGridItemSize);
            var oldCount = _slots.Count;

            _weights.Insert(index);
            if (oldCount == 0)
            {
                _slots.Add(slot);
                Add(slot.Wrapper);
                return;
            }

            if (index >= oldCount)
            {
                var divider = CreateDivider();
                _dividers.Add(divider);
                _slots.Add(slot);
                Add(divider);
                Add(slot.Wrapper);
                return;
            }

            // The new divider goes to the RIGHT of the new wrapper, and into
            // _dividers at the same index. Both lists are read left to right —
            // children are [w0][d0][w1][d1]…, and OnDividerDragMoved resizes
            // _weights[k] / _weights[k+1] for _dividers[k] — so an insert that
            // puts the divider first, or at index - 1, pairs drags with the
            // wrong panes (and throws outright when index is 0).
            var visualIndex = IndexOf(_slots[index].Wrapper);
            var insertedDivider = CreateDivider();
            _dividers.Insert(index, insertedDivider);
            _slots.Insert(index, slot);
            Insert(visualIndex, slot.Wrapper);
            Insert(visualIndex + 1, insertedDivider);
        }

        private PaneSlot CreateSlot(
            ZepPaneState pane,
            AssetViewMode? inheritedViewMode,
            int? inheritedGridItemSize)
        {
            var slot = new PaneSlot
            {
                Pane = pane,
                Wrapper = new VisualElement
                {
                    name = "zep-split-pane",
                    focusable = true
                },
                Lifecycle = ZepSplitPaneLifecycle.Open
            };
            slot.Wrapper.usageHints = UsageHints.DynamicTransform;
            slot.Wrapper.AddToClassList(ZepUiClasses.SplitPane);
            slot.Content = CreatePaneContent(
                slot,
                pane,
                inheritedViewMode,
                inheritedGridItemSize,
                out var view,
                out var closeButton);
            slot.View = view;
            slot.CloseButton = closeButton;
            slot.Wrapper.Add(slot.Content);

            slot.Wrapper.RegisterCallback<PointerDownEvent>(
                evt => ActivateSlot(slot),
                TrickleDown.TrickleDown);
            slot.Wrapper.RegisterCallback<FocusInEvent>(
                evt => OnSlotFocusIn(slot, evt),
                TrickleDown.TrickleDown);
            slot.Wrapper.RegisterCallback<GeometryChangedEvent>(
                evt => OnSlotGeometryChanged(slot, evt));
            slot.Wrapper.RegisterCallback<TransitionEndEvent>(
                evt => OnSlotTransitionEnd(slot, evt));
            return slot;
        }

        private VisualElement CreatePaneContent(
            PaneSlot slot,
            ZepPaneState pane,
            AssetViewMode? inheritedViewMode,
            int? inheritedGridItemSize,
            out ZepPaneView view,
            out Button closeButton)
        {
            var content = new VisualElement
            {
                name = "zep-split-pane-content"
            };
            content.usageHints = UsageHints.DynamicTransform;
            content.AddToClassList(ZepUiClasses.SplitPaneContent);

            view = CreatePaneView(pane, inheritedViewMode, inheritedGridItemSize);
            closeButton = null;
            if (pane.Source.IsAuxiliary)
            {
                // The close control ends the pane's own nav row, after the
                // breadcrumb. The workspace still owns it: closing is a
                // workspace operation and its enabled/tooltip state is slot
                // chrome, the pane view only lends the row.
                closeButton = new Button(() => CloseAuxiliary(slot.Pane))
                {
                    name = "zep-split-pane-close-button",
                    text = "\u00d7",
                    tooltip = "Close split"
                };
                closeButton.AddToClassList(ZepUiClasses.SplitPaneCloseButton);
                closeButton.AddToClassList(ZepUiClasses.FlatButton);
                view.SplitNavigationBar.Add(closeButton);
            }

            content.Add(view);
            return content;
        }

        private ZepPaneView CreatePaneView(
            ZepPaneState pane,
            AssetViewMode? inheritedViewMode,
            int? inheritedGridItemSize)
        {
            var view = new ZepPaneView(
                _panelState,
                pane,
                _iconRepository,
                _controller,
                _selectionAction,
                inheritedViewMode,
                inheritedGridItemSize,
                _dropOps);
            WirePaneView(view);
            return view;
        }

        private void WirePaneView(ZepPaneView view)
        {
            view.OpenRequested += OnOpenRequested;
            view.PresentationChanged += OnPanePresentationChanged;
            view.ContextMenuRequested += OnContextMenuRequested;
        }

        private void UnwirePaneView(ZepPaneView view)
        {
            view.OpenRequested -= OnOpenRequested;
            view.PresentationChanged -= OnPanePresentationChanged;
            view.ContextMenuRequested -= OnContextMenuRequested;
        }

        private ZepPaneSplitterWidget CreateDivider()
        {
            var divider = new ZepPaneSplitterWidget();
            divider.DragStarted += () => OnDividerDragStarted(divider);
            divider.DragMoved += delta => OnDividerDragMoved(divider, delta);
            divider.DragEnded += () => OnDividerDragEnded(divider);
            divider.KeyboardResizeRequested += delta =>
                OnDividerKeyboardResizeRequested(divider, delta);
            divider.EqualizeRequested += () => EqualizeDivider(divider);
            return divider;
        }

        private void OnDividerDragStarted(ZepPaneSplitterWidget divider)
        {
            if (_isWidthMotionActive)
            {
                SettleWidthMotion(_widthMotionGeneration);
            }

            var index = _dividers.IndexOf(divider);
            if (index < 0 || index + 1 >= _slots.Count)
            {
                return;
            }

            _activeDividerIndex = index;
            AddToClassList(ZepUiClasses.SplitWorkspaceResizing);
            var leftWidth = ResolvePaneWidth(index);
            var rightWidth = ResolvePaneWidth(index + 1);
            _dividerLeftWidthAtStart = leftWidth;
            _dividerPairWidthAtStart = leftWidth + rightWidth;
        }

        private void OnDividerDragMoved(
            ZepPaneSplitterWidget divider,
            float delta)
        {
            var index = _dividers.IndexOf(divider);
            if (index < 0 || index != _activeDividerIndex)
            {
                return;
            }

            var pairWidth = _dividerPairWidthAtStart;
            if (pairWidth <= 0f)
            {
                return;
            }

            var minimum = Mathf.Min(
                AssetsAreaConstants.SplitPaneMinimumWidth,
                pairWidth * 0.5f);
            var leftWidth = Mathf.Clamp(
                _dividerLeftWidthAtStart + delta,
                minimum,
                pairWidth - minimum);
            _weights.ResizePair(index, leftWidth, pairWidth);
            ApplyWeightStyles();
            ApplyEstimatedPaneWidths();
        }

        private void OnDividerDragEnded(ZepPaneSplitterWidget divider)
        {
            if (_activeDividerIndex == _dividers.IndexOf(divider))
            {
                _activeDividerIndex = -1;
                RemoveFromClassList(ZepUiClasses.SplitWorkspaceResizing);
                ArrangementChanged?.Invoke();
            }
        }

        private void OnDividerKeyboardResizeRequested(
            ZepPaneSplitterWidget divider,
            float delta)
        {
            OnDividerDragStarted(divider);
            OnDividerDragMoved(divider, delta);
            OnDividerDragEnded(divider);
        }

        private void EqualizeDivider(ZepPaneSplitterWidget divider)
        {
            var index = _dividers.IndexOf(divider);
            if (index < 0 || index + 1 >= _weights.Count)
            {
                return;
            }

            _weights.EqualizePair(index);
            ApplyWeightStyles();
            ApplyEstimatedPaneWidths();
            ArrangementChanged?.Invoke();
        }

        private void RemoveDividerForClosedSlot(int removedSlotIndex)
        {
            if (_dividers.Count == 0)
            {
                return;
            }

            var dividerIndex = removedSlotIndex > 0 ? removedSlotIndex - 1 : 0;
            var divider = _dividers[dividerIndex];
            divider.CancelDrag();
            _dividers.RemoveAt(dividerIndex);
            divider.RemoveFromHierarchy();
            _activeDividerIndex = -1;
        }

        private bool CanFitPaneCount(int paneCount)
        {
            var width = _availableWidth > 0f
                ? _availableWidth
                : resolvedStyle.width;
            if (width <= 0f)
            {
                return false;
            }

            var requiredWidth =
                (paneCount * AssetsAreaConstants.SplitPaneMinimumWidth)
                + ((paneCount - 1) * ZepPaneSplitterWidget.Width);
            return width >= requiredWidth;
        }

        private void ClampWeightsToAvailableWidth()
        {
            _weights.ClampToWidth(
                _availableWidth,
                _dividers.Count * ZepPaneSplitterWidget.Width,
                AssetsAreaConstants.SplitPaneMinimumWidth);
        }

        private void ApplyWeightStyles()
        {
            for (var i = 0; i < _slots.Count && i < _weights.Count; i++)
            {
                _slots[i].Wrapper.style.flexGrow = _weights[i];
            }
        }

        private void ApplyEstimatedPaneWidths()
        {
            for (var i = 0; i < _slots.Count; i++)
            {
                ApplyPaneWidth(_slots[i], ResolvePaneWidth(i));
            }
        }

        private float ResolvePaneWidth(int index)
        {
            if (index < 0 || index >= _slots.Count)
            {
                return -1f;
            }

            var resolvedWidth = _slots[index].Wrapper.resolvedStyle.width;
            if (resolvedWidth > 0f)
            {
                return resolvedWidth;
            }

            var paneWidth = Mathf.Max(
                0f,
                _availableWidth - (_dividers.Count * ZepPaneSplitterWidget.Width));
            return index < _weights.Count ? paneWidth * _weights[index] : -1f;
        }

        private static void ApplyPaneWidth(PaneSlot slot, float width)
        {
            if (width > 0f)
            {
                slot.View.ApplyResponsiveWidth(width);
            }
        }

        // Called by OpenAuxiliary before the state is asked to open anything,
        // so the state still holds the pane the new one inherits from.
        private void CaptureActivePresentation()
        {
            var activeView = GetPaneView(_panelState.ActivePane) ?? PrimaryPaneView;
            _pendingViewMode = activeView.ViewMode;
            _pendingGridItemSize = activeView.GridItemSize;
            _hasPendingPresentation = true;
        }

        private void ResolveInheritedPresentation(
            out AssetViewMode viewMode,
            out int gridItemSize)
        {
            if (_hasPendingPresentation)
            {
                viewMode = _pendingViewMode;
                gridItemSize = _pendingGridItemSize;
                return;
            }

            // Reached while the state already names the arriving pane, which
            // has no slot yet: the presentation must come from the pane that
            // was active before this change.
            var activeView = GetPaneView(_paneActiveBeforeChange) ?? PrimaryPaneView;
            viewMode = activeView.ViewMode;
            gridItemSize = activeView.GridItemSize;
        }

        private void ActivateSlot(PaneSlot slot)
        {
            if (_panelState.IsLivePane(slot.Pane)
                && !ReferenceEquals(_panelState.ActivePane, slot.Pane))
            {
                _panelState.ActivatePane(slot.Pane);
            }
        }

        private void OnSlotFocusIn(PaneSlot slot, FocusInEvent evt)
        {
            var focused = evt.target as VisualElement;
            if (focused != null
                && !ReferenceEquals(focused, slot.Wrapper)
                && slot.Wrapper.Contains(focused)
                && focused.focusable)
            {
                slot.LastFocusedChild = focused;
            }

            ActivateSlot(slot);
        }

        private static void BlurFocusedDescendant(PaneSlot slot)
        {
            var focused = slot.Wrapper.panel?.focusController?.focusedElement
                as VisualElement;
            if (focused != null && slot.Wrapper.Contains(focused))
            {
                focused.Blur();
            }
        }

        private void FocusSlotAtPane(ZepPaneState pane)
        {
            var index = IndexOfPane(pane);
            if (index >= 0)
            {
                FocusSlot(_slots[index]);
            }
        }

        private static void FocusSlot(PaneSlot slot)
        {
            var target = slot.LastFocusedChild;
            if (target != null
                && target.panel != null
                && target.enabledInHierarchy
                && target.resolvedStyle.display != DisplayStyle.None
                && target.focusable)
            {
                target.Focus();
                return;
            }

            slot.Wrapper.Focus();
        }

        private void OnSlotGeometryChanged(PaneSlot slot, GeometryChangedEvent evt)
        {
            if (!_isWidthMotionActive
                && !Mathf.Approximately(evt.newRect.width, evt.oldRect.width))
            {
                ApplyPaneWidth(slot, evt.newRect.width);
            }
        }

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (!Mathf.Approximately(evt.newRect.width, evt.oldRect.width))
            {
                ApplyResponsiveWidth(evt.newRect.width);
            }
        }

        private void OnAttachToPanel(AttachToPanelEvent evt)
        {
            if (_isTornDown)
            {
                return;
            }

            ReconcileCollection();
            Bind();
            BindTerminalEvents(evt.destinationPanel?.visualTree);
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            FlushPendingPresentationSnapshot();
            CancelSplitDrag();
            UnbindTerminalEvents();
            CancelDividerDrags();
            SettleAllMotion();
            Unbind();
        }

        private void Bind()
        {
            if (_isSubscribed || _isTornDown)
            {
                return;
            }

            _panelState.PaneChanged += OnPaneChanged;
            _isSubscribed = true;
        }

        private void Unbind()
        {
            if (!_isSubscribed)
            {
                return;
            }

            _panelState.PaneChanged -= OnPaneChanged;
            _isSubscribed = false;
        }

        private void ReconcileCollection()
        {
            for (var i = _slots.Count - 1; i >= 1; i--)
            {
                if (!_panelState.IsLivePane(_slots[i].Pane))
                {
                    ClosePaneVisual(_slots[i].Pane, _panelState.ActivePane);
                }
            }

            for (var i = 0; i < _panelState.AuxiliaryPanes.Count; i++)
            {
                var pane = _panelState.AuxiliaryPanes[i];
                if (IndexOfPane(pane) >= 0)
                {
                    continue;
                }

                ResolveInheritedPresentation(out var viewMode, out var gridItemSize);
                InsertSlot(i + 1, pane, viewMode, gridItemSize);
            }

            if (IndexOfPane(_panelState.ActivePane) >= 0)
            {
                _paneActiveBeforeChange = _panelState.ActivePane;
            }
            else if (IndexOfPane(_paneActiveBeforeChange) < 0)
            {
                _paneActiveBeforeChange = _panelState.PrimaryPane;
            }
            ClampWeightsToAvailableWidth();
            ApplyWeightStyles();
            ApplyEstimatedPaneWidths();
            RefreshChrome();
        }

        private int IndexOfPane(ZepPaneState pane)
        {
            for (var i = 0; i < _slots.Count; i++)
            {
                if (ReferenceEquals(_slots[i].Pane, pane))
                {
                    return i;
                }
            }

            return -1;
        }

        private string ResolvePaneTitle(ZepPaneState pane)
        {
            switch (pane.Source.Kind)
            {
                case ZepPaneSourceKind.Primary:
                    return "Project";
                case ZepPaneSourceKind.Folder:
                    return "Folder \u00b7 " + LastPathSegment(
                        pane.Source.RootFolderPath.Value);
                case ZepPaneSourceKind.Tag:
                    ResolveTagIdentity(pane.Source.TagId, out var tagName, out _);
                    return "Tag \u00b7 " + tagName;
                default:
                    return "Split View";
            }
        }

        // A live tag can disappear from the sidebar after its final assignment
        // is removed. Its split still takes its identity from the definition.
        // A deleted tag keeps its id and fallback colour until reconciliation.
        private void ResolveTagIdentity(string tagId, out string name, out ZepTagColor color)
        {
            for (var i = 0; i < _panelState.TagFilterOptions.Count; i++)
            {
                var tag = _panelState.TagFilterOptions[i].Tag;
                if (string.Equals(tag.Id, tagId, StringComparison.OrdinalIgnoreCase))
                {
                    name = string.IsNullOrWhiteSpace(tag.Name) ? tagId : tag.Name;
                    color = tag.Color;
                    return;
                }
            }

            var definitions = _controller?.GetSharedTags();
            for (var i = 0; definitions != null && i < definitions.Count; i++)
            {
                var tag = definitions[i];
                if (string.Equals(tag.Id, tagId, StringComparison.OrdinalIgnoreCase))
                {
                    name = string.IsNullOrWhiteSpace(tag.Name) ? tagId : tag.Name;
                    color = tag.Color;
                    return;
                }
            }

            name = tagId ?? string.Empty;
            color = ZepTagColor.None;
        }

        private static string LastPathSegment(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Folder";
            }

            var slash = path.LastIndexOf('/');
            return slash >= 0 && slash + 1 < path.Length
                ? path.Substring(slash + 1)
                : path;
        }

        private void OnOpenRequested(ZepPaneState pane) =>
            OpenRequested?.Invoke(pane);

        private void OnPanePresentationChanged(ZepPaneState pane)
        {
            if (!_panelState.IsLivePane(pane))
            {
                return;
            }

            // A grid-zoom slider drag reports every tick, and each raise costs a
            // full snapshot capture plus SetDirty. The snapshot only reads the
            // pane's final view mode and item size, so coalesce the burst into
            // one raise on the next frame.
            if (_presentationSnapshotTask != null)
            {
                return;
            }

            var generation = ++_presentationSnapshotGeneration;
            _presentationSnapshotTask = schedule.Execute(() =>
            {
                if (generation == _presentationSnapshotGeneration)
                {
                    FlushPendingPresentationSnapshot();
                }
            });
        }

        // Scheduled items do not tick while detached, so a pending raise is
        // flushed on the way out rather than dropped: the snapshot must still
        // record the last size the user picked.
        private void FlushPendingPresentationSnapshot()
        {
            var task = _presentationSnapshotTask;
            if (task == null)
            {
                return;
            }

            _presentationSnapshotTask = null;
            _presentationSnapshotGeneration++;
            task.Pause();
            ArrangementChanged?.Invoke();
        }

        private void OnContextMenuRequested(
            ZepPaneState pane,
            Rect anchor,
            ZepAssetEntry entry) =>
            ContextMenuRequested?.Invoke(pane, anchor, entry);

        private readonly struct SplitDropTarget
        {
            private SplitDropTarget(
                ZepSplitOperationKind kind,
                ZepPaneState replacementAuxiliary,
                string message)
            {
                Kind = kind;
                ReplacementAuxiliary = replacementAuxiliary;
                Message = message ?? string.Empty;
            }

            internal ZepSplitOperationKind Kind { get; }

            internal ZepPaneState ReplacementAuxiliary { get; }

            internal string Message { get; }

            internal bool IsValid => Kind != ZepSplitOperationKind.Rejected;

            internal static SplitDropTarget Rejected(string message)
            {
                return new SplitDropTarget(
                    ZepSplitOperationKind.Rejected,
                    null,
                    message);
            }

            internal static SplitDropTarget Add(string message)
            {
                return new SplitDropTarget(
                    ZepSplitOperationKind.Added,
                    null,
                    message);
            }

            internal static SplitDropTarget Activate(
                ZepPaneState pane,
                string message)
            {
                return new SplitDropTarget(
                    ZepSplitOperationKind.Activated,
                    pane,
                    message);
            }

            internal static SplitDropTarget Replace(
                ZepPaneState pane,
                string message)
            {
                return new SplitDropTarget(
                    ZepSplitOperationKind.Replaced,
                    pane,
                    message);
            }
        }

        private sealed class PaneSlot
        {
            public ZepPaneState Pane;
            public VisualElement Wrapper;
            public VisualElement Content;
            public ZepPaneView View;
            public Button CloseButton;
            public VisualElement LastFocusedChild;
            public ZepSplitPaneLifecycle Lifecycle;
            public int MotionGeneration;
            public ZepPaneState RetiringPane;
            public VisualElement RetiringContent;
            public IVisualElementScheduledItem NextMotionTask;
            public IVisualElementScheduledItem FallbackMotionTask;

            // What RefreshSlotChrome last painted onto CloseButton, Wrapper
            // and the view's tag crumb. Reset wherever those elements are
            // replaced, because the cache describes the elements, not the pane.
            public bool HasPaintedChrome;
            public ZepPaneSourceKind ChromeSourceKind;
            public string ChromeSourceKey;
            public string ChromeTagName;
            public ZepTagColor ChromeTagColor;
            public bool ChromeCanClose;
            public bool ChromeIsActive;
        }
    }
}
