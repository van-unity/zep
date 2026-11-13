using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Zep.Core.Application;
using Zep.Core.Domain;

namespace Zep.Core.State
{
    // Owns shared project state and the live panes in the split workspace and windows.
    public sealed class ZepState
    {
        public const int MaxSplitViews = 1;

        private readonly List<ZepFolderEntry> _folders =
            new List<ZepFolderEntry>();
        // Membership index over _folders, rebuilt only when the folder list
        // actually changes. IsFolderAssetEntry is asked once per selected
        // asset (Enter on a selection, context-menu build, create target
        // resolution), and the list it used to scan is the whole project's
        // folder catalog — so a 5k selection in a 10k-folder project cost 50M
        // string comparisons per keystroke.
        private readonly HashSet<string> _folderPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ZepTagFilterOption> _tagFilterOptions =
            new List<ZepTagFilterOption>();
        private readonly List<ZepPaneState> _auxiliaryPanes =
            new List<ZepPaneState>();
        private readonly ReadOnlyCollection<ZepPaneState> _readOnlyAuxiliaryPanes;
        private readonly List<ZepPaneState> _windowPanes =
            new List<ZepPaneState>();
        private readonly ReadOnlyCollection<ZepPaneState> _readOnlyWindowPanes;
        // Not readonly: EndNotificationDeferral hands the filled map to the
        // drain loop and installs a fresh one, so a listener that defers again
        // mid-drain writes into the new map and can never mutate the one being
        // iterated.
        private Dictionary<ZepPaneState, ZepPaneUpdate>
            _deferredPaneUpdates =
                new Dictionary<ZepPaneState, ZepPaneUpdate>();
        private int _notificationDeferralDepth;
        private bool _hasDeferredGlobalChange;
        private string _statusMessage = string.Empty;
        private bool _canUndo;
        private bool _canRedo;
        private bool _isIndexScanInProgress;

        public ZepState()
        {
            PrimaryPane = new ZepPaneState(ZepPaneSource.Primary);
            ActivePane = PrimaryPane;
            _readOnlyAuxiliaryPanes = _auxiliaryPanes.AsReadOnly();
            _readOnlyWindowPanes = _windowPanes.AsReadOnly();
            SubscribeToPane(PrimaryPane);
        }

        public event Action GlobalChanged;

        public event Action<ZepPaneChange> PaneChanged;

        // Every PaneChanged raise goes through here so the dispatch count is a
        // single fact: subscribers run ApplyProjection, so "how many times did
        // the panes publish" is the number the scaling work is measured on.
        private void RaisePaneChanged(ZepPaneChange change)
        {
            ZepPerfLog.Count(ZepPerfCounter.PaneChangedDispatches);
            PaneChanged?.Invoke(change);
        }

        public ZepPaneState PrimaryPane { get; }

        public IReadOnlyList<ZepPaneState> AuxiliaryPanes =>
            _readOnlyAuxiliaryPanes;

        internal IReadOnlyList<ZepPaneState> WindowPanes => _readOnlyWindowPanes;

        public ZepPaneState ActivePane { get; private set; }

        internal IEnumerable<ZepPaneState> Panes
        {
            get
            {
                yield return PrimaryPane;
                for (var i = 0; i < _auxiliaryPanes.Count; i++)
                {
                    yield return _auxiliaryPanes[i];
                }

                for (var i = 0; i < _windowPanes.Count; i++)
                {
                    yield return _windowPanes[i];
                }
            }
        }

        public IReadOnlyList<ZepFolderEntry> Folders => _folders;

        public IReadOnlyList<ZepTagFilterOption> TagFilterOptions =>
            _tagFilterOptions;

        public string StatusMessage => _statusMessage;

        internal bool CanUndo => _canUndo;

        internal bool CanRedo => _canRedo;

        public bool IsIndexScanInProgress => _isIndexScanInProgress;

