using System;
using System.Collections.Generic;
using Zep.Core.Domain;
using Zep.Core.FileOperations;

namespace Zep.Core.State
{
    public sealed class ZepPaneState
    {
        private static readonly IReadOnlyList<ZepAssetEntry> EmptyAssets =
            Array.Empty<ZepAssetEntry>();

        private readonly List<ZepSearchScopeOption> _searchScopeOptions =
            new List<ZepSearchScopeOption>();
        private readonly List<ZepTypeFilterOption> _typeFilterOptions =
            new List<ZepTypeFilterOption>();
        private readonly List<AssetPath> _selectedAssetPaths = new List<AssetPath>();
        private readonly HashSet<AssetPath> _expandedAssetFolders = new HashSet<AssetPath>();
        // Sidebar multi-selection: the folders listed TOGETHER with the current
        // one. Primary browse only, never holds the current folder, and is
        // relative to it — any change of the current folder, a plain folder
        // selection, a search, or a tag view empties it. Not persisted and not in history.
        private readonly List<AssetPath> _additionalFolderPaths = new List<AssetPath>();
        private readonly List<NavigationEntry> _history = new List<NavigationEntry>();

        // Far past any usable Back depth, and small enough that the list stays a
        // rounding error for the life of the editor session.
        private const int MaxHistoryDepth = 256;
        private int _notificationDeferralDepth;
        private ZepPaneUpdate _deferredUpdate;
        private bool _hasDeferredNavButtonsChange;
        private string _searchText = string.Empty;
        private ZepSearchScope _selectedSearchScope;
        private string _selectedTypeFilter = string.Empty;
        private string _primarySelectedTagFilterId = string.Empty;
        private AssetPath _currentFolderPath;
        private AssetPath _selectedFolderPath;
        private AssetPath _selectedAssetPath;
        private AssetPath _lastSelectedAssetPath;
        // The asset the user picked WHILE a search was live, i.e. an actual
        // search result. Search exit reveals this one — not _lastSelectedAssetPath,
        // which survives navigation and would send a user who searched but picked
        // nothing back to a folder they left long before typing.
        private AssetPath _pickedSearchResult;
        private AssetPath _primaryBrowserFolderPath;
        private ZepSearchScope _primaryBrowserSearchScope = ZepSearchScope.Assets;
        private ZepSearchScope _primaryTagSearchScope = ZepSearchScope.All;
        private Func<IReadOnlyList<ZepAssetEntry>> _visibleAssetsProvider;
        private IReadOnlyList<ZepAssetEntry> _projectedAssets = EmptyAssets;
        // Bumped by every write to _selectedAssetPaths; the key half of the
        // GetSelectedAssetEntries memo below.
        private int _selectionRevision;
        private List<ZepAssetEntry> _selectedEntriesCache;
        private int _selectedEntriesRevision = -1;
        private IReadOnlyList<ZepAssetEntry> _selectedEntriesSource;
        // The ghost row of an inline create is spliced into a STABLE list
        // instance, so the reference above cannot see it; the count can.
        private int _selectedEntriesSourceCount = -1;
        private IReadOnlyList<ZepAssetEntry> _selectedEntriesProjection;
        private Func<IReadOnlyList<ZepAssetEntry>> _selectedEntriesProvider;
        private int _historyIndex = -1;
        private bool _isHistoryNavigation;
        private NavigationEntry _lastKnownEntry = NavigationEntry.None;
        // Tag-sourced panes only. Empty while the pane shows its tag list; the
        // tagged folder the user opened otherwise. It is the ceiling the pane
        // browses under — a rooted folder pane's Source.RootFolderPath, minus
        // the identity: the pane stays "Tag · X" and returns to the list.
        private AssetPath _tagBrowseRootPath;

        public ZepPaneState(ZepPaneSource source)
        {
            Source = source;
            switch (source.Kind)
            {
                case ZepPaneSourceKind.Primary:
                    _primaryBrowserFolderPath = AssetPath.From("Assets");
                    _selectedSearchScope = ZepSearchScope.Assets;
                    break;
                case ZepPaneSourceKind.Folder:
                    _currentFolderPath = source.RootFolderPath;
                    _selectedFolderPath = source.RootFolderPath;
                    _selectedSearchScope = AssetPathClassification.IsPackagesPath(source.RootFolderPath)
                        ? ZepSearchScope.Packages
                        : ZepSearchScope.Assets;
                    break;
                case ZepPaneSourceKind.Tag:
                    _currentFolderPath = AssetPath.From("Assets");
                    _selectedSearchScope = ZepSearchScope.All;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(source));
            }
        }

        internal event Action<ZepPaneState, ZepPaneUpdate> Changed;

        internal event Action<bool, bool> NavButtonsStateChanged;

        public ZepPaneSource Source { get; private set; }

        public string SearchText => _searchText;

        public ZepSearchScope SelectedSearchScope => _selectedSearchScope;

        public string SelectedTypeFilter => _selectedTypeFilter;

        // Empty while a tag pane browses an opened folder: the projection
        // treats any tag filter as "list the tagged assets" regardless of the
        // current folder, so the filter has to read as absent for the folder's
        // contents to show. The tag itself lives in Source and is untouched.
        public string SelectedTagFilterId => Source.Kind == ZepPaneSourceKind.Tag
            ? (_tagBrowseRootPath.IsEmpty ? Source.TagId : string.Empty)
            : _primarySelectedTagFilterId;

        public AssetPath CurrentFolderPath => _currentFolderPath;

        // The folder a tag pane has opened from its list, empty while it shows
        // the list. Only ever non-empty on a Tag source.
        public AssetPath TagBrowseRootPath => _tagBrowseRootPath;

        public AssetPath BreadcrumbRootPath =>
            TryGetBrowseCeiling(out var ceiling) ? ceiling : default;

        public AssetPath BreadcrumbCurrentPath => _currentFolderPath;

        public AssetPath SelectedFolderPath => _selectedFolderPath;

        public IReadOnlyList<AssetPath> AdditionalFolderPaths => _additionalFolderPaths;

        internal AssetPath SelectedAssetPath => _selectedAssetPath;

        internal AssetPath LastSelectedAssetPath => _lastSelectedAssetPath;

        public IReadOnlyList<AssetPath> SelectedAssetPaths => _selectedAssetPaths;

        public IReadOnlyList<ZepSearchScopeOption> SearchScopeOptions =>
            _searchScopeOptions;

        public IReadOnlyList<ZepTypeFilterOption> TypeFilterOptions =>
            _typeFilterOptions;

        public Func<IReadOnlyList<ZepAssetEntry>> VisibleAssetsProvider
        {
            get => _visibleAssetsProvider;
            set
            {
                if (_visibleAssetsProvider == value)
                {
                    return;
                }

                _visibleAssetsProvider = value;
                RaiseChanged(
                    ZepPaneUpdate.VisibleAssetsProvider,
                    notifyNavButtons: false);
            }
        }

        public IReadOnlyList<ZepAssetEntry> ProjectedAssets =>
            _projectedAssets;

        internal void SetProjectedAssets(
            IReadOnlyList<ZepAssetEntry> projectedAssets)
        {
            var normalized = projectedAssets ?? EmptyAssets;
            if (ReferenceEquals(_projectedAssets, normalized))
            {
                return;
            }

            _projectedAssets = normalized;
            RaiseChanged(
                ZepPaneUpdate.Projection,
                notifyNavButtons: false);
        }

