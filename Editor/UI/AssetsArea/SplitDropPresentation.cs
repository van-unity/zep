using System;
using Zep.Core.State;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    // Owns everything the workspace shows WHILE a split drag is live: the
    // overlay card that names the verdict, the ghost pane that stands in for
    // the split about to be added, and the pulse the added pane gets on drop.
    // Each of those is a two-step presentation — attach or toggle a class now,
    // add the "presented" class on the next panel tick so USS can transition —
    // so this type owns the scheduled items that carry the second step and the
    // generation counters that stop an interrupted step from resurrecting a
    // newer one. The split view keeps the drop-target policy that decides the
    // verdict and the slot/weight/divider composition the ghost is measured
    // against; it reaches those back through the callbacks this constructor
    // takes, which is the whole surface this owner needs.
    internal sealed class SplitDropPresentation
    {
        private readonly VisualElement _host;
        private readonly Func<int> _slotCount;
        private readonly Func<int> _dividerCount;
        private readonly Func<float> _availableWidth;
        private readonly Action _settleUnsettledMotion;
        private readonly Action _ghostRetired;
        private readonly VisualElement _preview;
        private readonly Label _previewLabel;
        private readonly VisualElement _ghost;
        private readonly VisualElement _ghostPane;
        private readonly Label _ghostTitleLabel;

        private string _previewText = string.Empty;
        private bool _isGhostActive;
        private VisualElement _pulsedWrapper;
        private IVisualElementScheduledItem _pulseReset;
        private IVisualElementScheduledItem _previewPresentTask;
        private IVisualElementScheduledItem _ghostPresentTask;
        private IVisualElementScheduledItem _ghostRetireTask;
        private int _previewGeneration;
        private int _ghostGeneration;
        private int _pulseGeneration;

        // settleUnsettledMotion runs the host's structural settlement only if
        // something is still in motion: the ghost cannot be measured against
        // widths that are still animating. ghostRetired is how the host learns
        // that the extra pane the ghost stood for is gone again.
        internal SplitDropPresentation(
            VisualElement host,
            Func<int> slotCount,
            Func<int> dividerCount,
            Func<float> availableWidth,
            Action settleUnsettledMotion,
            Action ghostRetired)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _slotCount = slotCount ?? throw new ArgumentNullException(nameof(slotCount));
            _dividerCount = dividerCount
                ?? throw new ArgumentNullException(nameof(dividerCount));
            _availableWidth = availableWidth
                ?? throw new ArgumentNullException(nameof(availableWidth));
            _settleUnsettledMotion = settleUnsettledMotion
                ?? throw new ArgumentNullException(nameof(settleUnsettledMotion));
            _ghostRetired = ghostRetired
                ?? throw new ArgumentNullException(nameof(ghostRetired));

            _preview = BuildPreview(out _previewLabel);
            _ghost = BuildGhost(out _ghostPane, out _ghostTitleLabel);
            _host.Add(_preview);
        }

        internal bool IsPreviewVisible => IsOverlayVisible || _isGhostActive;

        internal bool IsPreviewRejected =>
            _preview.ClassListContains(
                ZepUiClasses.SplitDropPreviewRejected);

        internal string PreviewText => _previewText;

        // The ghost element is still in the hierarchy — including while it is
        // animating out, when the workspace must still look like a split.
        internal bool IsGhostAttached => _ghost.parent != null;

        // The ghost is live and mounted: it is holding a column for a pane that
        // has not opened yet, and that pane inherits its width when it does.
        internal bool IsGhostStandingIn => _isGhostActive && _ghost.parent != null;

        internal bool HasPendingWork => _previewPresentTask != null
            || _ghostPresentTask != null
            || _ghostRetireTask != null
            || _pulseReset != null;

        // The pane width the ghost currently occupies on screen, or 0 when no
        // ghost is standing in for a pane: an opening slot starts from it so
        // the real pane grows out of the ghost instead of from nothing.
        internal float VisibleGhostPaneWidth
        {
            get
            {
                if (!IsGhostStandingIn)
                {
                    return 0f;
                }

                var ghostWidth = _ghost.layout.width;
                if (ghostWidth <= 0f
                    || float.IsNaN(ghostWidth)
                    || float.IsInfinity(ghostWidth))
                {
                    return 0f;
                }

                return Mathf.Max(
                    0f,
                    ghostWidth - ZepPaneSplitterWidget.Width);
            }
        }

        // An Added verdict is shown as the ghost pane itself; every other
        // verdict — activate, replace, rejected — is shown as the overlay card.
        internal void ShowPreview(
            ZepSplitOperationKind kind,
            string message,
            ZepSplitDragPayload payload)
        {
            _previewText = message;
            if (kind == ZepSplitOperationKind.Added)
            {
                HideOverlay();
                ShowGhost(payload);
                return;
            }

            HideGhost(animated: true);
            ShowOverlay(
                message,
                kind != ZepSplitOperationKind.Rejected);
        }

        internal void HidePreview()
        {
            HideOverlay();
            HideGhost(animated: true);
            _previewText = string.Empty;
        }

        // The drop landed: the real pane takes the ghost's place this frame, so
        // the ghost leaves without its retirement transition.
        internal void ConsumeGhost()
        {
            if (_ghost.parent == null)
            {
                return;
            }

            _isGhostActive = false;
            _ghostGeneration++;
            PauseGhostTasks();
            _ghost.RemoveFromHierarchy();
            _ghost.RemoveFromClassList(
                ZepUiClasses.SplitDropGhostPresented);
            _ghost.style.width = StyleKeyword.Null;
            _ghostPane.style.width = StyleKeyword.Null;
        }

        // Unlike HideGhost this cannot defer to a retirement already in
        // flight — the host is settling every visual to its resting state now.
        internal void SettleGhost()
        {
            if (!_isGhostActive && _ghost.parent == null)
            {
                return;
            }

            _isGhostActive = false;
            CompleteGhostRetirement(++_ghostGeneration);
        }

        internal void RefreshGhostWidth()
        {
            if (!_isGhostActive)
            {
                return;
            }

            // Before the presenting tick the ghost is still at width 0 and must
            // stay there, or it would jump in instead of growing.
            if (_ghostPresentTask == null)
            {
                ApplyGhostTargetWidth();
            }
            else
            {
                ApplyGhostPaneWidth();
            }
        }

        internal void Pulse(VisualElement wrapper)
        {
            ClearPulse();
            var pulseGeneration = ++_pulseGeneration;
            _pulsedWrapper = wrapper;
            _pulsedWrapper.AddToClassList(
                ZepUiClasses.SplitPaneDropPulse);
            _pulseReset = _pulsedWrapper.schedule.Execute(() =>
            {
                if (pulseGeneration == _pulseGeneration)
                {
                    ClearPulse();
                }
            });
            _pulseReset.ExecuteLater(
                ZepSplitWorkspaceView.IndicatorMotionMs);
        }

        internal void ClearPulse()
        {
            _pulseGeneration++;
            var pulseReset = _pulseReset;
            _pulseReset = null;
            pulseReset?.Pause();

            if (_pulsedWrapper != null)
            {
                _pulsedWrapper.RemoveFromClassList(
                    ZepUiClasses.SplitPaneDropPulse);
                _pulsedWrapper = null;
            }
        }

        private bool IsOverlayVisible =>
            _preview.ClassListContains(
                ZepUiClasses.SplitDropPreviewVisible);

        private void ShowOverlay(string message, bool isValid)
        {
            var wasVisible = IsOverlayVisible;
            var wasRejected = IsPreviewRejected;
            _previewLabel.text = message;
            _preview.EnableInClassList(
                ZepUiClasses.SplitDropPreviewRejected,
                !isValid);
            _preview.AddToClassList(
                ZepUiClasses.SplitDropPreviewVisible);
            _preview.BringToFront();

            if (!wasVisible || wasRejected == isValid)
            {
                var previewGeneration = ++_previewGeneration;
                var previewPresentTask = _previewPresentTask;
                _previewPresentTask = null;
                previewPresentTask?.Pause();
                _preview.RemoveFromClassList(
                    ZepUiClasses.SplitDropPreviewPresented);
                if (isValid)
                {
                    _previewPresentTask = _preview.schedule.Execute(() =>
                    {
                        if (previewGeneration != _previewGeneration)
                        {
                            return;
                        }

                        _previewPresentTask = null;
                        _preview.AddToClassList(
                            ZepUiClasses.SplitDropPreviewPresented);
                    });
                    _previewPresentTask.ExecuteLater(
                        ZepSplitWorkspaceView.NextPanelTickMs);
                }
            }
        }

        private void HideOverlay()
        {
            _previewGeneration++;
            var previewPresentTask = _previewPresentTask;
            _previewPresentTask = null;
            previewPresentTask?.Pause();
            _preview.RemoveFromClassList(
                ZepUiClasses.SplitDropPreviewVisible);
            _preview.RemoveFromClassList(
                ZepUiClasses.SplitDropPreviewPresented);
            _preview.RemoveFromClassList(
                ZepUiClasses.SplitDropPreviewRejected);
            _previewLabel.text = string.Empty;
        }

        private void ShowGhost(ZepSplitDragPayload payload)
        {
            _ghostTitleLabel.text = ResolveGhostTitle(payload);

            if (_isGhostActive)
            {
                ApplyGhostTargetWidth();
                return;
            }

            if (_ghost.parent == null)
            {
                _settleUnsettledMotion();
            }

            _isGhostActive = true;
            var generation = ++_ghostGeneration;
            PauseGhostTasks();
            _host.AddToClassList(ZepUiClasses.SplitWorkspaceMultiple);

            // A ghost still retiring is caught before it leaves and simply
            // presents again, so a drag that re-enters does not restart it.
            if (_ghost.parent != null)
            {
                _ghost.AddToClassList(
                    ZepUiClasses.SplitDropGhostPresented);
                ApplyGhostTargetWidth();
                return;
            }

            _ghost.RemoveFromClassList(
                ZepUiClasses.SplitDropGhostPresented);
            _ghost.style.width = 0f;
            ApplyGhostPaneWidth();
            _host.Add(_ghost);
            _ghost.BringToFront();
            _ghostPresentTask = _host.schedule.Execute(() =>
            {
                if (!_isGhostActive || generation != _ghostGeneration)
                {
                    return;
                }

                _ghostPresentTask = null;
                _ghost.AddToClassList(
                    ZepUiClasses.SplitDropGhostPresented);
                ApplyGhostTargetWidth();
            });
            _ghostPresentTask.ExecuteLater(
                ZepSplitWorkspaceView.NextPanelTickMs);
        }

        private void HideGhost(bool animated)
        {
            if (!_isGhostActive && _ghost.parent == null)
            {
                return;
            }

            if (!_isGhostActive && _ghostRetireTask != null)
            {
                return;
            }

            _isGhostActive = false;
            var generation = ++_ghostGeneration;
            PauseGhostTasks();
            _ghost.RemoveFromClassList(
                ZepUiClasses.SplitDropGhostPresented);
            _ghost.style.width = 0f;

            if (!animated || _host.panel == null)
            {
                CompleteGhostRetirement(generation);
                return;
            }

            _ghostRetireTask = _host.schedule.Execute(() =>
                CompleteGhostRetirement(generation));
            _ghostRetireTask.ExecuteLater(
                ZepSplitWorkspaceView.DropGhostMotionMs
                + ZepSplitWorkspaceView.TransitionFallbackBufferMs);
        }

        private void CompleteGhostRetirement(int generation)
        {
            if (_isGhostActive || generation != _ghostGeneration)
            {
                return;
            }

            PauseGhostTasks();
            _ghost.RemoveFromHierarchy();
            _ghost.RemoveFromClassList(
                ZepUiClasses.SplitDropGhostPresented);
            _ghost.style.width = StyleKeyword.Null;
            _ghostPane.style.width = StyleKeyword.Null;
            _ghostRetired();
        }

        private void PauseGhostTasks()
        {
            var presentTask = _ghostPresentTask;
            var retireTask = _ghostRetireTask;
            _ghostPresentTask = null;
            _ghostRetireTask = null;
            presentTask?.Pause();
            retireTask?.Pause();
        }

        private void ApplyGhostTargetWidth()
        {
            var paneWidth = ApplyGhostPaneWidth();
            _ghost.style.width =
                ZepPaneSplitterWidget.Width + paneWidth;
        }

        // The ghost is the pane the workspace would have if the drop landed
        // now, so it takes an equal share of the width one more divider and one
        // more pane would leave.
        private float ApplyGhostPaneWidth()
        {
            var available = _availableWidth();
            var width = available > 0f
                ? available
                : _host.resolvedStyle.width;
            var slots = _slotCount();
            var paneArea = Mathf.Max(
                0f,
                width
                - ((_dividerCount() + 1)
                    * ZepPaneSplitterWidget.Width));
            var paneWidth = slots > 0
                ? paneArea / (slots + 1f)
                : 0f;
            _ghostPane.style.width = paneWidth;
            return paneWidth;
        }

        private static string ResolveGhostTitle(
            ZepSplitDragPayload payload)
        {
            switch (payload.Source.Kind)
            {
                case ZepPaneSourceKind.Folder:
                    return "Folder \u00b7 " + payload.DisplayName;
                case ZepPaneSourceKind.Tag:
                    return "Tag \u00b7 " + payload.DisplayName;
                default:
                    return payload.DisplayName;
            }
        }

        private static VisualElement BuildPreview(out Label label)
        {
            var preview = new VisualElement
            {
                name = "zep-split-drop-preview",
                pickingMode = PickingMode.Ignore
            };
            preview.AddToClassList(ZepUiClasses.AbsoluteFill);
            preview.AddToClassList(ZepUiClasses.SplitDropPreview);

            var card = new VisualElement
            {
                name = "zep-split-drop-preview-card",
                pickingMode = PickingMode.Ignore
            };
            card.AddToClassList(ZepUiClasses.SplitDropPreviewCard);

            label = new Label
            {
                name = "zep-split-drop-preview-label",
                pickingMode = PickingMode.Ignore
            };
            label.AddToClassList(ZepUiClasses.SplitDropPreviewLabel);
            card.Add(label);
            preview.Add(card);
            return preview;
        }

        private static VisualElement BuildGhost(
            out VisualElement pane,
            out Label titleLabel)
        {
            var ghost = new VisualElement
            {
                name = "zep-split-drop-ghost",
                pickingMode = PickingMode.Ignore
            };
            ghost.AddToClassList(ZepUiClasses.SplitDropGhost);

            var divider = new VisualElement
            {
                name = "zep-split-drop-ghost-divider",
                pickingMode = PickingMode.Ignore
            };
            divider.AddToClassList(ZepUiClasses.PaneSplitter);
            var grip = new VisualElement
            {
                name = "zep-split-drop-ghost-divider-grip",
                pickingMode = PickingMode.Ignore
            };
            grip.AddToClassList(ZepUiClasses.PaneSplitterGrip);
            divider.Add(grip);

            pane = new VisualElement
            {
                name = "zep-split-drop-ghost-pane",
                pickingMode = PickingMode.Ignore
            };
            pane.AddToClassList(ZepUiClasses.SplitPane);
            var content = new VisualElement
            {
                name = "zep-split-drop-ghost-content",
                pickingMode = PickingMode.Ignore
            };
            content.usageHints = UsageHints.DynamicTransform;
            content.AddToClassList(ZepUiClasses.SplitPaneContent);

            // Mirrors the chrome the dropped pane will carry: a nav row with
            // the pane's name where its crumb will be.
            var navigationBar = new VisualElement
            {
                name = "zep-split-drop-ghost-navigation-bar",
                pickingMode = PickingMode.Ignore
            };
            navigationBar.AddToClassList(ZepUiClasses.SplitPaneNavigationBar);
            titleLabel = new Label
            {
                name = "zep-split-drop-ghost-title",
                pickingMode = PickingMode.Ignore
            };
            titleLabel.AddToClassList(ZepUiClasses.SplitDropGhostTitle);
            navigationBar.Add(titleLabel);

            var instructionLabel = new Label
            {
                name = "zep-split-drop-ghost-label",
                text = "Release to open in Split View",
                pickingMode = PickingMode.Ignore
            };
            instructionLabel.AddToClassList(
                ZepUiClasses.SplitDropPreviewLabel);

            content.Add(navigationBar);
            content.Add(instructionLabel);
            pane.Add(content);
            ghost.Add(divider);
            ghost.Add(pane);
            return ghost;
        }
    }
}