        public bool IsLivePane(ZepPaneState pane)
        {
            return pane != null
                && (ReferenceEquals(pane, PrimaryPane)
                    || _auxiliaryPanes.Contains(pane)
                    || _windowPanes.Contains(pane));
        }

        public ZepPaneState OpenWindowPane(ZepPaneSource source)
        {
            if (!source.IsAuxiliary)
            {
                throw new ArgumentException("A window pane requires a folder or tag source.", nameof(source));
            }

            // A native window owns its browsing state even when another window
            // or the split workspace shows the same source.
            var pane = new ZepPaneState(source);
            SubscribeToPane(pane);
            _windowPanes.Add(pane);
            ActivePane = pane;
            RaisePaneChanged(ZepPaneChange.Added(pane));
            return pane;
        }

        public bool CloseWindowPane(ZepPaneState pane)
        {
            if (!_windowPanes.Remove(pane))
            {
                return false;
            }

            UnsubscribeFromPane(pane);
            if (ReferenceEquals(ActivePane, pane))
            {
                ActivePane = PrimaryPane;
            }

            RaisePaneChanged(ZepPaneChange.Closed(pane, ActivePane));
            return true;
        }

        // Split UI asks the collection owner whether width must be reserved
        // before opening. Capacity and duplicate-source policy stay here so
        // visual hosts never grow a second MaxSplitViews check.
        public bool WouldOpenAuxiliaryAddPane(ZepPaneSource source)
        {
            return source.IsAuxiliary
                && FindAuxiliary(source) == null
                && _auxiliaryPanes.Count < MaxSplitViews;
        }

        public ZepSplitOperationResult OpenAuxiliary(
            ZepPaneSource source,
            ZepPaneState replacementAuxiliary = null)
        {
            if (!source.IsAuxiliary)
            {
                return ZepSplitOperationResult.Rejected();
            }

            var existing = FindAuxiliary(source);
            if (existing != null)
            {
                return ActivatePane(existing);
            }

            if (_auxiliaryPanes.Count < MaxSplitViews)
            {
                var addedPane = new ZepPaneState(source);
                SubscribeToPane(addedPane);
                _auxiliaryPanes.Add(addedPane);
                ActivePane = addedPane;
                RaisePaneChanged(ZepPaneChange.Added(addedPane));
                return ZepSplitOperationResult.Added(addedPane);
            }

            ZepPaneState replacedPane;
            if (replacementAuxiliary != null)
            {
                if (!_auxiliaryPanes.Contains(replacementAuxiliary))
                {
                    return ZepSplitOperationResult.Rejected();
                }

                replacedPane = replacementAuxiliary;
            }
            else if (_auxiliaryPanes.Contains(ActivePane))
            {
                replacedPane = ActivePane;
            }
            else
            {
                replacedPane = _auxiliaryPanes[_auxiliaryPanes.Count - 1];
            }

            var replacementIndex = _auxiliaryPanes.IndexOf(replacedPane);
            var replacementPane = new ZepPaneState(source);
            SubscribeToPane(replacementPane);
            _auxiliaryPanes[replacementIndex] = replacementPane;
            UnsubscribeFromPane(replacedPane);
            ActivePane = replacementPane;
            RaisePaneChanged(ZepPaneChange.Replaced(
                replacedPane,
                replacementPane));
            return ZepSplitOperationResult.Replaced(
                replacedPane,
                replacementPane);
        }

        public ZepSplitOperationResult ActivatePane(ZepPaneState pane)
        {
            if (!IsLivePane(pane))
            {
                return ZepSplitOperationResult.Rejected();
            }

            var oldActivePane = ActivePane;
            ActivePane = pane;
            RaisePaneChanged(ZepPaneChange.Activated(oldActivePane, pane));
            return ZepSplitOperationResult.Activated(oldActivePane, pane);
        }