        public AssetsPaneMode ActiveMode
        {
            get
            {
                switch (Source.Kind)
                {
                    case ZepPaneSourceKind.Folder:
                        return AssetsPaneMode.Browser;
                    case ZepPaneSourceKind.Tag:
                        return _tagBrowseRootPath.IsEmpty
                            ? AssetsPaneMode.Tags
                            : AssetsPaneMode.Browser;
                }

                if (!string.IsNullOrEmpty(_primarySelectedTagFilterId))
                {
                    return AssetsPaneMode.Tags;
                }

                return AssetsPaneMode.Browser;
            }
        }

        public bool CanNavigateBack => _historyIndex > 0;

        public bool CanNavigateForward =>
            _historyIndex >= 0 && _historyIndex < _history.Count - 1;

        public NavigationEntry CurrentEntry =>
            _historyIndex >= 0 ? _history[_historyIndex] : NavigationEntry.None;

        internal IDisposable DeferNotifications()
        {
            _notificationDeferralDepth++;
            return new NotificationDeferral(this);
        }

        internal void SetSearchText(string searchText)
        {
            var normalized = searchText ?? string.Empty;
            if (string.Equals(_searchText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _searchText = normalized;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                // The search session ends here, so whatever was picked inside it
                // stops being a reveal target for the NEXT search. Blank counts
                // as no search everywhere else too (HasActiveAssetFilter, the
                // projection's hasSearchText).
                _pickedSearchResult = default;
            }
            else
            {
                // A search replaces the folder listing, and its "current
                // folder" scope is one folder: the multi-selection ends here.
                _additionalFolderPaths.Clear();
            }

            RaiseChanged(ZepPaneUpdate.Search);
        }

        public bool SetSelectedSearchScope(ZepSearchScope selectedSearchScope)
        {
            if (!IsSearchScopeAllowed(selectedSearchScope))
            {
                return false;
            }

            if (_selectedSearchScope == selectedSearchScope)
            {
                RememberSearchScope(ActiveMode, selectedSearchScope);
                return true;
            }

            _selectedSearchScope = selectedSearchScope;
            RememberSearchScope(ActiveMode, selectedSearchScope);
            RaiseChanged(ZepPaneUpdate.Filter);
            return true;
        }

        // Mechanical reconciliation from the projection, NOT user intent: the
        // scope handed in here is whatever survives the current folder's option
        // list. At the Assets root that list has no CurrentFolder entry, so the
        // resolver falls back to All — recording that would erase a
        // CurrentFolder preference the moment the user passes through the root.
        // The remembered per-mode scope is written only by the public setter.
        internal void ReconcileSelectedSearchScope(ZepSearchScope selectedSearchScope)
        {
            if (!IsSearchScopeAllowed(selectedSearchScope)
                || _selectedSearchScope == selectedSearchScope)
            {
                return;
            }

            _selectedSearchScope = selectedSearchScope;
            RaiseChanged(ZepPaneUpdate.Filter);
        }

        public void SetSelectedTypeFilter(string selectedTypeFilter)
        {
            var normalized = selectedTypeFilter ?? string.Empty;
            if (string.Equals(_selectedTypeFilter, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _selectedTypeFilter = normalized;
            RaiseChanged(ZepPaneUpdate.Filter);
        }

        internal bool SetSelectedTagFilterId(string selectedTagFilterId)
        {
            var normalized = selectedTagFilterId ?? string.Empty;
            if (Source.Kind == ZepPaneSourceKind.Tag)
            {
                // Pinned to its tag: re-asserting it while a folder is open
                // returns to the list, anything else is refused.
                if (!string.Equals(Source.TagId, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                LeaveTagBrowseRoot();
                return true;
            }

            if (!Source.IsPrimary)
            {
                return string.IsNullOrEmpty(normalized);
            }

            if (string.Equals(
                _primarySelectedTagFilterId,
                normalized,
                StringComparison.Ordinal))
            {
                return true;
            }

            _primarySelectedTagFilterId = normalized;
            if (normalized.Length > 0)
            {
                _additionalFolderPaths.Clear();
            }

            RaiseChanged(ZepPaneUpdate.Navigation | ZepPaneUpdate.Filter);
            return true;
        }

        public bool TrySetCurrentFolderPath(AssetPath currentFolderPath)
        {
            if (!CanNavigateTo(currentFolderPath))
            {
                return false;
            }

            if (_currentFolderPath == currentFolderPath)
            {
                return true;
            }

            _currentFolderPath = currentFolderPath;
            _additionalFolderPaths.Clear();
            RaiseChanged(ZepPaneUpdate.Navigation);
            return true;
        }

        public bool TrySetSelectedFolderPath(AssetPath selectedFolderPath)
        {
            if (!CanSelectFolder(selectedFolderPath))
            {
                return false;
            }

            if (_selectedFolderPath == selectedFolderPath)
            {
                return true;
            }

            _selectedFolderPath = selectedFolderPath;
            RaiseChanged(ZepPaneUpdate.Selection);
            return true;
        }

        // ⌘/Ctrl-click on a sidebar folder. Adds the folder to, or removes it
        // from, the folders listed with the current one. Toggling the current
        // folder itself hands "current" to the next selected folder, and is a
        // no-op when it is the only one. Where several folders cannot be listed
        // together (see CanListFoldersTogether) it is a plain selection.
        public bool TryToggleFolderInSelection(string folderPath)
        {
            var typedFolderPath = AssetPath.From(folderPath);
            if (typedFolderPath.IsEmpty)
            {
                return false;
            }

            if (!CanListFoldersTogether())
            {
                return TrySetFolderSelection(folderPath);
            }

            if (typedFolderPath == _currentFolderPath)
            {
                if (_additionalFolderPaths.Count == 0)
                {
                    return true;
                }

                var nextCurrent = _additionalFolderPaths[0];
                var remaining = _additionalFolderPaths.GetRange(
                    1,
                    _additionalFolderPaths.Count - 1);
                using (DeferNotifications())
                {
                    TrySetCurrentFolderPath(nextCurrent);
                    TrySetSelectedFolderPath(nextCurrent);
                    SetAdditionalFolderPaths(remaining);
                    SetSelectedAssetPaths(Array.Empty<AssetPath>());
                }

                return true;
            }

            var toggled = new List<AssetPath>(_additionalFolderPaths);
            if (!toggled.Remove(typedFolderPath))
            {
                toggled.Add(typedFolderPath);
            }

            SetAdditionalFolderPaths(toggled);
            return true;
        }

        // Shift-click on a sidebar folder: the current folder stays the anchor
        // and rangeFolderPaths (the sidebar rows from the anchor through the
        // clicked one) replace whatever was listed with it.
        public bool TrySelectFolderRange(
            string clickedFolderPath,
            IReadOnlyList<string> rangeFolderPaths)
        {
            if (!CanListFoldersTogether() || rangeFolderPaths == null)
            {
                return TrySetFolderSelection(clickedFolderPath);
            }

            var range = new List<AssetPath>(rangeFolderPaths.Count);
            for (var i = 0; i < rangeFolderPaths.Count; i++)
            {
                range.Add(AssetPath.From(rangeFolderPaths[i]));
            }

            SetAdditionalFolderPaths(range);
            return true;
        }

        // Replaces the folders listed with the current one. Empty entries, the
        // current folder and repeats are dropped, so the invariant on the field
        // holds whatever the caller passes.
        internal void SetAdditionalFolderPaths(IReadOnlyList<AssetPath> folderPaths)
        {
            var next = new List<AssetPath>(folderPaths?.Count ?? 0);
            if (folderPaths != null && CanListFoldersTogether())
            {
                for (var i = 0; i < folderPaths.Count; i++)
                {
                    var folderPath = folderPaths[i];
                    if (!folderPath.IsEmpty
                        && folderPath != _currentFolderPath
                        && !next.Contains(folderPath))
                    {
                        next.Add(folderPath);
                    }
                }
            }

            var unchanged = next.Count == _additionalFolderPaths.Count;
            for (var i = 0; unchanged && i < next.Count; i++)
            {
                unchanged = next[i] == _additionalFolderPaths[i];
            }

            if (unchanged)
            {
                return;
            }

            _additionalFolderPaths.Clear();
            _additionalFolderPaths.AddRange(next);
            RaiseChanged(ZepPaneUpdate.Navigation);
        }

        // Several folders are listed together only where the sidebar's folder
        // selection IS the pane's content: the primary pane, browsing a folder,
        // with no search and no tag view replacing that content.
        private bool CanListFoldersTogether()
        {
            return Source.IsPrimary
                && !_currentFolderPath.IsEmpty
                && string.IsNullOrWhiteSpace(_searchText)
                && string.IsNullOrEmpty(_primarySelectedTagFilterId);
        }

        public void SetSelectedAssetPath(AssetPath selectedAssetPath)
        {
            if (selectedAssetPath.IsEmpty)
            {
                SetSelectedAssetPaths(Array.Empty<AssetPath>());
                return;
            }

            // Re-asserting the current single selection is the common case (every
            // reconcile after a projection, and every click on the already
            // selected row). Going through SetSelectedAssetPaths for it built an
            // array, a List and a HashSet before the equality check below threw
            // them away. The scope check has to stay: a path can stop being an
            // allowed search result after it was selected, and then this call
            // must clear the selection rather than confirm it.
            if (_selectedAssetPaths.Count == 1
                && _selectedAssetPaths[0] == selectedAssetPath
                && _selectedAssetPath == selectedAssetPath
                && IsSearchResultAllowed(selectedAssetPath))
            {
                return;
            }

            SetSelectedAssetPaths(new[] { selectedAssetPath });
        }

        public void SetSelectedAssetPaths(IEnumerable<AssetPath> selectedAssetPaths)
        {
            var normalized = NormalizeSelectedAssetPaths(selectedAssetPaths);
            var primarySelection = normalized.Count > 0 ? normalized[0] : default;
            if (_selectedAssetPath == primarySelection
                && SequenceEquals(_selectedAssetPaths, normalized))
            {
                return;
            }

            _selectedAssetPath = primarySelection;
            if (!primarySelection.IsEmpty)
            {
                _lastSelectedAssetPath = primarySelection;
                if (!string.IsNullOrWhiteSpace(_searchText))
                {
                    _pickedSearchResult = primarySelection;
                }
            }

            ReplaceContents(_selectedAssetPaths, normalized);
            _selectionRevision++;
            RaiseChanged(ZepPaneUpdate.Selection);
        }

        // Folders the assets LIST view shows inline-expanded (Finder-style
        // disclosure). Session-local browsing state, like the sub-asset
        // expansion: not serialized, keyed by path so it survives navigating
        // away and back. Owned here — not in the view — because selection
        // reconciliation must treat a selected child of an expanded folder as
        // in-view (see ZepController.ReconcileSelectedAssetPaths).
        public IReadOnlyCollection<AssetPath> ExpandedAssetFolderPaths => _expandedAssetFolders;

        public bool IsAssetFolderExpanded(AssetPath folderPath)
        {
            return !folderPath.IsEmpty && _expandedAssetFolders.Contains(folderPath);
        }

        public void ToggleAssetFolderExpanded(AssetPath folderPath)
        {
            SetAssetFolderExpanded(folderPath, !IsAssetFolderExpanded(folderPath));
        }

        public void SetAssetFolderExpanded(AssetPath folderPath, bool expanded)
        {
            if (folderPath.IsEmpty)
            {
                return;
            }

            var changed = expanded
                ? _expandedAssetFolders.Add(folderPath)
                : _expandedAssetFolders.Remove(folderPath);
            if (!changed)
            {
                return;
            }

            using (DeferNotifications())
            {
                // Finder behaviour: collapsing a folder whose descendants are
                // selected moves the selection onto the folder itself rather
                // than leaving it on rows that just disappeared.
                if (!expanded && SelectionContainsDescendantOf(folderPath))
                {
                    SetSelectedAssetPaths(new[] { folderPath });
                }

                RaiseChanged(ZepPaneUpdate.Expansion);
            }
        }

        private bool SelectionContainsDescendantOf(AssetPath folderPath)
        {
            for (var i = 0; i < _selectedAssetPaths.Count; i++)
            {
                var selected = _selectedAssetPaths[i];
                if (!selected.Equals(folderPath)
                    && Application.ZepPathHelpers.IsWithinFolderScope(
                        selected.Value,
                        folderPath.Value))
                {
                    return true;
                }
            }

            return false;
        }

        public void SetSearchScopeOptions(
            IEnumerable<ZepSearchScopeOption> searchScopeOptions)
        {
            var allowed = new List<ZepSearchScopeOption>();
            if (searchScopeOptions != null)
            {
                foreach (var option in searchScopeOptions)
                {
                    if (IsSearchScopeAllowed(option.Scope))
                    {
                        allowed.Add(option);
                    }
                }
            }

            if (ReplaceContents(_searchScopeOptions, allowed))
            {
                RaiseChanged(ZepPaneUpdate.Options);
            }
        }

        public void SetTypeFilterOptions(
            IEnumerable<ZepTypeFilterOption> typeFilterOptions)
        {
            if (ReplaceContents(_typeFilterOptions, typeFilterOptions))
            {
                RaiseChanged(ZepPaneUpdate.Options);
            }
        }

        public void NavigateBack()
        {
            NavigateHistory(-1);
        }

        public void NavigateForward()
        {
            NavigateHistory(1);
        }

        public void SyncNavigationHistory()
        {
            var entry = ResolveCurrentNavigationEntry();
            if (entry.IsEmpty)
            {
                FireNavButtons();
                return;
            }

            if (_isHistoryNavigation)
            {
                _lastKnownEntry = entry;
                FireNavButtons();
                return;
            }

            if (entry.Equals(_lastKnownEntry))
            {
                FireNavButtons();
                return;
            }

            if (_historyIndex >= 0
                && _historyIndex < _history.Count
                && _history[_historyIndex].Equals(entry))
            {
                _lastKnownEntry = entry;
                FireNavButtons();
                return;
            }

            if (_historyIndex < _history.Count - 1)
            {
                _history.RemoveRange(
                    _historyIndex + 1,
                    _history.Count - _historyIndex - 1);
            }

            _history.Add(entry);
            // Bounded from the front. Nothing else ever shrinks this list —
            // only a forward branch truncates, and that truncates the FUTURE —
            // so a long editing session grew one NavigationEntry per navigation
            // for the life of the pane. The cursor sits on the entry just added,
            // so dropping the oldest entries keeps it on the newest one and Back
            // simply stops MaxHistoryDepth steps ago instead of at the session's
            // first folder.
            if (_history.Count > MaxHistoryDepth)
            {
                _history.RemoveRange(0, _history.Count - MaxHistoryDepth);
            }

            _historyIndex = _history.Count - 1;
            _lastKnownEntry = entry;
            RaiseChanged(ZepPaneUpdate.History);
        }

        public IReadOnlyList<NavigationEntry> NavigationHistory => _history;

        public int NavigationHistoryIndex => _historyIndex;

        // Re-applies a history captured from this pane before a domain reload,
        // which rebuilds the pane from scratch. Entries are trusted as they were
        // recorded; a folder that vanished meanwhile is redirected when Back
        // lands on it, exactly as it is for a folder deleted mid-session.
        public void RestoreNavigationHistory(
            IReadOnlyList<NavigationEntry> entries,
            int index)
        {
            _history.Clear();
            if (entries != null)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    if (!entries[i].IsEmpty)
                    {
                        _history.Add(entries[i]);
                    }
                }
            }

            if (_history.Count > MaxHistoryDepth)
            {
                _history.RemoveRange(0, _history.Count - MaxHistoryDepth);
            }

            _historyIndex = _history.Count == 0
                ? -1
                : Math.Max(0, Math.Min(index, _history.Count - 1));
            _lastKnownEntry = _historyIndex >= 0
                ? _history[_historyIndex]
                : NavigationEntry.None;
            RaiseChanged(ZepPaneUpdate.History);
        }

        internal bool ApplyEntry(NavigationEntry entry)
        {
            if (Source.Kind == ZepPaneSourceKind.Tag
                && entry.IsFolder
                && _tagBrowseRootPath.IsEmpty)
            {
                // History re-entry from the list. A double-click from the list
                // always makes the opened folder the ceiling; history instead
                // restores the ceiling the entry was recorded under, so Back
                // past the tag and Forward again cannot narrow the pane to a
                // folder the user only ever reached from inside its parent.
                var folder = AssetPath.From(entry.FolderPath);
                var ceiling = entry.BrowseRootPath != null
                    ? AssetPath.From(entry.BrowseRootPath)
                    : folder;
                return TryEnterTagFolder(ceiling, folder);
            }

            if (entry.IsFolder)
            {
                return TrySetFolderSelection(entry.FolderPath);
            }

            if (entry.IsTag)
            {
                return ApplyTagView(entry.TagId);
            }

            return false;
        }

        public bool TrySetFolderSelection(string folderPath)
        {
            var typedFolderPath = AssetPath.From(folderPath);
            if (Source.Kind == ZepPaneSourceKind.Tag
                && _tagBrowseRootPath.IsEmpty)
            {
                // Opening a folder from the tag list: that folder becomes the
                // ceiling, exactly as dropping it into a split would have.
                return TryEnterTagFolder(typedFolderPath, typedFolderPath);
            }

            if (!CanNavigateTo(typedFolderPath))
            {
                return false;
            }

            using (DeferNotifications())
            {
                if (Source.IsPrimary)
                {
                    SetSelectedTagFilterId(string.Empty);
                }

                SetSearchText(string.Empty);
                TrySetCurrentFolderPath(typedFolderPath);
                TrySetSelectedFolderPath(typedFolderPath);
                SetAdditionalFolderPaths(null);
                if (Source.IsPrimary)
                {
                    SetSelectedSearchScope(_primaryBrowserSearchScope);
                }

                SetSelectedAssetPaths(Array.Empty<AssetPath>());
            }

            return true;
        }

        // Source ceilings and roots bound upward navigation; tag lists have no parent.
        public bool TryNavigateToParentFolder()
        {
            string current = _currentFolderPath;
            if (string.IsNullOrEmpty(current))
            {
                return false;
            }

            if (ActiveMode != AssetsPaneMode.Browser)
            {
                return false;
            }

            // "Assets" and "Packages" (slash at 0 or absent) have no parent to
            // browse to; TrySetFolderSelection is the same path the breadcrumb takes.
            var slash = current.LastIndexOf('/');
            if (slash <= 0)
            {
                return false;
            }

            return TrySetFolderSelection(current.Substring(0, slash));
        }

        public bool ApplyTagView(string tagId)
        {
            var normalizedTagId = tagId ?? string.Empty;
            if (Source.Kind == ZepPaneSourceKind.Tag)
            {
                return SetSelectedTagFilterId(normalizedTagId);
            }

            if (!Source.IsPrimary)
            {
                return false;
            }

            using (DeferNotifications())
            {
                TrySetSelectedFolderPath(default);
                SetSelectedTagFilterId(normalizedTagId);
                SetSelectedSearchScope(_primaryTagSearchScope);
            }

            return true;
        }

        // The tag-list toggle: asserting the tag this pane is already showing
        // turns the tag view back off, anything else switches to it. A tag pane
        // reads its own tag as absent while it browses an opened folder, so the
        // same call there is the "switch to it" leg — which returns it to the list.
        internal bool ToggleTagView(string tagId)
        {
            if (string.Equals(
                SelectedTagFilterId,
                tagId,
                StringComparison.OrdinalIgnoreCase))
            {
                return SetSelectedTagFilterId(string.Empty);
            }

            return ApplyTagView(tagId);
        }

        public bool EnterMode(AssetsPaneMode mode)
        {
            if (!Source.IsPrimary)
            {
                if (Source.Kind == ZepPaneSourceKind.Tag
                    && mode == AssetsPaneMode.Tags)
                {
                    LeaveTagBrowseRoot();
                    return true;
                }

                return mode == ActiveMode;
            }

            if (ActiveMode == mode)
            {
                return true;
            }

            var outgoingMode = ActiveMode;
            if (outgoingMode == AssetsPaneMode.Browser)
            {
                _primaryBrowserFolderPath = _currentFolderPath;
            }

            using (DeferNotifications())
            {
                switch (mode)
                {
                    case AssetsPaneMode.Browser:
                        SetSelectedTagFilterId(string.Empty);
                        SetSearchText(string.Empty);
                        var browserFolder = _primaryBrowserFolderPath.IsEmpty
                            ? AssetPath.From("Assets")
                            : _primaryBrowserFolderPath;
                        TrySetCurrentFolderPath(browserFolder);
                        TrySetSelectedFolderPath(browserFolder);
                        SetSelectedSearchScope(_primaryBrowserSearchScope);
                        SetSelectedAssetPaths(Array.Empty<AssetPath>());
                        break;
                    case AssetsPaneMode.Tags:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mode));
                }

                RaiseChanged(ZepPaneUpdate.Navigation);
            }

            return true;
        }

        internal bool TryRevealPickedSearchResultOnExit(out AssetPath? revealedAsset)
        {
            revealedAsset = null;
            if ((Source.Kind == ZepPaneSourceKind.Tag && _tagBrowseRootPath.IsEmpty)
                || _pickedSearchResult.IsEmpty)
            {
                return false;
            }

            var asset = _pickedSearchResult;
            if (!IsSearchResultAllowed(asset))
            {
                return false;
            }

            // A tag view has no containing folder to reveal into — the tag IS
            // the container, and the pick still carries the tag once the search
            // ends. Clearing the filter (or navigating to the asset's folder)
            // would drop the user out of the very view they were searching, so
            // here the exit only ends the search and keeps the pick selected;
            // the caller still frames it.
            if (!string.IsNullOrEmpty(SelectedTagFilterId))
            {
                using (DeferNotifications())
                {
                    SetSearchText(string.Empty);
                    SetSelectedAssetPaths(new[] { asset });
                }

                revealedAsset = asset;
                return true;
            }

            var parentFolder = AssetPath.From(GetParentFolderPath(asset.Value));
            if (parentFolder.IsEmpty || !CanNavigateTo(parentFolder))
            {
                return false;
            }

            using (DeferNotifications())
            {
                if (Source.IsPrimary)
                {
                    SetSelectedTagFilterId(string.Empty);
                    _primaryBrowserFolderPath = parentFolder;
                }

                SetSearchText(string.Empty);
                TrySetCurrentFolderPath(parentFolder);
                TrySetSelectedFolderPath(parentFolder);
                if (Source.IsPrimary)
                {
                    SetSelectedSearchScope(_primaryBrowserSearchScope);
                }

                SetSelectedAssetPaths(new[] { asset });
            }

            revealedAsset = asset;
            return true;
        }

        public bool HasActiveAssetFilter()
        {
            return !string.IsNullOrWhiteSpace(_searchText)
                || !string.IsNullOrWhiteSpace(_selectedTypeFilter)
                || !string.IsNullOrWhiteSpace(SelectedTagFilterId);
        }

        // HasActiveAssetFilter minus the container: in a tag view the tag IS
        // the container being browsed (ActiveMode derives Tags from it), not a
        // filter narrowing one. Chrome that asks "is this view filtered?" —
        // the empty-state message — counts only what a user can stack on top
        // of any container: search and the type filter. The tag filter cannot
        // be non-empty in any other mode, so outside a tag view the two
        // queries agree.
        public bool HasAssetFilterBeyondContainer()
        {
            return !string.IsNullOrWhiteSpace(_searchText)
                || !string.IsNullOrWhiteSpace(_selectedTypeFilter);
        }

        internal bool IsPathWithinSource(AssetPath path)
        {
            if (!TryGetBrowseCeiling(out var ceiling))
            {
                return true;
            }

            return IsSameOrDescendant(path.Value, ceiling.Value);
        }

        internal bool IsSearchResultAllowed(AssetPath path)
        {
            return !path.IsEmpty && IsPathWithinSource(path);
        }

        internal bool IsSearchScopeAllowed(ZepSearchScope scope)
        {
            if (!TryGetBrowseCeiling(out var ceiling))
            {
                return true;
            }

            if (scope == ZepSearchScope.CurrentFolder)
            {
                return true;
            }

            return scope == RootScopeFor(ceiling);
        }

        // User intent only — called from the public setter, which every scope
        // click goes through and which runs while ActiveMode is still the mode
        // the user is looking at. Mode switches deliberately do NOT snapshot the
        // live scope on the way out: it may be a projection-reconciled fallback
        // (see ReconcileSelectedSearchScope), and capturing that is how a
        // CurrentFolder preference used to die on a trip through the root.
        private void RememberSearchScope(AssetsPaneMode mode, ZepSearchScope scope)
        {
            if (!Source.IsPrimary)
            {
                return;
            }

            switch (mode)
            {
                case AssetsPaneMode.Browser:
                    _primaryBrowserSearchScope = scope;
                    break;
                case AssetsPaneMode.Tags:
                    _primaryTagSearchScope = scope;
                    break;
            }
        }

        // The controller resolves source identity against authoritative folder/tag
        // state and supplies the resulting live folder set. This owner then commits
        // a rooted-folder move/fallback as one pane-scoped change, so root identity,
        // current navigation, selection, and history can never disagree mid-update.
        internal bool TryReconcileSource(
            ZepPaneSource reconciledSource,
            IEnumerable<AssetPath> liveFolderPaths)
        {
            if (Source.Kind != reconciledSource.Kind)
            {
                return false;
            }

            if (Source.Kind != ZepPaneSourceKind.Folder)
            {
                return Source == reconciledSource;
            }

            var liveFolders = new HashSet<AssetPath>();
            if (liveFolderPaths != null)
            {
                foreach (var liveFolderPath in liveFolderPaths)
                {
                    if (!liveFolderPath.IsEmpty)
                    {
                        liveFolders.Add(liveFolderPath);
                    }
                }
            }

            return TryReconcileSource(reconciledSource, liveFolders.Contains);
        }

        internal bool TryReconcileSource(
            ZepPaneSource reconciledSource,
            Func<AssetPath, bool> isLiveFolder)
        {
            if (Source.Kind != reconciledSource.Kind || isLiveFolder == null)
            {
                return false;
            }

            if (Source.Kind != ZepPaneSourceKind.Folder)
            {
                return Source == reconciledSource;
            }

            var oldRoot = Source.RootFolderPath;
            var newRoot = reconciledSource.RootFolderPath;
            if (!isLiveFolder(newRoot))
            {
                return false;
            }

            ReconcileRootedBrowse(reconciledSource, oldRoot, newRoot, isLiveFolder);
            return true;
        }

        // The tag pane's opened folder is path-keyed like a rooted pane's root,
        // so a move rewrites it and a deletion has to be answered — but the
        // answer is "back to the tag list", never "close the pane": the tag,
        // not the folder, is what the pane was opened for. The controller
        // supplies the root rewritten through its move mapping.
        internal void ReconcileTagBrowseRoot(
            AssetPath rewrittenRoot,
            Func<AssetPath, bool> isLiveFolder)
        {
            if (Source.Kind != ZepPaneSourceKind.Tag
                || _tagBrowseRootPath.IsEmpty
                || isLiveFolder == null)
            {
                return;
            }

            if (rewrittenRoot.IsEmpty || !isLiveFolder(rewrittenRoot))
            {
                LeaveTagBrowseRoot();
                return;
            }

            ReconcileRootedBrowse(Source, _tagBrowseRootPath, rewrittenRoot, isLiveFolder);
        }

        // Rewrites everything path-keyed under oldRoot to newRoot in one
        // commit; newRoot is known live. Shared by a rooted pane's move and a
        // tag pane's opened-folder move, which differ only in what owns the
        // root (Source vs _tagBrowseRootPath).
        private void ReconcileRootedBrowse(
            ZepPaneSource reconciledSource,
            AssetPath oldRoot,
            AssetPath newRoot,
            Func<AssetPath, bool> isLiveFolder)
        {
            // Never empty: the walk ends at newRoot, which the caller proved live.
            var currentFolder = FindNearestLiveFolder(
                RewriteRootedPath(_currentFolderPath, oldRoot, newRoot),
                newRoot,
                isLiveFolder);

            var selectedFolder = RewriteRootedPath(
                _selectedFolderPath,
                oldRoot,
                newRoot);
            if (selectedFolder.IsEmpty
                || !isLiveFolder(selectedFolder)
                || !IsSameOrDescendant(selectedFolder.Value, newRoot.Value))
            {
                selectedFolder = currentFolder;
            }

            var selectedAssetPaths = new List<AssetPath>(_selectedAssetPaths.Count);
            for (var i = 0; i < _selectedAssetPaths.Count; i++)
            {
                var rewritten = RewriteRootedPath(
                    _selectedAssetPaths[i],
                    oldRoot,
                    newRoot);
                if (!rewritten.IsEmpty
                    && IsSameOrDescendant(rewritten.Value, newRoot.Value))
                {
                    selectedAssetPaths.Add(rewritten);
                }
            }

            var lastSelectedAsset = RewriteRootedPath(
                _lastSelectedAssetPath,
                oldRoot,
                newRoot);
            if (!lastSelectedAsset.IsEmpty
                && !IsSameOrDescendant(lastSelectedAsset.Value, newRoot.Value))
            {
                lastSelectedAsset = default;
            }

            // Path-keyed like everything above it, and the search the user is
            // still typing outlives the move. Left at its pre-move path the pick
            // sits outside the new root, IsSearchResultAllowed rejects it, and
            // the exit reveal silently stops working.
            var pickedSearchResult = RewriteRootedPath(
                _pickedSearchResult,
                oldRoot,
                newRoot);
            if (!pickedSearchResult.IsEmpty
                && !IsSameOrDescendant(pickedSearchResult.Value, newRoot.Value))
            {
                pickedSearchResult = default;
            }

            var history = new List<NavigationEntry>(_history.Count);
            for (var i = 0; i < _history.Count; i++)
            {
                history.Add(ReconcileRootedHistoryEntry(
                    _history[i],
                    oldRoot,
                    newRoot,
                    isLiveFolder));
            }

            var lastKnownEntry = ReconcileRootedHistoryEntry(
                _lastKnownEntry,
                oldRoot,
                newRoot,
                isLiveFolder);

            // Inline expansion is keyed by path like everything above it, so it
            // has to move with the root in the same commit — otherwise the pane
            // reopens its folders collapsed, and a folder later recreated at an
            // old path silently inherits the stale expansion.
            var expandedFolders = new HashSet<AssetPath>();
            foreach (var expandedFolder in _expandedAssetFolders)
            {
                var rewritten = RewriteRootedPath(expandedFolder, oldRoot, newRoot);
                if (!rewritten.IsEmpty
                    && IsSameOrDescendant(rewritten.Value, newRoot.Value)
                    && isLiveFolder(rewritten))
                {
                    expandedFolders.Add(rewritten);
                }
            }

            var selectedScope = _selectedSearchScope;
            if (selectedScope != ZepSearchScope.CurrentFolder)
            {
                selectedScope = AssetPathClassification.IsPackagesPath(newRoot)
                    ? ZepSearchScope.Packages
                    : ZepSearchScope.Assets;
            }

            var primarySelection = selectedAssetPaths.Count > 0
                ? selectedAssetPaths[0]
                : default;
            var ownsRootInTagBrowse = Source.Kind == ZepPaneSourceKind.Tag;
            var update = ZepPaneUpdate.None;
            if (Source != reconciledSource
                || (ownsRootInTagBrowse && _tagBrowseRootPath != newRoot)
                || _currentFolderPath != currentFolder
                || _selectedFolderPath != selectedFolder)
            {
                update |= ZepPaneUpdate.Navigation;
            }

            if (_selectedSearchScope != selectedScope)
            {
                update |= ZepPaneUpdate.Filter;
            }

            // The picked search result is listed here so a move that touches
            // only it still reaches the commit below — update == None returns
            // early, which would leave it at its pre-move path.
            if (_selectedAssetPath != primarySelection
                || _lastSelectedAssetPath != lastSelectedAsset
                || _pickedSearchResult != pickedSearchResult
                || !SequenceEquals(_selectedAssetPaths, selectedAssetPaths))
            {
                update |= ZepPaneUpdate.Selection;
            }

            if (!_lastKnownEntry.Equals(lastKnownEntry)
                || !SequenceEquals(_history, history))
            {
                update |= ZepPaneUpdate.History;
            }

            if (!_expandedAssetFolders.SetEquals(expandedFolders))
            {
                update |= ZepPaneUpdate.Expansion;
            }

            if (update == ZepPaneUpdate.None)
            {
                return;
            }

            Source = reconciledSource;
            if (ownsRootInTagBrowse)
            {
                _tagBrowseRootPath = newRoot;
            }

            _currentFolderPath = currentFolder;
            _selectedFolderPath = selectedFolder;
            _selectedSearchScope = selectedScope;
            _selectedAssetPath = primarySelection;
            _lastSelectedAssetPath = lastSelectedAsset;
            _pickedSearchResult = pickedSearchResult;
            ReplaceContents(_selectedAssetPaths, selectedAssetPaths);
            _selectionRevision++;
            ReplaceContents(_history, history);
            _lastKnownEntry = lastKnownEntry;
            _expandedAssetFolders.Clear();
            _expandedAssetFolders.UnionWith(expandedFolders);
            RaiseChanged(update);
        }

        // A rename/move rewrote a path the user had inline-expanded. Expansion
        // is keyed by path, so without this sweep the folder reopens collapsed
        // under its new name and — worse — the stale key silently pre-expands
        // whatever is later created at the old path. Callers must exclude
        // creates (whose "previous path" is the creation PARENT) and results
        // that left the project, or the rewrite would drag unrelated folders
        // along; see ZepController.HandleOperationResult.
        internal void RewriteExpandedAssetFolders(AssetPath previousPath, AssetPath newPath)
        {
            if (previousPath.IsEmpty
                || newPath.IsEmpty
                || _expandedAssetFolders.Count == 0)
            {
                return;
            }

            var rewritten = new HashSet<AssetPath>();
            var changed = false;
            foreach (var expandedFolder in _expandedAssetFolders)
            {
                var moved = RewriteRootedPath(expandedFolder, previousPath, newPath);
                changed |= moved != expandedFolder;
                rewritten.Add(moved);
            }

            if (!changed)
            {
                return;
            }

            _expandedAssetFolders.Clear();
            _expandedAssetFolders.UnionWith(rewritten);
            RaiseChanged(ZepPaneUpdate.Expansion);
        }

        // Callers read this several times per interaction (the shell reads it
        // twice in one block, the context menu once per action it offers), and
        // each read scanned the whole visible list and allocated a set. The
        // answer only moves when the selection, the projection, the provider,
        // or the list the provider hands back moves — so it is memoised on
        // exactly those four. The returned list is shared: callers read it,
        // they must not mutate it.
        public List<ZepAssetEntry> GetSelectedAssetEntries()
        {
            var assets = _visibleAssetsProvider != null
                ? _visibleAssetsProvider()
                : EmptyAssets;
            if (assets == null)
            {
                assets = EmptyAssets;
            }

            if (_selectedEntriesCache != null
                && _selectedEntriesRevision == _selectionRevision
                && ReferenceEquals(_selectedEntriesSource, assets)
                && _selectedEntriesSourceCount == assets.Count
                && ReferenceEquals(_selectedEntriesProjection, _projectedAssets)
                && ReferenceEquals(_selectedEntriesProvider, _visibleAssetsProvider))
            {
                return _selectedEntriesCache;
            }

            var selectedAssets = new List<ZepAssetEntry>();
            _selectedEntriesCache = selectedAssets;
            _selectedEntriesRevision = _selectionRevision;
            _selectedEntriesSource = assets;
            _selectedEntriesSourceCount = assets.Count;
            _selectedEntriesProjection = _projectedAssets;
            _selectedEntriesProvider = _visibleAssetsProvider;
            if (_selectedAssetPaths.Count == 0)
            {
                return selectedAssets;
            }

            if (assets is Application.IAssetPathLookup)
            {
                // Selection-first: ask the order where each of the k selected
                // paths sits, then read those indices in visible order. Walking
                // the order instead touched every row, and in search mode the
                // order is the LAZY projection — one indexer hit per row
                // MATERIALIZED an entry, so a memo miss on a 30k-match search
                // built 30k entries to hand back the two the user selected
                // (audit N1). Same resolver, same OrdinalIgnoreCase identity and
                // same first-occurrence-wins on a duplicated path as the scan
                // below; the selection itself cannot repeat a path.
                var indices = new List<int>(_selectedAssetPaths.Count);
                for (var i = 0; i < _selectedAssetPaths.Count; i++)
                {
                    var index = Application.ZepPathHelpers.FindAssetPathIndex(
                        assets,
                        _selectedAssetPaths[i].Value);
                    if (index >= 0)
                    {
                        indices.Add(index);
                    }
                }

                indices.Sort();
                for (var i = 0; i < indices.Count; i++)
                {
                    var selectedAsset = assets[indices[i]];
                    if (selectedAsset != null)
                    {
                        selectedAssets.Add(selectedAsset);
                    }
                }

                return selectedAssets;
            }

            // Orders that index nothing (a plain list, the inline-create ghost
            // splice) keep the scan: pushing k paths through the resolver there
            // would be k full scans of the same list.
            var selectedSet = new HashSet<AssetPath>(_selectedAssetPaths);
            for (var i = 0; i < assets.Count; i++)
            {
                var selectedAsset = assets[i];
                if (selectedAsset == null
                    || !selectedSet.Contains(AssetPath.From(selectedAsset.Path)))
                {
                    continue;
                }

                selectedAssets.Add(selectedAsset);
            }

            return selectedAssets;
        }

        public string ResolveCreateTargetFolderPath(
            Func<ZepAssetEntry, bool> isFolderAssetEntry)
        {
            var selectedAssets = GetSelectedAssetEntries();
            if (selectedAssets.Count == 1
                && isFolderAssetEntry(selectedAssets[0])
                && !string.IsNullOrWhiteSpace(selectedAssets[0].Path))
            {
                return selectedAssets[0].Path;
            }

            string selectedFolderPath = _selectedFolderPath;
            if (!string.IsNullOrWhiteSpace(selectedFolderPath))
            {
                return selectedFolderPath;
            }

            string currentFolderPath = _currentFolderPath;
            if (string.IsNullOrWhiteSpace(currentFolderPath))
            {
                return "Assets";
            }

            return currentFolderPath;
        }

        // Background menus act on the browsed folder when Unity can resolve it.
        public string ResolveBackgroundContextFolderPath(Func<string, bool> isIndexedFolder)
        {
            if (ActiveMode != AssetsPaneMode.Browser)
            {
                return null;
            }

            string currentFolderPath = _currentFolderPath;
            if (string.IsNullOrWhiteSpace(currentFolderPath)
                || (AssetPathClassification.IsProjectRootPath(currentFolderPath)
                    && !AssetPathClassification.IsWritablePath(currentFolderPath)))
            {
                return null;
            }

            return isIndexedFolder(currentFolderPath) ? currentFolderPath : null;
        }

        public List<string> BuildContextMenuSelection(string clickedAssetPath)
        {
            var selectedAssets = GetSelectedAssetEntries();
            var selectedPaths = new List<string>(selectedAssets.Count);
            for (var i = 0; i < selectedAssets.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(selectedAssets[i].Path))
                {
                    selectedPaths.Add(selectedAssets[i].Path);
                }
            }

            if (AssetPathClassification.ContainsAssetPath(selectedPaths, clickedAssetPath))
            {
                return selectedPaths;
            }

            selectedPaths.Clear();
            if (!string.IsNullOrWhiteSpace(clickedAssetPath))
            {
                selectedPaths.Add(clickedAssetPath);
            }

            return selectedPaths;
        }

