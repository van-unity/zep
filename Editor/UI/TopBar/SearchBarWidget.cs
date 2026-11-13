using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using Zep.Editor.UI.AssetsArea;
using UnityEditor;
using Zep.Core.Application.Ports;
using Zep.Core.Domain;
using Zep.Core.Search;
using Zep.Editor.Bridge;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.TopBar
{
    // Owns the dialog query and keyboard selection independently of browsing.
    internal sealed class SearchBarWidget : VisualElement
    {
        internal static string HistoryPreferenceKey => "Zep.RecentSearchQueries." + Application.dataPath;
        // Below NarrowWidth the six named type chips no longer share one row.
        internal const float NarrowWidth = 520f;
        internal const float ShortHeight = 440f;
        private readonly ToolbarSearchField _searchField;
        private readonly TextField _searchTextInput;
        private readonly VisualElement _card;
        private readonly ScrollView _suggestionSurface;
        private readonly ListView _resultList;
        private readonly UnityZepIconRepository _icons;
        private IReadOnlyList<ZepAssetEntry> _results = Array.Empty<ZepAssetEntry>();
        private readonly HashSet<string> _approximatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly VisualElement _filters;
        private readonly VisualElement _activeFilters;
        private readonly VisualElement _typePicker;
        private readonly VisualElement _typeBackdrop;
        private readonly Button _moreTypes;
        private readonly Label _typeSelectionSummary;
        private readonly Button _clearTypes;
        private int _typePickerAnimationVersion;
        private readonly TextField _typeSearch;
        private readonly ScrollView _typeChoices;
        private readonly VisualElement _recovery;
        private readonly VisualElement _historySurface;
        private readonly Button _broadenButton;
        private readonly Button _clearFiltersButton;
        private readonly Label _context;
        private readonly string _historyKey;
        private readonly List<string> _recentQueries = new List<string>();
        private IReadOnlyList<ZepTypeFilterOption> _typeOptions = Array.Empty<ZepTypeFilterOption>();
        private bool _typePickerVisible;
        private bool _indexing;
        private string _scopeLabel = "Assets";
        private string _inheritedTagLabel;
        private string _lastResultQuery;
        private string _historyError;

        [Serializable]
        private sealed class RecentQueries
        {
            public List<string> queries = new List<string>();
        }

        internal string InheritedType { get; private set; } = string.Empty;
        internal string InheritedTag { get; private set; } = string.Empty;
        internal int ResultCount => _results.Count;
        internal ZepAssetEntry SelectedResult => _selectedSuggestionIndex >= 0 && _selectedSuggestionIndex < _results.Count
            ? _results[_selectedSuggestionIndex] : null;
        internal event Action SelectedResultChanged;
        internal event Action QuickLookRequested;
        internal event Func<bool> QuickLookDismissRequested;
        internal event Action FiltersChanged;
        internal event Action BroadenRequested;
        private readonly Label _emptyState;
        private readonly Label _resultHeading;
        private readonly List<SuggestionRow> _suggestionRows = new List<SuggestionRow>();
        private readonly IDeferredCallScheduler _scheduler;
        private readonly Action _raisePendingSearchText;
        private readonly ModalFade _fade = new ModalFade();
        private VisualElement _returnFocus;
        private string _pendingSearchText;
        private bool _isSearchTextRaiseQueued;
        private int _selectedSuggestionIndex = -1;

        internal bool IsVisible { get; private set; }
        internal bool HasPendingQuery => _isSearchTextRaiseQueued;
        internal string SearchText => _searchField.value ?? string.Empty;
        internal event Action<string> SearchTextChanged;
        internal event Action<string, bool> OpenRequested;
        internal event Action Closed;
        internal VisualElement SuggestionSurface => _suggestionSurface;

        internal SearchBarWidget(IDeferredCallScheduler scheduler = null, UnityZepIconRepository icons = null, string historyKey = null)
        {
            _scheduler = scheduler ?? new UnityDeferredCallScheduler();
            _icons = icons;
            _historyKey = historyKey;
            LoadHistory();
            _raisePendingSearchText = RaisePendingSearchText;
            name = "zep-search-overlay";
            AddToClassList(ZepUiClasses.ModalScrim);
            AddToClassList(ZepUiClasses.SearchOverlay);
            style.display = DisplayStyle.None;
            // Width and height degrade separately: a short-but-wide window keeps
            // its type chips, a narrow-but-tall one keeps its context line.
            RegisterCallback<GeometryChangedEvent>(evt =>
            {
                EnableInClassList(ZepUiClasses.SearchOverlayNarrow, evt.newRect.width < NarrowWidth);
                EnableInClassList(ZepUiClasses.SearchOverlayShort, evt.newRect.height < ShortHeight);
            });
            RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == this && evt.button == 0) Hide();
                evt.StopPropagation();
            });
            RegisterCallback<KeyDownEvent>(OnSearchFieldKeyDown, TrickleDown.TrickleDown);
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                CancelPendingSearchTextRaise();
                IsVisible = false;
                SetTypePickerVisible(false);
                ++_typePickerAnimationVersion;
                _typeBackdrop.style.display = DisplayStyle.None;
                _returnFocus = null;
                if (_icons != null) _icons.PreviewsReady -= OnPreviewsReady;
                _fade.Hide(this, _card);
                style.display = DisplayStyle.None;
            });

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                if (_icons != null) _icons.PreviewsReady += OnPreviewsReady;
            });

            _card = new VisualElement { name = "zep-search-card" };
            _card.AddToClassList(ZepUiClasses.SearchCard);
            var heading = new VisualElement();
            heading.AddToClassList(ZepUiClasses.SearchHeading);
            var title = new Label("Find an asset");
            title.AddToClassList(ZepUiClasses.SearchTitle);
            heading.Add(title);
            _card.Add(heading);

            _searchField = new ToolbarSearchField
            {
                name = "zep-search",
                value = string.Empty,
                tooltip = "Search assets (t:Prefab, l:Label, tag:Tag, path:Folder)"
            };
            _searchField.AddToClassList(ZepUiClasses.SearchField);
            _searchField.RegisterValueChangedCallback(OnSearchFieldChanged);
            _searchTextInput = _searchField.Q<TextField>();
            _searchTextInput.textEdition.placeholder = "Search by name, type, or path…";
            _card.Add(_searchField);

            _context = new Label();
            _context.AddToClassList(ZepUiClasses.SearchContext);
            _card.Add(_context);
            _filters = new VisualElement { name = "zep-search-quick-types" };
            _filters.AddToClassList(ZepUiClasses.SearchFilters);
            foreach (var item in new[] { ("", "All types"), ("Prefab", "Prefabs"), ("Material", "Materials"),
                         ("Texture", "Textures"), ("Folder", "Folders"), ("Script", "Scripts") })
            {
                var type = item.Item1;
                var button = MakeButton(item.Item2, () => ToggleType(type));
                button.userData = type;
                button.name = "zep-search-type-" + (type.Length == 0 ? "all" : type.ToLowerInvariant());
                _filters.Add(button);
            }
            _moreTypes = MakeButton("Types…", () => SetTypePickerVisible(!_typePickerVisible));
            _moreTypes.name = "zep-search-more-types";
            _moreTypes.tooltip = "Choose asset types";
            _filters.Add(_moreTypes);
            _card.Add(_filters);
            var activeFilters = new ScrollView(ScrollViewMode.Vertical);
            activeFilters.contentContainer.AddToClassList(ZepUiClasses.SearchFilters);
            activeFilters.AddToClassList(ZepUiClasses.SearchActiveFilters);
            _activeFilters = activeFilters;
            _card.Add(_activeFilters);
            _typeBackdrop = new VisualElement();
            _typeBackdrop.AddToClassList(ZepUiClasses.SearchTypeBackdrop);
            _typeBackdrop.style.display = DisplayStyle.None;
            _typeBackdrop.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _typeBackdrop && evt.button == 0)
                {
                    focusController?.IgnoreEvent(evt);
                    SetTypePickerVisible(false);
                }
                evt.StopPropagation();
            });
            _typePicker = new VisualElement();
            _typePicker.AddToClassList(ZepUiClasses.SearchTypePicker);
            var typeHeader = new VisualElement();
            typeHeader.AddToClassList(ZepUiClasses.SearchHeading);
            var typeTitle = new Label("Asset types");
            typeTitle.AddToClassList(ZepUiClasses.SearchTitle);
            typeHeader.Add(typeTitle);
            var typeClose = MakeButton("✕", () => SetTypePickerVisible(false));
            typeClose.name = "zep-search-types-close";
            typeClose.tooltip = "Close types (Esc)";
            typeHeader.Add(typeClose);
            _typePicker.Add(typeHeader);
            var typeDescription = new Label("Match any of the selected types.");
            typeDescription.AddToClassList(ZepUiClasses.SearchTypeDescription);
            _typePicker.Add(typeDescription);
            _typeSearch = new TextField { name = "zep-search-types-query" };
            _typeSearch.textEdition.placeholder = "Find a type…";
            _typeSearch.AddToClassList(ZepUiClasses.PromptField);
            _typeSearch.RegisterValueChangedCallback(_ =>
            {
                RefreshTypeChoices();
                _typeChoices.scrollOffset = Vector2.zero;
            });
            _typePicker.Add(_typeSearch);
            _typeChoices = new ScrollView(ScrollViewMode.Vertical);
            _typeChoices.AddToClassList(ZepUiClasses.SearchTypeChoices);
            _typeChoices.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _typePicker.Add(_typeChoices);
            var typeFooter = new VisualElement();
            typeFooter.AddToClassList(ZepUiClasses.SearchTypeFooter);
            _typeSelectionSummary = new Label();
            _typeSelectionSummary.AddToClassList(ZepUiClasses.SearchTypeSummary);
            typeFooter.Add(_typeSelectionSummary);
            _clearTypes = MakeButton("Clear", () => ToggleType(string.Empty));
            _clearTypes.name = "zep-search-types-clear";
            _clearTypes.tooltip = "Clear type filters";
            typeFooter.Add(_clearTypes);
            var typeDone = MakeButton("Done", () => SetTypePickerVisible(false));
            typeDone.name = "zep-search-types-done";
            typeDone.AddToClassList(ZepUiClasses.SearchFilterSelected);
            typeFooter.Add(typeDone);
            _typePicker.Add(typeFooter);
            _typeBackdrop.Add(_typePicker);
            _card.Add(_typeBackdrop);
            _historySurface = new VisualElement();
            _historySurface.AddToClassList(ZepUiClasses.SearchHistory);
            _card.Add(_historySurface);

            _resultHeading = new Label("Your project");
            _resultHeading.AddToClassList(ZepUiClasses.SearchResultHeading);
            _card.Add(_resultHeading);
            var matches = new VisualElement();
            matches.AddToClassList(ZepUiClasses.SearchMatches);
            _card.Add(matches);
            _emptyState = new Label("Start with a name. Narrow it with t:Prefab or path:Folder.");
            _emptyState.AddToClassList(ZepUiClasses.SearchEmpty);
            matches.Add(_emptyState);
            _recovery = new VisualElement();
            _recovery.AddToClassList(ZepUiClasses.SearchFilters);
            _clearFiltersButton = MakeButton("Clear filters", ClearFilters);
            _recovery.Add(_clearFiltersButton);
            _broadenButton = MakeButton("Search entire project", () => BroadenRequested?.Invoke());
            _recovery.Add(_broadenButton);
            matches.Add(_recovery);
            _resultList = new ListView
            {
                name = "zep-search-results",
                fixedItemHeight = 56,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.None,
                focusable = true,
                makeItem = () =>
                {
                    var row = new SuggestionRow(this);
                    _suggestionRows.Add(row);
                    // Keep our row geometry separate from the native collection
                    // item, which Unity resizes and recycles as the viewport changes.
                    var item = new VisualElement { userData = row };
                    item.Add(row.Root);
                    return item;
                },
                bindItem = (element, i) => ((SuggestionRow)element.userData).Bind(i, _results[i]),
                unbindItem = (element, _) => ((SuggestionRow)element.userData).Clear(),
                destroyItem = element => _suggestionRows.Remove((SuggestionRow)element.userData)
            };
            _resultList.AddToClassList(ZepUiClasses.SearchResults);
            // Keep keyboard selection visible when the window changes
            // the viewport. ListView owns resizing its virtualized row pool.
            IVisualElementScheduledItem scrollAfterLayout = null;
            _resultList.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                scrollAfterLayout?.Pause();
                scrollAfterLayout = _resultList.schedule.Execute(() =>
                {
                    var selectedTop = _selectedSuggestionIndex * _resultList.fixedItemHeight;
                    var viewportTop = _suggestionSurface.scrollOffset.y;
                    var viewportHeight = _suggestionSurface.contentViewport.layout.height;
                    if (_selectedSuggestionIndex >= 0 && (selectedTop < viewportTop
                        || selectedTop + _resultList.fixedItemHeight > viewportTop + viewportHeight))
                    {
                        // Realize the selected item before aligning its full bounds;
                        // older Unity versions defer recycling for scrollOffset alone.
                        _resultList.ScrollToItem(_selectedSuggestionIndex);
                        _suggestionSurface.scrollOffset = new Vector2(0f, Mathf.Max(0f,
                            selectedTop < viewportTop ? selectedTop
                                : selectedTop + _resultList.fixedItemHeight - viewportHeight));
                    }
                });
            });
            _suggestionSurface = _resultList.Q<ScrollView>();
            _suggestionSurface.name = "zep-search-suggestions";
            _suggestionSurface.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _suggestionSurface.AddToClassList(ZepUiClasses.SearchSuggestions);
            _suggestionSurface.RegisterCallback<PointerDownEvent>(evt =>
            {
                focusController?.IgnoreEvent(evt);
                evt.StopPropagation();
            });
            matches.Add(_resultList);
            var footer = new VisualElement();
            footer.AddToClassList(ZepUiClasses.SearchFooter);
            var hints = new Label("Double-click to select   ·   Space Quick Look");
            hints.AddToClassList(ZepUiClasses.SearchHints);
            footer.Add(hints);
            _card.Add(footer);
            Add(_card);
            RefreshFilters();
            SetResults(null);
        }

        internal void FocusSearch()
        {
            if (IsVisible)
            {
                _searchField.Focus();
                _searchTextInput.SelectAll();
                return;
            }
            _returnFocus = focusController?.focusedElement as VisualElement;
            IsVisible = true;
            _fade.Show(this, _card, () =>
            {
                if (!IsVisible || panel == null) return;
                _searchField.Focus();
                _searchTextInput.SelectAll();
            });
        }

        internal void FocusResults() => _resultList.Focus();

        internal void MoveResultSelection(int direction)
        {
            var count = VisibleSuggestionCount();
            if (_isSearchTextRaiseQueued || count == 0) return;
            SelectSuggestion((_selectedSuggestionIndex + direction + count) % count);
        }

        internal void Hide()
        {
            if (!IsVisible) return;
            // Finish the dialog's pending query before closing; it is kept for reopening.
            RaisePendingSearchText();
            IsVisible = false;
            SetTypePickerVisible(false);
            _fade.Hide(this, _card);
            var destination = _returnFocus;
            _returnFocus = null;
            if (destination?.panel == panel && destination != null
                && destination.enabledInHierarchy && destination.resolvedStyle.display != DisplayStyle.None)
                destination.Focus();
            else
                parent?.Focus();
            Closed?.Invoke();
        }

        internal void SetSearchText(string searchText)
        {
            if (_isSearchTextRaiseQueued) return;
            var value = searchText ?? string.Empty;
            if (_searchField.value == value) return;
            _searchField.SetValueWithoutNotify(value);
            RefreshFilters();
            HideSuggestions();
        }

        internal void SetSuggestions(IReadOnlyList<AssetSearchSuggestion> suggestions)
        {
            _approximatePaths.Clear();
            var entries = new List<ZepAssetEntry>();
            if (suggestions != null)
                foreach (var suggestion in suggestions)
                {
                    var asset = suggestion.Asset;
                    entries.Add(new ZepAssetEntry(asset.Name, asset.AssetPath, asset.TypeName));
                    if (suggestion.IsFuzzy) _approximatePaths.Add(asset.AssetPath);
                }
            ApplyResults(entries);
        }

        internal void SetResults(IReadOnlyList<ZepAssetEntry> assets)
        {
            _approximatePaths.Clear();
            ApplyResults(assets ?? Array.Empty<ZepAssetEntry>());
        }

        private void ApplyResults(IReadOnlyList<ZepAssetEntry> assets)
        {
            var previousPath = _lastResultQuery == SearchText ? SelectedResult?.Path : null;
            var previousScroll = _suggestionSurface.scrollOffset;
            var previousIndex = _selectedSuggestionIndex;
            var previousTop = previousIndex * _resultList.fixedItemHeight;
            var wasSelectionVisible = previousPath != null
                && previousTop + _resultList.fixedItemHeight > previousScroll.y
                && previousTop < previousScroll.y + _suggestionSurface.contentViewport.layout.height;
            _results = assets;
            _lastResultQuery = SearchText;
            _selectedSuggestionIndex = assets.Count > 0 ? 0 : -1;
            if (previousPath != null)
            {
                var index = assets is IAssetPathLookup lookup ? lookup.IndexOfPath(previousPath) : -1;
                if (!(assets is IAssetPathLookup))
                    for (var i = 0; i < assets.Count; i++)
                        if (string.Equals(assets[i].Path, previousPath, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                if (index >= 0) _selectedSuggestionIndex = index;
            }
            _resultList.itemsSource = assets as IList ?? assets.ToList();
            _resultList.RefreshItems();
            _suggestionSurface.EnableInClassList(ZepUiClasses.SearchSuggestionsVisible, assets.Count > 0);
            _resultList.style.display = assets.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _suggestionSurface.scrollOffset = previousPath != null ? previousScroll : Vector2.zero;
            // Catalog changes can move the selected path or replace it with the first
            // result. Follow only a selection the user was still looking at.
            if (wasSelectionVisible && previousIndex != _selectedSuggestionIndex && _selectedSuggestionIndex >= 0)
                _resultList.ScrollToItem(_selectedSuggestionIndex);
            _emptyState.style.display = assets.Count > 0 ? DisplayStyle.None : DisplayStyle.Flex;
            var hasQuery = !string.IsNullOrWhiteSpace(SearchText) || InheritedType.Length > 0 || InheritedTag.Length > 0;
            _emptyState.text = _indexing ? "Indexing project… Results will update as assets become available."
                : hasQuery ? "No matching assets in " + _scopeLabel + ". Try removing a filter or broadening the search."
                : "Find an asset by name, or choose a type to start browsing.";
            _resultHeading.text = assets.Count > 0
                ? $"{assets.Count:N0} match{(assets.Count == 1 ? string.Empty : "es")}" + (_approximatePaths.Count > 0 ? " · Similar names" : string.Empty)
                : "Your project";
            _recovery.style.display = hasQuery && assets.Count == 0 && !_indexing ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshHistory();
            RefreshSuggestionSelection();
        }

        internal void SetContext(string type, string tagId, string tagLabel, IReadOnlyList<ZepTypeFilterOption> types)
        {
            InheritedType = type ?? string.Empty;
            InheritedTag = tagId ?? string.Empty;
            _inheritedTagLabel = tagLabel ?? tagId;
            _typeOptions = types ?? Array.Empty<ZepTypeFilterOption>();
            RefreshFilters();
        }

        internal void SetScopeContext(string label, bool canBroaden, bool indexing,
            IReadOnlyList<ZepTypeFilterOption> types = null)
        {
            if (types != null && !ReferenceEquals(types, _typeOptions))
            {
                _typeOptions = types;
                if (_typePickerVisible) RefreshTypeChoices();
            }
            _scopeLabel = label;
            _indexing = indexing;
            _context.text = "Search in " + label + (indexing ? " · Indexing…" : string.Empty);
            _context.tooltip = label;
            _broadenButton.style.display = canBroaden ? DisplayStyle.Flex : DisplayStyle.None;
        }

        internal void SetFailure(string message)
        {
            SetResults(null);
            _historySurface.style.display = DisplayStyle.None;
            _recovery.style.display = DisplayStyle.None;
            _emptyState.text = message;
        }

        private static Button MakeButton(string text, Action action)
        {
            var button = new Button(action) { text = text, enableRichText = false };
            button.AddToClassList(ZepUiClasses.FlatButton);
            button.AddToClassList(ZepUiClasses.SearchControl);
            return button;
        }

        private void ToggleType(string type)
        {
            var clearInherited = type.Length > 0 && string.Equals(InheritedType, type, StringComparison.OrdinalIgnoreCase)
                && !SearchQuery.Parse(SearchText).TypeTerms.Any(t => string.Equals(t.Value, type, StringComparison.OrdinalIgnoreCase));
            InheritedType = string.Empty;
            _searchField.value = clearInherited ? SearchText : type.Length == 0 ? SearchQuery.RemoveFilters(SearchText, SearchField.Type)
                : SearchQuery.ToggleTypeFilter(SearchText, type);
            RefreshFilters();
            if (!_isSearchTextRaiseQueued) FiltersChanged?.Invoke();
            if (!_typePickerVisible) _searchField.Focus();
        }

        internal void ClearFilters()
        {
            InheritedType = InheritedTag = string.Empty;
            _searchField.value = SearchQuery.RemoveFilters(SearchText);
            RefreshFilters();
            if (!_isSearchTextRaiseQueued) FiltersChanged?.Invoke();
            _searchField.Focus();
        }

        private void RefreshFilters()
        {
            var parsed = SearchQuery.Parse(SearchText);
            foreach (var child in _filters.Children())
            {
                if (child.userData is not string type) continue;
                var selected = type.Length == 0 ? parsed.TypeTerms.Count == 0 && InheritedType.Length == 0
                    && !parsed.ExcludedTerms.Any(t => t.Field == SearchField.Type)
                    : parsed.TypeTerms.Any(t => string.Equals(t.Value, type, StringComparison.OrdinalIgnoreCase));
                child.EnableInClassList(ZepUiClasses.SearchFilterSelected, selected);
            }
            _activeFilters.Clear();
            foreach (var span in SearchQuery.FilterSpans(SearchText))
            {
                var query = SearchText;
                var token = query.Substring(span.Start, span.Length);
                var value = token.Substring(token.IndexOfAny(new[] { ':', '=' }) + 1).Trim('"');
                var label = span.Field == SearchField.Label ? "Unity label" : span.Field.ToString();
                if (token.StartsWith("-", StringComparison.Ordinal)) label = "Exclude " + label.ToLowerInvariant();
                var chip = MakeButton(label + ": " + value + " ×", () =>
                {
                    _searchField.value = query.Remove(span.Start, span.Length).Trim();
                    _searchField.Focus();
                });
                chip.tooltip = "Remove " + token;
                _activeFilters.Add(chip);
            }
            if (InheritedType.Length > 0)
                _activeFilters.Add(MakeButton("Browser type: " + AssetEntryRenderer.ResolveTypeDisplayName(InheritedType) + " ×", () =>
                { InheritedType = string.Empty; RefreshFilters(); FiltersChanged?.Invoke(); }));
            if (InheritedTag.Length > 0)
                _activeFilters.Add(MakeButton("Tag: " + _inheritedTagLabel + " ×", () =>
                { InheritedTag = string.Empty; RefreshFilters(); FiltersChanged?.Invoke(); }));
            var hasFilters = _activeFilters.childCount > 0;
            if (hasFilters)
                _activeFilters.Add(MakeButton("Clear filters", ClearFilters));
            _activeFilters.style.display = hasFilters ? DisplayStyle.Flex : DisplayStyle.None;
            _clearFiltersButton.style.display = hasFilters ? DisplayStyle.Flex : DisplayStyle.None;
            if (_typePickerVisible) RefreshTypeSelection();
        }

        private void SetTypePickerVisible(bool visible)
        {
            if (_typePickerVisible == visible) return;
            _typePickerVisible = visible;
            var version = ++_typePickerAnimationVersion;
            _typePicker.SetEnabled(visible);
            _moreTypes.EnableInClassList(ZepUiClasses.SearchFilterSelected, visible);
            if (visible)
            {
                QuickLookDismissRequested?.Invoke();
                _typeBackdrop.style.display = DisplayStyle.Flex;
                _typeBackdrop.BringToFront();
                _typeSearch.SetValueWithoutNotify(string.Empty);
                RefreshTypeChoices();
                _typeChoices.scrollOffset = Vector2.zero;
                _typePicker.schedule.Execute(() =>
                {
                    if (version != _typePickerAnimationVersion) return;
                    _typePicker.AddToClassList(ZepUiClasses.SearchTypePickerOpen);
                    _typeSearch.Focus();
                }).ExecuteLater(16);
            }
            else
            {
                _typePicker.RemoveFromClassList(ZepUiClasses.SearchTypePickerOpen);
                if (IsVisible) _moreTypes.Focus();
                _typeBackdrop.schedule.Execute(() =>
                {
                    if (version == _typePickerAnimationVersion) _typeBackdrop.style.display = DisplayStyle.None;
                }).ExecuteLater(180);
            }
        }

        private void RefreshTypeChoices()
        {
            var focusedType = (focusController?.focusedElement as Button)?.userData as string;
            var scroll = _typeChoices.scrollOffset;
            _typeChoices.Clear();
            var common = new[] { "Prefab", "Material", "Texture", "Sprite", "Folder", "Script", "Scene", "AudioClip", "Model", "AnimationClip", "Shader", "ScriptableObject" };
            var query = _typeSearch.value?.Trim() ?? string.Empty;
            AddGroup("Common types", common);
            AddGroup("Project types", _typeOptions.Select(t => t.TypeName)
                .Where(t => !string.IsNullOrEmpty(t) && !common.Contains(t, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
            if (_typeChoices.childCount == 0)
            {
                var empty = new Label("No matching types.\nTry a different name.");
                empty.AddToClassList(ZepUiClasses.SearchEmpty);
                _typeChoices.Add(empty);
            }
            RefreshTypeSelection();
            _typeChoices.scrollOffset = scroll;

            void AddGroup(string heading, IEnumerable<string> types)
            {
                var headingAdded = false;
                foreach (var type in types)
                {
                    var label = ObjectNames.NicifyVariableName(AssetEntryRenderer.ResolveTypeDisplayName(type));
                    if (label.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                        && type.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!headingAdded)
                    {
                        var section = new Label(heading);
                        section.AddToClassList(ZepUiClasses.SearchTypeGroup);
                        _typeChoices.Add(section);
                        headingAdded = true;
                    }
                    var button = new Button(() => ToggleType(type)) { userData = type, tooltip = type };
                    button.AddToClassList(ZepUiClasses.FlatButton);
                    button.AddToClassList(ZepUiClasses.SearchTypeRow);
                    var check = new Label();
                    check.AddToClassList(ZepUiClasses.SearchTypeCheck);
                    button.Add(check);
                    var copy = new VisualElement();
                    copy.AddToClassList(ZepUiClasses.SearchResultText);
                    var name = new Label(label) { enableRichText = false };
                    name.AddToClassList(ZepUiClasses.SearchTypeName);
                    copy.Add(name);
                    if (label != type)
                    {
                        var technicalName = new Label(type) { enableRichText = false };
                        technicalName.AddToClassList(ZepUiClasses.SearchTypeTechnicalName);
                        copy.Add(technicalName);
                    }
                    button.Add(copy);
                    _typeChoices.Add(button);
                    if (focusedType == type)
                        button.schedule.Execute(() => { if (_typePickerVisible) button.Focus(); });
                }
            }
        }

        private void RefreshTypeSelection()
        {
            // Keep rows mounted while filters change so toggling a type retains
            // keyboard focus and the scroll position inside a long catalog.
            var parsed = SearchQuery.Parse(SearchText);
            var selected = new HashSet<string>(parsed.TypeTerms.Select(t => t.Value), StringComparer.OrdinalIgnoreCase);
            if (InheritedType.Length > 0) selected.Add(InheritedType);
            _typeSelectionSummary.text = selected.Count == 0 ? "All types" : $"{selected.Count} selected";
            _clearTypes.SetEnabled(selected.Count > 0 || parsed.ExcludedTerms.Any(t => t.Field == SearchField.Type));
            _typeChoices.Query<Button>().ForEach(button =>
            {
                var active = selected.Contains((string)button.userData);
                button.EnableInClassList(ZepUiClasses.SearchFilterSelected, active);
                button.Q<Label>(className: ZepUiClasses.SearchTypeCheck).text = active ? "✓" : string.Empty;
            });
        }

        internal void OnSearchFieldKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                if (_typePickerVisible) SetTypePickerVisible(false);
                else if (QuickLookDismissRequested?.Invoke() == true) FocusResults();
                else Hide();
            }
            else if (evt.keyCode == KeyCode.Space && evt.modifiers == EventModifiers.None
                && !_typePickerVisible && evt.target is VisualElement target
                && (target == _resultList || _resultList.Contains(target)))
            {
                if (!_isSearchTextRaiseQueued && SelectedResult != null)
                    QuickLookRequested?.Invoke();
            }
            else if (evt.keyCode == KeyCode.DownArrow || evt.keyCode == KeyCode.UpArrow)
            {
                if (_typePickerVisible)
                {
                    var choices = _typeChoices.Query<Button>().ToList();
                    if (choices.Count > 0)
                    {
                        var current = choices.IndexOf(focusController?.focusedElement as Button);
                        var next = current < 0 ? (evt.keyCode == KeyCode.DownArrow ? 0 : choices.Count - 1)
                            : (current + (evt.keyCode == KeyCode.DownArrow ? 1 : choices.Count - 1)) % choices.Count;
                        choices[next].Focus();
                        _typeChoices.ScrollTo(choices[next]);
                    }
                    focusController?.IgnoreEvent(evt);
                    evt.StopPropagation();
                    return;
                }
                var count = VisibleSuggestionCount();
                if (!_isSearchTextRaiseQueued && count > 0)
                {
                    MoveResultSelection(evt.keyCode == KeyCode.DownArrow ? 1 : -1);
                    FocusResults();
                }
            }
            else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                // Results for an earlier query must never open during a pending edit.
                if (_typePickerVisible && evt.target is not Button)
                {
                    focusController?.IgnoreEvent(evt);
                    evt.StopPropagation();
                    return;
                }
                if (evt.target is Button) return;
                // Only a result double-click selects it in the main window.
            }
            else if (evt.keyCode == KeyCode.Tab)
            {
                // Keep Tab inside the dialog while including the mounted scope
                // buttons. Hidden scroller buttons are not keyboard stops.
                var stops = new List<VisualElement> { _typePickerVisible ? _typeSearch : _searchTextInput };
                var focusScope = _typePickerVisible ? _typePicker : _card;
                focusScope.Query<VisualElement>().ForEach(element =>
                {
                    if (element is not Button && element != _typeSearch && element != _resultList) return;
                    if (stops.Contains(element) || (!_typePickerVisible && _typeBackdrop.Contains(element))
                        || _searchField.Contains(element) || !element.enabledInHierarchy
                        || element.worldBound.width <= 0 || element.worldBound.height <= 0) return;
                    for (var ancestor = element; ancestor != null && ancestor != _card; ancestor = ancestor.parent)
                        if (ancestor.resolvedStyle.display == DisplayStyle.None) return;
                    stops.Add(element);
                });
                var focused = focusController?.focusedElement as VisualElement;
                var current = focused == _searchField || (focused != null && _searchField.Contains(focused))
                    ? 0 : stops.IndexOf(focused);
                var next = (Math.Max(0, current) + (evt.shiftKey ? stops.Count - 1 : 1)) % stops.Count;
                stops[next].Focus();
            }
            else return;
            focusController?.IgnoreEvent(evt);
            evt.StopPropagation();
        }

        private void OnSearchFieldChanged(ChangeEvent<string> evt)
        {
            var newValue = evt.newValue ?? string.Empty;
            // Any edit is a new query; stale candidates cannot be activated.
            RefreshFilters();
            HideSuggestions();
            if (newValue.Length > 0) _emptyState.text = "Searching…";

            // Coalesce typing onto the editor tick; clearing drops pending results immediately.
            if (newValue.Length == 0)
            {
                CancelPendingSearchTextRaise();
                SearchTextChanged?.Invoke(newValue);
                return;
            }

            _pendingSearchText = newValue;
            if (_isSearchTextRaiseQueued)
            {
                return;
            }

            _isSearchTextRaiseQueued = true;
            _scheduler.Schedule(_raisePendingSearchText);
        }

        private void RaisePendingSearchText()
        {
            if (!_isSearchTextRaiseQueued)
            {
                return;
            }

            var searchText = _pendingSearchText;
            CancelPendingSearchTextRaise();

            // The panel can go away between the keystroke and the tick (window
            // closed or docked elsewhere). Raising then would query for a
            // dialog nobody is showing, so the keystroke is dropped — the same
            // lifecycle boundary as the dialog focus callback.
            if (panel == null)
            {
                return;
            }

            SearchTextChanged?.Invoke(searchText);
        }

        private void CancelPendingSearchTextRaise()
        {
            if (_isSearchTextRaiseQueued)
            {
                _isSearchTextRaiseQueued = false;
                _scheduler.Cancel(_raisePendingSearchText);
            }

            _pendingSearchText = null;
        }

        private int VisibleSuggestionCount() => _results.Count;

        private void SelectSuggestion(int index)
        {
            if (_isSearchTextRaiseQueued || index < 0 || index >= _results.Count) return;
            _selectedSuggestionIndex = index;
            RefreshSuggestionSelection();
            _resultList.ScrollToItem(index);
        }

        private void RefreshSuggestionSelection()
        {
            foreach (var row in _suggestionRows)
                row.Root.EnableInClassList(ZepUiClasses.SearchSuggestionRowSelected,
                    row.Index >= 0 && row.Index == _selectedSuggestionIndex);
            SelectedResultChanged?.Invoke();
        }

        private void OpenSuggestion(int index)
        {
            if (_isSearchTextRaiseQueued || index < 0 || index >= _results.Count) return;
            var asset = _results[index];
            RememberQuery();
            Hide();
            OpenRequested?.Invoke(asset.Path, asset.IsFolder);
        }

        private void HideSuggestions() => SetSuggestions(null);

        private void OnPreviewsReady(IReadOnlyList<string> paths)
        {
            foreach (var row in _suggestionRows)
                if (row.Index >= 0 && paths.Contains(row.AssetPath)) row.RefreshIcon();
        }

        private void LoadHistory()
        {
            if (_historyKey == null) return;
            try
            {
                var json = EditorPrefs.GetString(_historyKey, string.Empty);
                if (json.Length == 0) return;
                var history = JsonUtility.FromJson<RecentQueries>(json);
                if (history?.queries == null) throw new FormatException("Invalid history");
                _recentQueries.AddRange(history.queries.Where(q => !string.IsNullOrWhiteSpace(q)).Distinct().Take(6));
            }
            catch (Exception) { _historyError = "Recent queries could not be read. Clear history to reset."; }
        }

        private void RememberQuery()
        {
            var query = SearchText.Trim();
            if (query.Length == 0) return;
            _recentQueries.Remove(query);
            _recentQueries.Insert(0, query);
            if (_recentQueries.Count > 6) _recentQueries.RemoveAt(6);
            if (_historyKey != null)
            {
                try { EditorPrefs.SetString(_historyKey, JsonUtility.ToJson(new RecentQueries { queries = _recentQueries })); }
                catch (Exception) { _historyError = "Recent queries could not be saved."; }
            }
        }

        private void RefreshHistory()
        {
            _historySurface.Clear();
            var show = string.IsNullOrWhiteSpace(SearchText) && InheritedType.Length == 0 && InheritedTag.Length == 0;
            _historySurface.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show || _recentQueries.Count == 0 && _historyError == null) return;
            _historySurface.Add(new Label(_historyError ?? "Recent queries"));
            foreach (var query in _recentQueries)
            {
                var button = MakeButton(query, () => { _searchField.value = query; _searchField.Focus(); });
                button.tooltip = query;
                _historySurface.Add(button);
            }
            _historySurface.Add(MakeButton("Clear history", () =>
            {
                _recentQueries.Clear(); _historyError = null;
                if (_historyKey != null) EditorPrefs.DeleteKey(_historyKey);
                RefreshHistory();
            }));
        }

        private static string TypeLabel(ZepAssetEntry asset)
        {
            if (asset.IsFolder) return "Folder";
            var extension = Path.GetExtension(asset.Path).ToLowerInvariant();
            if (extension == ".prefab") return "Prefab";
            if (extension == ".fbx" || extension == ".obj" || extension == ".blend") return "Model";
            return AssetEntryRenderer.ResolveTypeDisplayName(asset.TypeName);
        }

        private string HighlightName(string name)
        {
            // Unity rich text has no HTML entity escaping. Render literal asset
            // names containing markup without rich text rather than interpreting them.
            if (name.IndexOf('<') >= 0 || name.IndexOf('>') >= 0) return name;
            var hits = new bool[name.Length];
            foreach (var term in SearchQuery.Parse(SearchText).RequiredTerms)
            {
                if (term.Field != SearchField.Any && term.Field != SearchField.Name || term.Value.Length == 0) continue;
                var at = 0;
                while ((at = name.IndexOf(term.Value, at, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    for (var i = at; i < at + term.Value.Length; i++) hits[i] = true;
                    at += term.Value.Length;
                }
            }
            var text = new StringBuilder();
            var bold = false;
            for (var i = 0; i < name.Length; i++)
            {
                if (hits[i] != bold) { text.Append(hits[i] ? "<b>" : "</b>"); bold = hits[i]; }
                text.Append(name[i]);
            }
            if (bold) text.Append("</b>");
            return text.ToString();
        }

        private sealed class SuggestionRow
        {
            private readonly SearchBarWidget _owner;
            private readonly Label _primary;
            private readonly Label _secondary;
            private readonly Label _type;
            private readonly Image _icon;
            private ZepAssetEntry _asset;
            private bool _openOnPointerUp;
            internal int Index { get; private set; } = -1;
            internal string AssetPath => _asset?.Path;
            internal VisualElement Root { get; }

            internal SuggestionRow(SearchBarWidget owner)
            {
                _owner = owner;
                Root = new VisualElement { pickingMode = PickingMode.Position };
                Root.AddToClassList(ZepUiClasses.SearchSuggestionRow);
                Root.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    owner.SelectSuggestion(Index);
                    owner.FocusResults();
                    // Mouse-up has no native double-click count. Keep the down-event
                    // intent until release so closing search cannot lose that release.
                    _openOnPointerUp = evt.clickCount == 2;
                });
                Root.RegisterCallback<PointerUpEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    var open = _openOnPointerUp;
                    _openOnPointerUp = false;
                    if (open) owner.OpenSuggestion(Index);
                });
                _icon = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                _icon.AddToClassList(ZepUiClasses.SearchResultIcon);
                Root.Add(_icon);
                _primary = new Label { enableRichText = true };
                _primary.AddToClassList(ZepUiClasses.SearchSuggestionPrimary);
                _secondary = new Label { enableRichText = false };
                _secondary.AddToClassList(ZepUiClasses.SearchSuggestionSecondary);
                var text = new VisualElement();
                text.AddToClassList(ZepUiClasses.SearchResultText);
                text.Add(_primary);
                text.Add(_secondary);
                Root.Add(text);
                _type = new Label { enableRichText = false };
                _type.AddToClassList(ZepUiClasses.SearchResultType);
                Root.Add(_type);
            }

            internal void Bind(int index, ZepAssetEntry asset)
            {
                _openOnPointerUp = false;
                Index = index;
                _asset = asset;
                Root.name = "zep-search-suggestion-" + index;
                _primary.enableRichText = asset.Name.IndexOf('<') < 0 && asset.Name.IndexOf('>') < 0;
                _primary.text = _owner.HighlightName(asset.Name);
                _secondary.text = AssetPathFormat.GetParentFolderPath(asset.Path)
                    + (_owner._approximatePaths.Contains(asset.Path) ? " · Similar name" : string.Empty);
                _type.text = TypeLabel(asset);
                Root.tooltip = asset.Name + " · " + _type.text + "\n" + asset.Path;
                Root.EnableInClassList(ZepUiClasses.SearchSuggestionRowSelected, index == _owner._selectedSuggestionIndex);
                RefreshIcon();
            }

            internal void RefreshIcon()
            {
                _icon.EnableInClassList(ZepUiClasses.ThemedFolderIcon, _asset.IsFolder);
                _icon.image = _owner._icons?.GetAssetIcon(_asset.Path, _asset.TypeName);
            }

            internal void Clear()
            {
                _openOnPointerUp = false;
                Index = -1;
                _asset = null;
                _icon.image = null;
                Root.name = string.Empty;
                Root.RemoveFromClassList(ZepUiClasses.SearchSuggestionRowSelected);
            }
        }
    }
}