        // The primary is structurally outside this API and outside AuxiliaryPanes.
        public ZepSplitOperationResult CloseAuxiliary(
            ZepPaneState auxiliaryPane)
        {
            var auxiliaryIndex = _auxiliaryPanes.IndexOf(auxiliaryPane);
            if (auxiliaryIndex < 0)
            {
                return ZepSplitOperationResult.Rejected();
            }

            var wasActive = ReferenceEquals(ActivePane, auxiliaryPane);
            _auxiliaryPanes.RemoveAt(auxiliaryIndex);
            UnsubscribeFromPane(auxiliaryPane);
            if (wasActive)
            {
                ActivePane = auxiliaryIndex > 0
                    ? _auxiliaryPanes[auxiliaryIndex - 1]
                    : PrimaryPane;
            }

            RaisePaneChanged(ZepPaneChange.Closed(auxiliaryPane, ActivePane));
            return ZepSplitOperationResult.Closed(auxiliaryPane, ActivePane);
        }

        // Domain restoration supplies fully reconciled, detached pane candidates.
        // The collection owner still enforces capacity, source uniqueness, live
        // subscriptions, and active-pane validity, then publishes one stable
        // collection delta instead of replaying Added/Activated churn.
        public bool RestoreAuxiliaries(
            IReadOnlyList<ZepPaneState> candidates,
            ZepPaneState activeAuxiliary)
        {
            if (_auxiliaryPanes.Count != 0)
            {
                return false;
            }

            var restoredBySource =
                new Dictionary<ZepPaneSource, ZepPaneState>();
            ZepPaneState restoredActive = null;
            if (candidates != null)
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    var pane = candidates[i];
                    if (pane == null || !pane.Source.IsAuxiliary)
                    {
                        continue;
                    }

                    if (restoredBySource.TryGetValue(
                        pane.Source,
                        out var equivalentPane))
                    {
                        if (ReferenceEquals(activeAuxiliary, pane))
                        {
                            restoredActive = equivalentPane;
                        }

                        continue;
                    }

                    if (_auxiliaryPanes.Count >= MaxSplitViews)
                    {
                        continue;
                    }

                    SubscribeToPane(pane);
                    _auxiliaryPanes.Add(pane);
                    restoredBySource.Add(pane.Source, pane);
                    if (ReferenceEquals(activeAuxiliary, pane))
                    {
                        restoredActive = pane;
                    }
                }
            }

            ActivePane = restoredActive ?? PrimaryPane;

            if (_auxiliaryPanes.Count > 0)
            {
                RaisePaneChanged(ZepPaneChange.Restored(ActivePane));
            }