        public IReadOnlyDictionary<string, ICollection<string>> BuildAssignedTagMap()
        {
            var map = new Dictionary<string, ICollection<string>>(
                StringComparer.OrdinalIgnoreCase);
            var selectedAssets = GetSelectedAssetEntries();
            for (var i = 0; i < selectedAssets.Count; i++)
            {
                var asset = selectedAssets[i];
                if (asset == null || string.IsNullOrWhiteSpace(asset.Path))
                {
                    continue;
                }

                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var t = 0; t < asset.Tags.Count; t++)
                {
                    if (!string.IsNullOrWhiteSpace(asset.Tags[t].Id))
                    {
                        ids.Add(asset.Tags[t].Id);
                    }
                }

                map[asset.Path] = ids;
            }

            string selectedFolderPath = _selectedFolderPath;
            if (!string.IsNullOrWhiteSpace(selectedFolderPath)
                && !map.ContainsKey(selectedFolderPath))
            {
                map[selectedFolderPath] = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
            }

            return map;
        }

        private bool CanNavigateTo(AssetPath folderPath)
        {
            switch (Source.Kind)
            {
                case ZepPaneSourceKind.Primary:
                    return true;
                case ZepPaneSourceKind.Folder:
                    return IsSameOrDescendant(
                        folderPath.Value,
                        Source.RootFolderPath.Value);
                case ZepPaneSourceKind.Tag:
                    return _tagBrowseRootPath.IsEmpty
                        ? folderPath == _currentFolderPath
                        : IsSameOrDescendant(
                            folderPath.Value,
                            _tagBrowseRootPath.Value);
                default:
                    return false;
            }
        }

        private bool CanSelectFolder(AssetPath folderPath)
        {
            if (Source.IsPrimary)
            {
                return true;
            }

            if (TryGetBrowseCeiling(out _))
            {
                return folderPath.IsEmpty || IsPathWithinSource(folderPath);
            }

            return folderPath.IsEmpty;
        }

        // The folder an auxiliary pane cannot climb above: a rooted pane's
        // root, or the folder a tag pane opened from its list. Absent for the
        // primary pane and for a tag pane still showing its list.
        private bool TryGetBrowseCeiling(out AssetPath ceiling)
        {
            switch (Source.Kind)
            {
                case ZepPaneSourceKind.Folder:
                    ceiling = Source.RootFolderPath;
                    return true;
                case ZepPaneSourceKind.Tag when !_tagBrowseRootPath.IsEmpty:
                    ceiling = _tagBrowseRootPath;
                    return true;
                default:
                    ceiling = default;
                    return false;
            }
        }

        private static ZepSearchScope RootScopeFor(AssetPath root)
        {
            return AssetPathClassification.IsPackagesPath(root)
                ? ZepSearchScope.Packages
                : ZepSearchScope.Assets;
        }

        // Opens `folder` in a tag pane under `ceiling` (the folder itself on a
        // click from the list; the remembered ceiling on history re-entry).
        // Refused unless the pane is showing its list, the ceiling is a real
        // Assets/Packages folder, and the folder sits under it.
        public bool TryEnterTagFolder(AssetPath ceiling, AssetPath folder)
        {
            if (Source.Kind != ZepPaneSourceKind.Tag
                || !_tagBrowseRootPath.IsEmpty
                || !AssetPathClassification.IsProjectAssetPath(ceiling)
                || !IsSameOrDescendant(folder.Value, ceiling.Value))
            {
                return false;
            }

            using (DeferNotifications())
            {
                _tagBrowseRootPath = ceiling;
                SetSearchText(string.Empty);
                TrySetCurrentFolderPath(folder);
                TrySetSelectedFolderPath(folder);
                // Scope "All" is not offered under a ceiling; the ceiling's own
                // scope is, and CurrentFolder still is (see IsSearchScopeAllowed).
                SetSelectedSearchScope(RootScopeFor(ceiling));
                SetSelectedAssetPaths(Array.Empty<AssetPath>());
                // The mode and the (now absent) tag filter changed with the root.
                RaiseChanged(ZepPaneUpdate.Navigation | ZepPaneUpdate.Filter);
            }

            return true;
        }

        // Back to the tag list. Every path-keyed piece of the opened folder is
        // dropped so the list projects clean; the history entries keep the
        // ceiling they were recorded under for re-entry.
        private void LeaveTagBrowseRoot()
        {
            if (_tagBrowseRootPath.IsEmpty)
            {
                return;
            }

            using (DeferNotifications())
            {
                _tagBrowseRootPath = default;
                _currentFolderPath = AssetPath.From("Assets");
                _selectedFolderPath = default;
                SetSearchText(string.Empty);
                SetSelectedSearchScope(ZepSearchScope.All);
                SetSelectedAssetPaths(Array.Empty<AssetPath>());
                RaiseChanged(ZepPaneUpdate.Navigation | ZepPaneUpdate.Filter);
            }
        }

        private void NavigateHistory(int direction)
        {
            if (_history.Count == 0 || direction == 0)
            {
                return;
            }

            var targetIndex = _historyIndex + direction;
            if (targetIndex < 0 || targetIndex >= _history.Count)
            {
                return;
            }

            var target = _history[targetIndex];
            _historyIndex = targetIndex;
            _isHistoryNavigation = true;
            try
            {
                ApplyEntry(target);
            }
            finally
            {
                _isHistoryNavigation = false;
            }

            var landed = ResolveCurrentNavigationEntry();
            if (target.IsFolder
                && !landed.IsEmpty
                && !landed.Equals(target)
                && _historyIndex >= 0
                && _historyIndex < _history.Count)
            {
                _history[_historyIndex] = landed;
                _lastKnownEntry = landed;
                RaiseChanged(ZepPaneUpdate.History);
                return;
            }

            _lastKnownEntry = landed.IsEmpty ? target : landed;
            RaiseChanged(ZepPaneUpdate.History);
        }

        private NavigationEntry ResolveCurrentNavigationEntry()
        {
            // A tag pane records the tag while it shows the list and the folder
            // once it has opened one, so Back from the folder lands on the tag.
            if (!string.IsNullOrWhiteSpace(SelectedTagFilterId))
            {
                return NavigationEntry.Tag(SelectedTagFilterId);
            }

            var folderPath = _currentFolderPath.Value;
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                folderPath = "Assets";
            }

            return NavigationEntry.Folder(
                folderPath,
                Source.Kind == ZepPaneSourceKind.Tag
                    ? _tagBrowseRootPath.Value
                    : null);
        }

        private void RaiseChanged(
            ZepPaneUpdate update,
            bool notifyNavButtons = true)
        {
            if (_notificationDeferralDepth > 0)
            {
                _deferredUpdate |= update;
                _hasDeferredNavButtonsChange |= notifyNavButtons;
                return;
            }

            Changed?.Invoke(this, update);
            if (notifyNavButtons)
            {
                FireNavButtons();
            }
        }

        private void FireNavButtons()
        {
            NavButtonsStateChanged?.Invoke(CanNavigateBack, CanNavigateForward);
        }

        private void EndNotificationDeferral()
        {
            if (_notificationDeferralDepth == 0)
            {
                return;
            }

            _notificationDeferralDepth--;
            if (_notificationDeferralDepth != 0
                || _deferredUpdate == ZepPaneUpdate.None)
            {
                return;
            }

            var update = _deferredUpdate;
            var notifyNavButtons = _hasDeferredNavButtonsChange;
            _deferredUpdate = ZepPaneUpdate.None;
            _hasDeferredNavButtonsChange = false;
            Changed?.Invoke(this, update);
            if (notifyNavButtons)
            {
                FireNavButtons();
            }
        }

        private static bool IsSameOrDescendant(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return path.Length > root.Length
                && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && path[root.Length] == '/';
        }

        private static AssetPath GetParentFolder(AssetPath path)
        {
            return AssetPath.From(GetParentFolderPath(path.Value));
        }

        private static AssetPath RewriteRootedPath(
            AssetPath path,
            AssetPath oldRoot,
            AssetPath newRoot)
        {
            if (path.IsEmpty)
            {
                return default;
            }

            if (!IsSameOrDescendant(path.Value, oldRoot.Value))
            {
                return path;
            }

            return AssetPath.From(newRoot.Value + path.Value.Substring(oldRoot.Value.Length));
        }

        private static AssetPath FindNearestLiveFolder(
            AssetPath candidate,
            AssetPath root,
            Func<AssetPath, bool> isLiveFolder)
        {
            if (candidate.IsEmpty
                || !IsSameOrDescendant(candidate.Value, root.Value))
            {
                candidate = root;
            }

            while (!candidate.IsEmpty
                && IsSameOrDescendant(candidate.Value, root.Value))
            {
                if (isLiveFolder(candidate))
                {
                    return candidate;
                }

                if (candidate == root)
                {
                    break;
                }

                candidate = GetParentFolder(candidate);
            }

            return default;
        }

        private static NavigationEntry ReconcileRootedHistoryEntry(
            NavigationEntry entry,
            AssetPath oldRoot,
            AssetPath newRoot,
            Func<AssetPath, bool> isLiveFolder)
        {
            if (entry.IsEmpty)
            {
                return NavigationEntry.None;
            }

            // A tag pane's list entry is not path-keyed; a rooted folder pane
            // never records one.
            if (!entry.IsFolder)
            {
                return entry;
            }

            var rewritten = RewriteRootedPath(
                AssetPath.From(entry.FolderPath),
                oldRoot,
                newRoot);
            var liveFolder = FindNearestLiveFolder(rewritten, newRoot, isLiveFolder);
            var folder = liveFolder.IsEmpty ? newRoot : liveFolder;
            if (entry.BrowseRootPath == null)
            {
                return NavigationEntry.Folder(folder.Value);
            }

            // A tag pane's ceiling moves with the folder; when the walk above
            // pulled the folder out from under it, the folder is its own ceiling.
            var ceiling = RewriteRootedPath(
                AssetPath.From(entry.BrowseRootPath),
                oldRoot,
                newRoot);
            if (ceiling.IsEmpty || !IsSameOrDescendant(folder.Value, ceiling.Value))
            {
                ceiling = folder;
            }

            return NavigationEntry.Folder(folder.Value, ceiling.Value);
        }

        private static string GetParentFolderPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return string.Empty;
            }

            var slash = assetPath.LastIndexOf('/');
            return slash <= 0 ? string.Empty : assetPath.Substring(0, slash);
        }

        private List<AssetPath> NormalizeSelectedAssetPaths(
            IEnumerable<AssetPath> selectedAssetPaths)
        {
            var normalized = new List<AssetPath>();
            if (selectedAssetPaths == null)
            {
                return normalized;
            }

            var seen = new HashSet<AssetPath>();
            foreach (var assetPath in selectedAssetPaths)
            {
                if (assetPath.IsEmpty
                    || !IsSearchResultAllowed(assetPath)
                    || !seen.Add(assetPath))
                {
                    continue;
                }

                normalized.Add(assetPath);
            }

            return normalized;
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
            private ZepPaneState _owner;

            public NotificationDeferral(ZepPaneState owner)
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