            return true;
        }

        internal IDisposable DeferNotifications()
        {
            _notificationDeferralDepth++;
            return new NotificationDeferral(this);
        }

        public void SetStatusMessage(string statusMessage)
        {
            var normalized = statusMessage ?? string.Empty;
            if (string.Equals(_statusMessage, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _statusMessage = normalized;
            RaiseGlobalChanged();
        }

        internal void SetCanUndoRedo(bool canUndo, bool canRedo)
        {
            if (_canUndo == canUndo && _canRedo == canRedo)
            {
                return;
            }

            _canUndo = canUndo;
            _canRedo = canRedo;
            RaiseGlobalChanged();
        }

        internal void SetIsIndexScanInProgress(bool isIndexScanInProgress)
        {
            if (_isIndexScanInProgress == isIndexScanInProgress)
            {
                return;
            }

            _isIndexScanInProgress = isIndexScanInProgress;
            RaiseGlobalChanged();
        }

        public void SetFolders(IEnumerable<ZepFolderEntry> folders)
        {
            if (ReplaceContents(_folders, folders))
            {
                RebuildFolderPathIndex();
                RaiseGlobalChanged();
            }
        }

        private void RebuildFolderPathIndex()
        {
            _folderPaths.Clear();
            for (var i = 0; i < _folders.Count; i++)
            {
                var folder = _folders[i];
                if (folder != null && !string.IsNullOrEmpty(folder.Path))
                {
                    _folderPaths.Add(folder.Path);
                }
            }
        }

        public void SetTagFilterOptions(
            IEnumerable<ZepTagFilterOption> tagFilterOptions)
        {
            if (ReplaceContents(_tagFilterOptions, tagFilterOptions))
            {
                RaiseGlobalChanged();
            }
        }

        public bool IsFolderAssetEntry(ZepAssetEntry asset)
        {
            if (asset == null)
            {
                return false;
            }

            return asset.IsFolder
                || (!string.IsNullOrEmpty(asset.Path) && _folderPaths.Contains(asset.Path));
        }

        private ZepPaneState FindAuxiliary(ZepPaneSource source)
        {
            for (var i = 0; i < _auxiliaryPanes.Count; i++)
            {
                if (_auxiliaryPanes[i].Source == source)
                {
                    return _auxiliaryPanes[i];
                }
            }

            return null;
        }

        private void SubscribeToPane(ZepPaneState pane)
        {
            pane.Changed += HandlePaneChanged;
        }

        private void UnsubscribeFromPane(ZepPaneState pane)
        {
            pane.Changed -= HandlePaneChanged;
        }

        private void HandlePaneChanged(
            ZepPaneState pane,
            ZepPaneUpdate update)
        {
            if (_notificationDeferralDepth > 0)
            {
                if (_deferredPaneUpdates.TryGetValue(pane, out var deferredUpdate))
                {
                    _deferredPaneUpdates[pane] = deferredUpdate | update;
                }
                else
                {
                    _deferredPaneUpdates.Add(pane, update);
                }

                return;
            }

            RaisePaneChanged(ZepPaneChange.Updated(pane, update));
        }

        private void RaiseGlobalChanged()
        {
            if (_notificationDeferralDepth > 0)
            {
                _hasDeferredGlobalChange = true;
                return;
            }

            GlobalChanged?.Invoke();
        }

        private void EndNotificationDeferral()
        {
            if (_notificationDeferralDepth == 0)
            {
                return;
            }

            _notificationDeferralDepth--;
            if (_notificationDeferralDepth != 0)
            {
                return;
            }

            var deferredPaneUpdates = _deferredPaneUpdates;
            _deferredPaneUpdates =
                new Dictionary<ZepPaneState, ZepPaneUpdate>();
            var hasDeferredGlobalChange = _hasDeferredGlobalChange;
            _hasDeferredGlobalChange = false;

            foreach (var pane in Panes)
            {
                if (deferredPaneUpdates.TryGetValue(pane, out var update))
                {
                    RaisePaneChanged(ZepPaneChange.Updated(pane, update));
                }
            }

            if (hasDeferredGlobalChange)
            {
                GlobalChanged?.Invoke();
            }
        }

        private static bool ReplaceContents<T>(List<T> target, IEnumerable<T> source)
        {
            if (source == null)
            {
                if (target.Count == 0)
                {
                    return false;
                }

                target.Clear();
                return true;
            }

            if (SequenceEquals(target, source))
            {
                return false;
            }

            target.Clear();
            foreach (var item in source)
            {
                target.Add(item);
            }

            return true;
        }

        private static bool SequenceEquals<T>(
            IReadOnlyList<T> target,
            IEnumerable<T> source)
        {
            if (source is IReadOnlyList<T> sourceList
                && sourceList.Count != target.Count)
            {
                return false;
            }

            var comparer = EqualityComparer<T>.Default;
            var index = 0;
            foreach (var item in source)
            {
                if (index >= target.Count || !comparer.Equals(target[index], item))
                {
                    return false;
                }

                index++;
            }

            return index == target.Count;
        }

        private sealed class NotificationDeferral : IDisposable
        {
            private ZepState _owner;

            public NotificationDeferral(ZepState owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                if (_owner == null)
                {
                    return;
                }

                _owner.EndNotificationDeferral();
                _owner = null;
            }
        }
    }
}
