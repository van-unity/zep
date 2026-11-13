using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Indexing;
using Zep.Core.Domain;

namespace Zep.Core.Search
{
    public readonly struct AssetSearchSuggestion
    {
        internal AssetSearchSuggestion(AssetRecord asset, bool isFuzzy)
        {
            Asset = asset;
            IsFuzzy = isFuzzy;
        }

        public AssetRecord Asset { get; }

        internal string CompletionText
        {
            get
            {
                var name = Asset?.Name ?? string.Empty;
                if (!RequiresQuotedNameQuery(name))
                {
                    return name;
                }

                var quoted = name.Replace("\\", "\\\\").Replace("\"", "\\\"");

                // The parser trims inside quotes, so an EXACT name query can never match a
                // name whose own edges are whitespace — the trimmed token is compared
                // against the untrimmed stored name. A partial query on the same text
                // still resolves to this asset, so drop the '=' for those names only.
                // (The parser's trimming is grammar contract; the §10.1 harness pins it.)
                return HasEdgeWhitespace(name)
                    ? "name:\"" + quoted + "\""
                    : "name=\"" + quoted + "\"";
            }
        }

        public bool IsFuzzy { get; }

        private static bool HasEdgeWhitespace(string name)
        {
            return name.Length > 0
                && (char.IsWhiteSpace(name[0])
                    || char.IsWhiteSpace(name[name.Length - 1]));
        }

        // The parser promotes a bare 32-hex-character token to an exact GUID query, so a
        // hash-named asset (lightmaps, bundles, generated caches) would be suggested and
        // then find nothing on accept. Hex digits are case-insensitive, so the raw
        // characters answer for the normalized (lowercased) form as well.
        private static bool IsGuidShapedName(string name)
        {
            if (name.Length != 32)
            {
                return false;
            }

            for (var i = 0; i < name.Length; i++)
            {
                if (!Uri.IsHexDigit(name[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool RequiresQuotedNameQuery(string name)
        {
            if (string.IsNullOrEmpty(name)
                || name[0] == '-'
                || name[0] == '!'
                || IsGuidShapedName(name))
            {
                return true;
            }

            for (var i = 0; i < name.Length; i++)
            {
                var character = name[i];
                if (char.IsWhiteSpace(character)
                    || character == ':'
                    || character == '='
                    || character == '"'
                    || character == '*'
                    || character == '?'
                    || character == '{'
                    || character == '}')
                {
                    return true;
                }
            }

            return false;
        }
    }

    public sealed class AssetSearchService 
    {
        private const int GramLength = GlobMatcher.MinimumLiteralLength;
        private const float CompactionThreshold = 0.25f;
        private const int MaxSuggestionQueryLength = 64;
        // Suggestions publish at most eight rows. Bounding verification prevents
        // a common typo (for example, "plaeyr" in a project with thousands of
        // Player* assets) from turning edit-distance scoring into a corpus scan.
        private const int MaxExactSuggestionCandidates = 4096;
        private const int MaxFuzzySuggestionCandidates = 256;

        private readonly InMemoryAssetIndex _index;
        private List<AssetRecord> _recordsByOrdinal = new List<AssetRecord>();
        private Dictionary<ulong, List<int>> _anyFieldGramIndex = new Dictionary<ulong, List<int>>();
        private Dictionary<ulong, List<int>> _nameGramIndex = new Dictionary<ulong, List<int>>();
        private Dictionary<ulong, List<int>> _pathGramIndex = new Dictionary<ulong, List<int>>();
        private Dictionary<ulong, List<int>> _typeGramIndex = new Dictionary<ulong, List<int>>();
        private Dictionary<ulong, List<int>> _tagGramIndex = new Dictionary<ulong, List<int>>();
        private Dictionary<ulong, List<int>> _labelGramIndex = new Dictionary<ulong, List<int>>();
        private Dictionary<ulong, List<int>> _guidGramIndex = new Dictionary<ulong, List<int>>();
        // Multi-arity indices for "any field" queries (the dominant typing shape).
        // For queries of length 1 or 2 the trigram index is useless — they used to
        // hit a fallback that scanned every record. With these two indices,
        // single- and double-char queries become a single dictionary lookup that
        // returns the exact posting list (no verify needed: the gram IS the query).
        //
        // Memory cost (50k records, ~35 unique chars / ~60 unique 2-grams per
        // record): ~7 MB for 1-gram + ~12 MB for 2-gram. Built once on hydration.
        private Dictionary<char, List<int>> _anyFieldOneGramIndex = new Dictionary<char, List<int>>();
        private Dictionary<uint, List<int>> _anyFieldTwoGramIndex = new Dictionary<uint, List<int>>();
        private Dictionary<char, List<int>> _nameFieldOneGramIndex = new Dictionary<char, List<int>>();
        private Dictionary<uint, List<int>> _nameFieldTwoGramIndex = new Dictionary<uint, List<int>>();
        // Type text has a few dozen distinct values across an entire project, so a
        // 1- or 2-character `t:` is answered by scanning these keys instead of the
        // per-record one- and two-gram postings that used to serve it (~35 + ~60
        // entries per record for one rare query shape). One int per record, keyed by
        // the record's whole normalized type search text — a record has exactly one,
        // so the lists are disjoint and each is already in ascending ordinal order.
        private Dictionary<string, List<int>> _ordinalsByTypeSearchText =
            new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private Dictionary<char, List<int>> _labelFieldOneGramIndex = new Dictionary<char, List<int>>();
        private Dictionary<uint, List<int>> _labelFieldTwoGramIndex = new Dictionary<uint, List<int>>();
        private Dictionary<string, int> _guidToOrdinal = new Dictionary<string, int>(StringComparer.Ordinal);
        private HashSet<int> _tombstones = new HashSet<int>();
        private readonly HashSet<ulong> _reusableGramSet = new HashSet<ulong>();
        private readonly HashSet<char> _reusableOneGramSet = new HashSet<char>();
        private readonly HashSet<uint> _reusableTwoGramSet = new HashSet<uint>();
        private readonly List<ulong> _reusableQueryGrams = new List<ulong>();
        // Search() assembles one entry per query token plus optional folder/type scope.
        // Reused across calls to avoid a per-Search List<IReadOnlyList<int>> allocation.
        private readonly List<IReadOnlyList<int>> _reusableCandidateSources = new List<IReadOnlyList<int>>();
        // TryGetCandidates assembles one posting list per gram of the current token.
        // Reused across calls (called multiple times per Search, once per token).
        private readonly List<IReadOnlyList<int>> _reusableGramCandidates = new List<IReadOnlyList<int>>();
        // Working buffer for IntersectCandidateSources to hold its smallest-first
        // ordered view of the input sources. Shared because Search and TryGetCandidates
        // never hold a reference to it across calls.
        private readonly List<IReadOnlyList<int>> _reusableOrderedSources = new List<IReadOnlyList<int>>();
        private readonly HashSet<int> _reusableGroupUnion = new HashSet<int>();
        private readonly List<int> _reusableTypeGroupCandidates = new List<int>();
        private readonly List<int> _reusableLabelGroupCandidates = new List<int>();
        // Posting ordinals must remain append-only, but result ordering must follow
        // name/path order after imports and renames. ApplyCatalogDelta keeps this list
        // current by merging the ordinals it appended into it, so broad post-mutation
        // queries walk it instead of re-sorting the whole corpus.
        private List<int> _alphabeticalOrdinals = new List<int>();
        // Destination of that merge; swapped with _alphabeticalOrdinals so neither list
        // is reallocated per delta. Never handed out: Search snapshots into a fresh list.
        private List<int> _alphabeticalMergeBuffer = new List<int>();
        private readonly List<int> _reusableDeltaAppendedOrdinals = new List<int>();
        private readonly List<int> _reusableAlphabeticalCandidates = new List<int>();
        private readonly List<int> _reusableSuggestionCandidates = new List<int>();
        private readonly List<SuggestionCandidate> _reusableTopSuggestions = new List<SuggestionCandidate>();
        private byte[] _suggestionCandidateHitsByOrdinal = Array.Empty<byte>();
        private int[] _distanceRowA = Array.Empty<int>();
        private int[] _distanceRowB = Array.Empty<int>();
        private int[] _distanceRowC = Array.Empty<int>();
        private int[] _candidateGenerationByOrdinal = Array.Empty<int>();
        private int _candidateGeneration;
        private bool _alphabeticalOrdinalsDirty;
        // Single-slot memos for the folder-scope and type-filter candidate lists.
        // Both filters are stable across a typing burst while the query token
        // changes every keystroke, so without these every keystroke re-ran the
        // same gram intersection — for a deep CurrentFolder scope that is
        // several near-full-corpus posting merges plus an int[] allocation per
        // keystroke. Keyed on _indexedVersion because posting lists mutate in
        // place on incremental upserts.
        private readonly FilterCandidateMemo _folderScopeMemo = new FilterCandidateMemo();
        private readonly FilterCandidateMemo _typeNameMemo = new FilterCandidateMemo();
        private int _indexedVersion = -1;
        // Flips true the first time the search index is built (WarmIndex or the
        // lazy build on first Search). Before that, catalog changes remain in
        // the canonical index so an import the user never searches after does
        // not eagerly pay the full search build.
        private bool _hasBuiltIndex;
        // Set when the tombstone share crosses CompactionThreshold. Compaction is
        // a full gram-index rebuild, and ApplyCatalogDelta runs inside Unity's
        // import callback — doing it there froze the editor for seconds on a
        // large project. Nothing about the RESULTS depends on it: every read path
        // already skips tombstoned ordinals, so the only cost of waiting is the
        // memory the retired postings hold. The controller services this on its
        // deferred worker and adopts the compacted index generation-checked.
        private bool _compactionRequested;
        private int[] _allOrdinals = Array.Empty<int>();
        private bool _allOrdinalsDirty;
        // True while every entry already written to _allOrdinals equals its own
        // index. With no tombstones that is the answer for the whole array, so a
        // delta that only appended records can grow the array and fill the new
        // tail instead of rewriting all of it. The empty array starts identity.
        private bool _allOrdinalsAreIdentity = true;
        private bool _recordsAlphabeticallyOrdered = true;

        public AssetSearchService(InMemoryAssetIndex index)
        {
            _index = index ?? throw new ArgumentNullException(nameof(index));
        }

        internal bool HasBuiltIndex => _hasBuiltIndex;

        internal void ApplyCatalogDelta(AssetCatalogDelta delta)
        {
            if (delta == null || delta.IsEmpty || !_hasBuiltIndex)
            {
                return;
            }

            // A direct synchronizer/test mutation happened between the derived
            // search index's last adopted version and this atomic batch. Rebuild
            // once rather than applying a delta onto stale postings.
            if (_indexedVersion != delta.PreviousRecordVersion)
            {
                RebuildSearchIndex();
                return;
            }

            // Every ordinal from here up is one this delta appended, which is what
            // makes the incremental alphabetical merge below possible.
            var firstOrdinalAppendedByThisDelta = _recordsByOrdinal.Count;

            for (var i = 0; i < delta.RemovedRecords.Count; i++)
            {
                RetireIndexedRecord(delta.RemovedRecords[i].Guid);
            }

            for (var i = 0; i < delta.UpsertedRecords.Count; i++)
            {
                AddIndexedRecord(delta.UpsertedRecords[i]);
            }

            MergeAppendedOrdinalsIntoAlphabeticalOrder(firstOrdinalAppendedByThisDelta);
            _indexedVersion = _index.RecordVersion;
            CompactIfNeeded();
        }

        private void RetireIndexedRecord(string guid)
        {
            if (!string.IsNullOrEmpty(guid)
                && _guidToOrdinal.TryGetValue(guid, out var ordinal))
            {
                _tombstones.Add(ordinal);
                _guidToOrdinal.Remove(guid);
                _alphabeticalOrdinalsDirty = true;
                _allOrdinalsDirty = true;
            }
        }

        private void AddIndexedRecord(AssetRecord record)
        {
            if (record == null)
            {
                return;
            }

            RetireIndexedRecord(record.Guid);
            var ordinal = _recordsByOrdinal.Count;
            _recordsByOrdinal.Add(record);
            _guidToOrdinal[record.Guid] = ordinal;
            IndexField(_anyFieldGramIndex, record.NormalizedSearchText, ordinal);
            IndexAnyFieldShortGrams(record.NormalizedSearchText, ordinal);
            IndexField(_nameGramIndex, record.NormalizedName, ordinal);
            IndexFieldShortGrams(
                _nameFieldOneGramIndex,
                _nameFieldTwoGramIndex,
                record.NormalizedName,
                ordinal);
            IndexField(_pathGramIndex, record.NormalizedAssetPath, ordinal);
            IndexField(_typeGramIndex, record.NormalizedTypeSearchText, ordinal);
            IndexTypeSearchText(record.NormalizedTypeSearchText, ordinal);
            IndexField(_tagGramIndex, record.NormalizedTagSearchText, ordinal);
            IndexField(_labelGramIndex, record.NormalizedUnityLabelSearchText, ordinal);
            IndexFieldShortGrams(
                _labelFieldOneGramIndex,
                _labelFieldTwoGramIndex,
                record.NormalizedUnityLabelSearchText,
                ordinal);
            IndexField(
                _guidGramIndex,
                record.NormalizedGuid,
                ordinal,
                SearchQuery.GuidIndexedPrefixLength);
            EnsureCandidateGenerationCapacity();
            _alphabeticalOrdinalsDirty = true;
            _allOrdinalsDirty = true;
            _recordsAlphabeticallyOrdered = false;
        }

        // Explicit warm-up trigger. The controller calls this in its deferred-startup
        // phase (post-paint, via EditorApplication.delayCall) so the first user Search
        // doesn't pay the full ~1s gram-index rebuild on a large project. Builds against
        // the current index state; subsequent Search calls see the adopted record version
        // and skip the rebuild.
        internal void WarmIndex()
        {
            EnsureSearchIndexIsCurrent();
        }

        internal static WorkerBuild BuildOnWorker(
            IReadOnlyList<AssetRecord> records,
            int expectedRecordVersion)
        {
            var snapshotIndex = new InMemoryAssetIndex();
            snapshotIndex.Replace(
                records ?? Array.Empty<AssetRecord>(),
                Array.Empty<AssetFolderRecord>());
            var built = new AssetSearchService(snapshotIndex);
            built.WarmIndex();
            return new WorkerBuild(built, expectedRecordVersion);
        }

        internal bool TryAdoptWorkerBuild(WorkerBuild build)
        {
            if (build == null
                || build.ExpectedRecordVersion != _index.RecordVersion)
            {
                return false;
            }

            var built = build.Service;
            _recordsByOrdinal = built._recordsByOrdinal;
            _anyFieldGramIndex = built._anyFieldGramIndex;
            _nameGramIndex = built._nameGramIndex;
            _pathGramIndex = built._pathGramIndex;
            _typeGramIndex = built._typeGramIndex;
            _tagGramIndex = built._tagGramIndex;
            _labelGramIndex = built._labelGramIndex;
            _guidGramIndex = built._guidGramIndex;
            _anyFieldOneGramIndex = built._anyFieldOneGramIndex;
            _anyFieldTwoGramIndex = built._anyFieldTwoGramIndex;
            _nameFieldOneGramIndex = built._nameFieldOneGramIndex;
            _nameFieldTwoGramIndex = built._nameFieldTwoGramIndex;
            _ordinalsByTypeSearchText = built._ordinalsByTypeSearchText;
            _labelFieldOneGramIndex = built._labelFieldOneGramIndex;
            _labelFieldTwoGramIndex = built._labelFieldTwoGramIndex;
            _guidToOrdinal = built._guidToOrdinal;
            _tombstones = built._tombstones;
            _alphabeticalOrdinals = built._alphabeticalOrdinals;
            _candidateGenerationByOrdinal = built._candidateGenerationByOrdinal;
            _suggestionCandidateHitsByOrdinal = new byte[_candidateGenerationByOrdinal.Length];
            _candidateGeneration = built._candidateGeneration;
            _alphabeticalOrdinalsDirty = built._alphabeticalOrdinalsDirty;
            _allOrdinals = built._allOrdinals;
            _allOrdinalsDirty = built._allOrdinalsDirty;
            _allOrdinalsAreIdentity = built._allOrdinalsAreIdentity;
            _recordsAlphabeticallyOrdered = built._recordsAlphabeticallyOrdered;
            _folderScopeMemo.Version = -1;
            _typeNameMemo.Version = -1;
            _indexedVersion = build.ExpectedRecordVersion;
            _hasBuiltIndex = true;
            // The adopted build was made from live records only, so whatever
            // compaction was pending has just happened.
            _compactionRequested = false;
            return true;
        }

        internal sealed class WorkerBuild
        {
            internal WorkerBuild(
                AssetSearchService service,
                int expectedRecordVersion)
            {
                Service = service;
                ExpectedRecordVersion = expectedRecordVersion;
            }

            internal AssetSearchService Service { get; }

            internal int ExpectedRecordVersion { get; }
        }

        internal IReadOnlyList<AssetRecord> Search(string query, string folderScopePath = "", string typeName = "")
        {
            return Search(SearchQuery.Parse(query), folderScopePath, typeName);
        }

        // Parse-free entry point: BrowseProjectionService already compiles the
        // query once per projection build (folder entries are matched against
        // the same tokens), so it hands those tokens straight through instead
        // of paying a second Parse per keystroke.
        //
        // Results remain alphabetically stable after incremental imports and renames.
        // Posting-list ordinals themselves stay append-only; when an upsert makes that
        // order dirty, only the returned snapshot is sorted.
        internal IReadOnlyList<AssetRecord> Search(
            ParsedSearchQuery searchQuery,
            string folderScopePath = "",
            string typeName = "")
        {
            searchQuery = searchQuery ?? new ParsedSearchQuery();
            // PERF-DIAG: EnsureSearchIndexIsCurrent is the lazy-build trigger on first
            // Search() of the session. If RebuildSearchIndex ran, that log line will
            // appear immediately before this one; warm calls log ~0ms. The stopwatch
            // itself is gated on IsEnabled so a disabled log costs this hot path
            // nothing (and stays null-guarded — never dereference an ungated sink).
            System.Diagnostics.Stopwatch ensureStopwatch = null;
            if (ZepPerfLog.IsEnabled)
            {
                ensureStopwatch = System.Diagnostics.Stopwatch.StartNew();
            }

            EnsureSearchIndexIsCurrent();
            if (ensureStopwatch != null)
            {
                ensureStopwatch.Stop();
                ZepPerfLog.Log($"[Zep/perf] AssetSearchService.Search ensureIndexCurrent tokens={searchQuery.Count} ms={ensureStopwatch.Elapsed.TotalMilliseconds:F2}");
            }

            if (searchQuery.MatchesNothing)
            {
                return Array.Empty<AssetRecord>();
            }

            var normalizedFolderScopePath = SearchQuery.NormalizeValue(folderScopePath);
            var normalizedTypeName = SearchQuery.NormalizeValue(typeName);

            if (searchQuery.IsEmpty
                && string.IsNullOrEmpty(normalizedFolderScopePath)
                && string.IsNullOrEmpty(normalizedTypeName))
            {
                if (_tombstones.Count == 0 && _recordsAlphabeticallyOrdered)
                {
                    // Snapshot, never the live field. BrowseProjectionService retains
                    // this result inside a UI-bound LazyAssetEntryList, so a later
                    // UpsertRecord append or a RebuildSearchIndex clear would mutate
                    // the collection the ListView is still binding against. Reachable
                    // from the UI: a lone "-" (typed on the way to an exclusion term)
                    // parses to an empty query, and the All scope resolves to no path.
                    // Every other Search path already returns a fresh list; this was
                    // the one that aliased.
                    return new List<AssetRecord>(_recordsByOrdinal);
                }

                EnsureAlphabeticalOrdinalsAreCurrent();
                var liveRecords = new List<AssetRecord>(_alphabeticalOrdinals.Count);
                for (var i = 0; i < _alphabeticalOrdinals.Count; i++)
                {
                    liveRecords.Add(_recordsByOrdinal[_alphabeticalOrdinals[i]]);
                }

                return liveRecords;
            }

            var candidateSources = _reusableCandidateSources;
            candidateSources.Clear();
            for (var i = 0; i < searchQuery.RequiredTerms.Count; i++)
            {
                if (!TryGetCandidates(searchQuery.RequiredTerms[i], out var candidates))
                {
                    return Array.Empty<AssetRecord>();
                }

                candidateSources.Add(candidates);
            }

            if (searchQuery.TypeTerms.Count > 0)
            {
                if (!TryGetGroupCandidates(
                        searchQuery.TypeTerms,
                        _reusableTypeGroupCandidates,
                        out var typeCandidates))
                {
                    return Array.Empty<AssetRecord>();
                }

                candidateSources.Add(typeCandidates);
            }

            if (searchQuery.LabelTerms.Count > 0)
            {
                if (!TryGetGroupCandidates(
                        searchQuery.LabelTerms,
                        _reusableLabelGroupCandidates,
                        out var labelCandidates))
                {
                    return Array.Empty<AssetRecord>();
                }

                candidateSources.Add(labelCandidates);
            }

            // Root scopes ("Assets"/"Packages" — the default) are skipped here:
            // their gram posting lists contain nearly every record because every
            // asset path starts with one of those prefixes, so the intersection
            // buys no selectivity and costs full-corpus merge passes on every
            // keystroke. The per-record MatchesFolderScope check in the result
            // loop enforces the same predicate. Deeper scopes stay on the gram
            // path, where the posting intersection is genuinely selective.
            if (!string.IsNullOrEmpty(normalizedFolderScopePath) && !IsCorpusWideScope(normalizedFolderScopePath))
            {
                if (!TryGetFilterCandidates(SearchField.Path, normalizedFolderScopePath, _folderScopeMemo, out var candidates))
                {
                    return Array.Empty<AssetRecord>();
                }

                candidateSources.Add(candidates);
            }

            if (!string.IsNullOrEmpty(normalizedTypeName))
            {
                if (!TryGetFilterCandidates(SearchField.Type, normalizedTypeName, _typeNameMemo, out var candidates))
                {
                    return Array.Empty<AssetRecord>();
                }

                candidateSources.Add(candidates);
            }

            var candidateOrdinals = candidateSources.Count == 0
                ? (IReadOnlyList<int>)GetAllOrdinals()
                : IntersectCandidateSources(candidateSources, _reusableOrderedSources);
            if (candidateOrdinals.Count == 0)
            {
                return Array.Empty<AssetRecord>();
            }

            // After an incremental upsert, append-only posting ordinals no longer
            // imply alphabetical order. For broad candidate sets, ordering once via
            // generation marks is linear and avoids a repeated O(results log results)
            // string sort. Narrow sets keep the cheaper candidate-first path and sort
            // only their small verified result list below.
            var candidatesAreAlphabetical = _recordsAlphabeticallyOrdered;
            if (!candidatesAreAlphabetical
                && candidateOrdinals.Count >= Math.Max(
                    32,
                    (_recordsByOrdinal.Count - _tombstones.Count) / 8))
            {
                candidateOrdinals = OrderCandidatesAlphabetically(candidateOrdinals);
                candidatesAreAlphabetical = true;
            }

            // Tombstones exist only between a file operation and the next
            // compaction; the common typing-burst case has none, so skip the
            // per-candidate set probe entirely then.
            var hasTombstones = _tombstones.Count > 0;
            var shortAnyPostingIsAuthoritative =
                IsAuthoritativeShortAnyPosting(searchQuery);

            // Verification fast path for the dominant typing shape. MatchesToken's
            // default arm ORs Contains over the name, the type name, the tag text and
            // the label text; AssetRecord.NormalizedSearchText is exactly those four
            // fields joined by '\n'. An occurrence of the term in that concatenation
            // therefore either lies wholly inside one field — which is what the OR
            // asks — or straddles a '\n', which requires the term itself to contain
            // '\n'. The parser treats '\n' as a token separator, but a quoted phrase
            // can still carry one, so the shape check rejects such a term instead of
            // assuming it cannot happen. That makes one IndexOf exactly equivalent to
            // the four field scans, and it also skips the per-candidate SearchDocument
            // that SearchQuery.Matches(record, query) builds.
            //
            // The GUID is no longer in that concatenation, so the term's GUID-prefix
            // leg is checked separately, and only for a term that could prefix a GUID.
            string soleUnqualifiedTerm = null;
            var soleUnqualifiedTermCouldPrefixGuid = false;
            if (!shortAnyPostingIsAuthoritative
                && IsSoleUnqualifiedPartialTerm(searchQuery, out var soleTerm)
                && soleTerm.IndexOf('\n') < 0)
            {
                soleUnqualifiedTerm = soleTerm;
                soleUnqualifiedTermCouldPrefixGuid =
                    SearchQuery.IsPossibleUnqualifiedGuidPrefix(soleTerm);
            }

            // Keep broad results as stable posting ordinals. The browse projection
            // retains this list behind LazyAssetEntryList, so storing one object
            // reference per match would allocate roughly twice as much memory while
            // still materializing no visible row. The ordinal wrapper pins the
            // record-list generation it was built against and resolves records only
            // when a consumer indexes a visible result.
            //
            // The array is sized by CANDIDATES, not by matches, so it used to cost
            // one 268 KB allocation per keystroke for "p" at 100k records — even when
            // the query matched nothing. Fill a reused buffer instead and only give
            // the result list an array of its own at the end. The buffer itself can
            // never be handed out while another list still reads it: a split view has
            // two live panes sharing this one service, each retaining its own result
            // list inside a UI-bound LazyAssetEntryList, and ApplyPaneProjection
            // refreshes one pane while the other stays bound. That is why this is a
            // hand-off rule rather than a generation stamp.
            // The scope is fixed for the whole search, so its shape is resolved
            // ONCE here instead of per candidate. The default pane scope is a
            // corpus-wide root ("assets"/"packages"), and MatchesFolderScope
            // charged every record an Equals plus a StartsWith for it — measured
            // 2.93 ms for "p" at 100k against 0.45 ms for the All scope, which
            // is now the difference between skipping this block entirely and
            // entering it. Inside, the separator character decides membership in
            // one indexed read before any prefix comparison runs. The predicate
            // is exactly MatchesFolderScope's: when the candidate is longer than
            // the scope the equality leg cannot hold, and when it is not longer
            // the prefix leg cannot.
            var folderScopeIsAll = string.IsNullOrEmpty(normalizedFolderScopePath);
            var folderScopeLength = folderScopeIsAll ? 0 : normalizedFolderScopePath.Length;
            var resultOrdinals = RentResultBuffer(candidateOrdinals.Count);
            var resultCount = 0;
            for (var i = 0; i < candidateOrdinals.Count; i++)
            {
                var ordinal = candidateOrdinals[i];
                if (hasTombstones && _tombstones.Contains(ordinal))
                {
                    continue;
                }

                var record = _recordsByOrdinal[ordinal];
                if (soleUnqualifiedTerm != null)
                {
                    if (record.NormalizedSearchText.IndexOf(
                            soleUnqualifiedTerm,
                            StringComparison.Ordinal) < 0
                        && !(soleUnqualifiedTermCouldPrefixGuid
                            && record.NormalizedGuid.StartsWith(
                                soleUnqualifiedTerm,
                                StringComparison.Ordinal)))
                    {
                        continue;
                    }
                }
                else if (!shortAnyPostingIsAuthoritative
                    && !SearchQuery.Matches(record, searchQuery))
                {
                    continue;
                }

                if (!folderScopeIsAll)
                {
                    var candidatePath = record.NormalizedAssetPath;
                    if (candidatePath.Length > folderScopeLength)
                    {
                        if (candidatePath[folderScopeLength] != '/'
                            || !candidatePath.StartsWith(normalizedFolderScopePath, StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }
                    else if (!string.Equals(candidatePath, normalizedFolderScopePath, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                if (!MatchesTypeName(record.NormalizedTypeName, normalizedTypeName))
                {
                    continue;
                }

                resultOrdinals[resultCount++] = ordinal;
            }

            if (!candidatesAreAlphabetical)
            {
                Array.Sort(
                    resultOrdinals,
                    0,
                    resultCount,
                    new OrdinalComparer(_recordsByOrdinal));
            }

            return resultCount == 0
                ? Array.Empty<AssetRecord>()
                : new OrdinalResultList(
                    _recordsByOrdinal,
                    ReleaseResultBuffer(resultOrdinals, resultCount),
                    resultCount);
        }

        // Scratch for one Search's result ordinals, grown to the largest candidate set
        // seen so far and reused after that. A search that matches nothing — the common
        // state while a term is half typed — now allocates nothing at all.
        private int[] _resultBuffer = Array.Empty<int>();

        private int[] RentResultBuffer(int candidateCount)
        {
            if (_resultBuffer.Length < candidateCount)
            {
                _resultBuffer = new int[candidateCount];
            }

            return _resultBuffer;
        }

        // The returned array belongs to the result list from here on, because two panes
        // can hold two live lists over this one service and neither may see the other's
        // ordinals move. A sparse result gets its own right-sized copy and the buffer
        // stays; a result that already fills most of the buffer takes the buffer itself,
        // since copying it would cost as much as the allocation it saves.
        private int[] ReleaseResultBuffer(int[] buffer, int resultCount)
        {
            if (!ReferenceEquals(buffer, _resultBuffer))
            {
                return buffer;
            }

            if (resultCount < buffer.Length / 2)
            {
                var trimmed = new int[resultCount];
                Array.Copy(buffer, trimmed, resultCount);
                return trimmed;
            }

            _resultBuffer = Array.Empty<int>();
            return buffer;
        }

        // Test seam. Every bound on the Suggest path holds down cost, not correctness, so
        // a regression in one of them is invisible from the outside — the popup still
        // shows the right eight rows, it just scans the corpus to find them. These
        // counters record what the LAST Suggest call actually walked, so the bound tests
        // can assert the bounds instead of timing them.
        internal int LastSuggestPrefixScanRecords { get; private set; }

        internal int LastSuggestExactPostingRecords { get; private set; }

        internal int LastSuggestFuzzyCandidates { get; private set; }

        internal int LastSuggestFuzzyDistanceEvaluations { get; private set; }

        internal long LastSuggestDistanceCells { get; private set; }

        internal IReadOnlyList<AssetSearchSuggestion> Suggest(
            string query,
            string folderScopePath = "",
            string typeName = "",
            int limit = 8)
        {
            return Suggest(SearchQuery.Parse(query), folderScopePath, typeName, limit);
        }

        // Parse-free entry point, for the same reason Search has one: the
        // suggestion pass runs on the keystroke that just built a projection,
        // and BrowseProjectionService already parsed that exact text. Parsing
        // it a second time was pure duplicate work on the typing path.
        internal IReadOnlyList<AssetSearchSuggestion> Suggest(
            ParsedSearchQuery searchQuery,
            string folderScopePath = "",
            string typeName = "",
            int limit = 8)
        {
            LastSuggestPrefixScanRecords = 0;
            LastSuggestExactPostingRecords = 0;
            LastSuggestFuzzyCandidates = 0;
            LastSuggestFuzzyDistanceEvaluations = 0;
            LastSuggestDistanceCells = 0;

            if (limit <= 0
                || !TryGetSuggestionToken(searchQuery, out var value, out var filters)
                || value.Length > MaxSuggestionQueryLength)
            {
                return Array.Empty<AssetSearchSuggestion>();
            }

            EnsureSearchIndexIsCurrent();
            var normalizedFolderScopePath = SearchQuery.NormalizeValue(folderScopePath);
            var normalizedTypeName = SearchQuery.NormalizeValue(typeName);
            var top = _reusableTopSuggestions;
            top.Clear();
            AddPrefixSuggestionCandidates(
                value,
                normalizedFolderScopePath,
                normalizedTypeName,
                limit, filters);

            // Runs even when the prefix pass already filled the top list: it is the only
            // pass that sees names matching in the MIDDLE, and the top list is a ranking,
            // not a quota — a full list can still be improved. Bounded by
            // MaxExactSuggestionCandidates.
            var token = SearchToken.CreateNormalized(SearchField.Name, value);
            if (TryGetCandidates(token, out var exactCandidates))
            {
                var exactCandidateCount = Math.Min(
                    exactCandidates.Count,
                    MaxExactSuggestionCandidates);
                LastSuggestExactPostingRecords = exactCandidateCount;
                for (var i = 0; i < exactCandidateCount; i++)
                {
                    var ordinal = exactCandidates[i];
                    if (_tombstones.Contains(ordinal))
                    {
                        continue;
                    }

                    var record = _recordsByOrdinal[ordinal];
                    if (!MatchesFolderScope(record.NormalizedAssetPath, normalizedFolderScopePath)
                        || !MatchesTypeName(record.NormalizedTypeName, normalizedTypeName)
                        || !SearchQuery.Matches(record, filters))
                    {
                        continue;
                    }

                    var score = GetExactSuggestionScore(record, value);
                    if (score >= 0)
                    {
                        ConsiderSuggestion(
                            new SuggestionCandidate(ordinal, score, isFuzzy: false),
                            limit);
                    }
                }
            }

            if (value.Length >= 3 && top.Count < limit)
            {
                var maxDistance = value.Length >= 7 ? 2 : 1;
                CollectFuzzySuggestionCandidates(value, maxDistance, out var minimumHits);
                LastSuggestFuzzyCandidates = _reusableSuggestionCandidates.Count;
                for (var i = 0; i < _reusableSuggestionCandidates.Count; i++)
                {
                    var ordinal = _reusableSuggestionCandidates[i];
                    if (_suggestionCandidateHitsByOrdinal[ordinal] < minimumHits
                        || _tombstones.Contains(ordinal))
                    {
                        continue;
                    }

                    var record = _recordsByOrdinal[ordinal];
                    if (record.NormalizedName.IndexOf(value, StringComparison.Ordinal) >= 0
                        || !MatchesFolderScope(record.NormalizedAssetPath, normalizedFolderScopePath)
                        || !MatchesTypeName(record.NormalizedTypeName, normalizedTypeName)
                        || !SearchQuery.Matches(record, filters))
                    {
                        continue;
                    }

                    LastSuggestFuzzyDistanceEvaluations++;
                    var distance = GetBestFuzzyNameDistance(
                        record,
                        value,
                        maxDistance,
                        out var wordStart);
                    if (distance > maxDistance)
                    {
                        continue;
                    }

                    var score = distance * 1000
                        + (wordStart == 0 ? 0 : 100 + wordStart);
                    ConsiderSuggestion(
                        new SuggestionCandidate(ordinal, score, isFuzzy: true),
                        limit);
                }
            }

            if (top.Count == 0)
            {
                return Array.Empty<AssetSearchSuggestion>();
            }

            var suggestions = new AssetSearchSuggestion[top.Count];
            for (var i = 0; i < top.Count; i++)
            {
                var candidate = top[i];
                suggestions[i] = new AssetSearchSuggestion(
                    _recordsByOrdinal[candidate.Ordinal],
                    candidate.IsFuzzy);
            }

            return suggestions;
        }

        private void AddPrefixSuggestionCandidates(
            string value,
            string normalizedFolderScopePath,
            string normalizedTypeName,
            int limit, ParsedSearchQuery filters)
        {
            EnsureAlphabeticalOrdinalsAreCurrent();
            var low = 0;
            var high = _alphabeticalOrdinals.Count;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                var record = _recordsByOrdinal[_alphabeticalOrdinals[middle]];
                if (string.Compare(
                        record.Name,
                        value,
                        StringComparison.OrdinalIgnoreCase) < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            var examined = 0;
            for (var i = low;
                i < _alphabeticalOrdinals.Count
                    && examined < MaxExactSuggestionCandidates;
                i++, examined++)
            {
                var ordinal = _alphabeticalOrdinals[i];
                var record = _recordsByOrdinal[ordinal];
                if (!record.NormalizedName.StartsWith(value, StringComparison.Ordinal))
                {
                    break;
                }

                if (!MatchesFolderScope(record.NormalizedAssetPath, normalizedFolderScopePath)
                    || !MatchesTypeName(record.NormalizedTypeName, normalizedTypeName)
                        || !SearchQuery.Matches(record, filters))
                {
                    continue;
                }

                // No break once the top list is full: the scan walks the prefix run
                // alphabetically while the score model prefers the SHORTEST completion,
                // so a strictly better match ("Players") routinely sorts after a run of
                // longer ones ("PlayerAbility*Controller"). ConsiderSuggestion already
                // keeps only the best `limit`; the run itself stays bounded by
                // MaxExactSuggestionCandidates and by the first non-prefix name.
                ConsiderSuggestion(
                    new SuggestionCandidate(
                        ordinal,
                        GetExactSuggestionScore(record, value),
                        isFuzzy: false),
                    limit);
            }

            LastSuggestPrefixScanRecords = examined;
        }

        private static bool TryGetSuggestionToken(ParsedSearchQuery parsed, out string value, out ParsedSearchQuery filters)
        {
            value = string.Empty;
            filters = new ParsedSearchQuery();
            if (parsed == null || parsed.MatchesNothing) return false;
            foreach (var token in parsed.RequiredTerms)
            {
                if (token.Field == SearchField.Any || token.Field == SearchField.Name)
                {
                    if (value.Length > 0 || token.MatchMode != SearchMatchMode.Partial) return false;
                    value = token.Value;
                }
                else filters.Add(token, false);
            }
            foreach (var token in parsed.TypeTerms) filters.Add(token, false);
            foreach (var token in parsed.LabelTerms) filters.Add(token, false);
            foreach (var token in parsed.ExcludedTerms) filters.Add(token, true);
            return value.Length > 0;
        }

        private static int GetExactSuggestionScore(AssetRecord record, string value)
        {
            var name = record.NormalizedName;
            if (string.Equals(name, value, StringComparison.Ordinal))
            {
                return 0;
            }

            if (name.StartsWith(value, StringComparison.Ordinal))
            {
                return 100 + Math.Min(99, name.Length - value.Length);
            }

            var matchIndex = name.IndexOf(value, StringComparison.Ordinal);
            if (matchIndex < 0)
            {
                return -1;
            }

            return IsSuggestionWordStart(record, matchIndex)
                ? 200 + Math.Min(99, matchIndex)
                : 300 + Math.Min(99, matchIndex);
        }

        private void CollectFuzzySuggestionCandidates(
            string value,
            int maxDistance,
            out int minimumHits)
        {
            if (_candidateGeneration == int.MaxValue)
            {
                Array.Clear(_candidateGenerationByOrdinal, 0, _candidateGenerationByOrdinal.Length);
                _candidateGeneration = 0;
            }

            var generation = ++_candidateGeneration;
            var candidates = _reusableSuggestionCandidates;
            candidates.Clear();
            var candidateOrdinalCeiling = -1;
            var postingSources = _reusableGramCandidates;
            postingSources.Clear();

            if (value.Length == 3)
            {
                _reusableOneGramSet.Clear();
                for (var i = 0; i < value.Length; i++)
                {
                    var character = value[i];
                    if (!_reusableOneGramSet.Add(character)
                        || !_nameFieldOneGramIndex.TryGetValue(character, out var postings))
                    {
                        continue;
                    }

                    postingSources.Add(postings);
                }

                postingSources.Sort(static (left, right) => left.Count.CompareTo(right.Count));
                for (var i = 0; i < postingSources.Count; i++)
                {
                    CountSuggestionCandidateHits(
                        postingSources[i],
                        generation,
                        candidates,
                        ref candidateOrdinalCeiling);
                }

                minimumHits = Math.Max(1, _reusableOneGramSet.Count - maxDistance);
                return;
            }

            _reusableTwoGramSet.Clear();
            for (var i = 0; i < value.Length - 1; i++)
            {
                var gram = Pack2Gram(value, i);
                if (!_reusableTwoGramSet.Add(gram)
                    || !_nameFieldTwoGramIndex.TryGetValue(gram, out var postings))
                {
                    continue;
                }

                postingSources.Add(postings);
            }

            postingSources.Sort(static (left, right) => left.Count.CompareTo(right.Count));
            for (var i = 0; i < postingSources.Count; i++)
            {
                CountSuggestionCandidateHits(
                    postingSources[i],
                    generation,
                    candidates,
                    ref candidateOrdinalCeiling);
            }

            var gramsAnEditCanDisrupt = maxDistance == 1 ? 3 : 6;
            minimumHits = Math.Max(1, _reusableTwoGramSet.Count - gramsAnEditCanDisrupt);
        }

        private void CountSuggestionCandidateHits(
            IReadOnlyList<int> postings,
            int generation,
            List<int> candidates,
            ref int candidateOrdinalCeiling)
        {
            for (var i = 0; i < postings.Count; i++)
            {
                var ordinal = postings[i];
                if (candidates.Count >= MaxFuzzySuggestionCandidates
                    && ordinal > candidateOrdinalCeiling)
                {
                    break;
                }

                if (_candidateGenerationByOrdinal[ordinal] != generation)
                {
                    if (candidates.Count >= MaxFuzzySuggestionCandidates)
                    {
                        continue;
                    }

                    _candidateGenerationByOrdinal[ordinal] = generation;
                    _suggestionCandidateHitsByOrdinal[ordinal] = 1;
                    candidates.Add(ordinal);
                    candidateOrdinalCeiling = Math.Max(candidateOrdinalCeiling, ordinal);
                }
                else if (_suggestionCandidateHitsByOrdinal[ordinal] < byte.MaxValue)
                {
                    _suggestionCandidateHitsByOrdinal[ordinal]++;
                }
            }
        }

        private int GetBestFuzzyNameDistance(
            AssetRecord record,
            string value,
            int maxDistance,
            out int bestWordStart)
        {
            var bestDistance = maxDistance + 1;
            bestWordStart = 0;
            CompareSuggestionPrefix(
                value,
                record.NormalizedName,
                0,
                record.NormalizedName.Length,
                maxDistance,
                ref bestDistance);

            for (var i = 1; i < record.NormalizedName.Length; i++)
            {
                if (!IsSuggestionWordStart(record, i))
                {
                    continue;
                }

                var wordEnd = FindSuggestionWordEnd(record, i);
                var before = bestDistance;
                CompareSuggestionPrefix(
                    value,
                    record.NormalizedName,
                    i,
                    wordEnd - i,
                    maxDistance,
                    ref bestDistance);
                if (bestDistance < before)
                {
                    bestWordStart = i;
                }
            }

            return bestDistance;
        }

        private void CompareSuggestionPrefix(
            string value,
            string normalizedName,
            int start,
            int availableLength,
            int maxDistance,
            ref int bestDistance)
        {
            var minimumLength = Math.Max(1, value.Length - maxDistance);
            var maximumLength = Math.Min(availableLength, value.Length + maxDistance);
            if (maximumLength < minimumLength)
            {
                return;
            }

            // Only a distance below the current best can change anything, so that is the
            // bound the table gets to early-out against.
            var bound = Math.Min(maxDistance, bestDistance - 1);
            if (bound < 0
                || !TryComputeBoundedDistanceRow(
                        value,
                        normalizedName,
                        start,
                        maximumLength,
                        bound,
                        out var distances))
            {
                return;
            }

            // One table over the LONGEST candidate prefix already carries every shorter
            // prefix distance: its final row is distance(value, target[start..start+j])
            // for every j. Building a fresh table per length re-walked the same cells
            // (~5x the cell work) for the same answers. Every j in [minimumLength,
            // maximumLength] satisfies |value.Length - j| <= maxDistance by construction,
            // so the per-length length-difference rejection the old loop relied on could
            // never fire and is not lost here.
            for (var length = minimumLength; length <= maximumLength; length++)
            {
                if (distances[length] < bestDistance)
                {
                    bestDistance = distances[length];
                }
            }
        }

        // Computes the Damerau-Levenshtein distance of <paramref name="source"/> against
        // EVERY prefix of target[targetStart .. targetStart+targetLength]; the answers are
        // the final row, indexed by prefix length. Returns false when the bound proves no
        // prefix can land within <paramref name="maxDistance"/> — row minima never
        // decrease, so a row that clears the bound everywhere ends the table. The row must
        // not be read in that case.
        private bool TryComputeBoundedDistanceRow(
            string source,
            string target,
            int targetStart,
            int targetLength,
            int maxDistance,
            out int[] distances)
        {
            distances = Array.Empty<int>();
            if (maxDistance < 0)
            {
                return false;
            }

            EnsureDistanceRowCapacity(targetLength + 1);
            var previousPrevious = _distanceRowA;
            var previous = _distanceRowB;
            var current = _distanceRowC;
            for (var j = 0; j <= targetLength; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= source.Length; i++)
            {
                current[0] = i;
                var rowMinimum = current[0];
                for (var j = 1; j <= targetLength; j++)
                {
                    var substitutionCost = source[i - 1] == target[targetStart + j - 1]
                        ? 0
                        : 1;
                    var distance = Math.Min(
                        Math.Min(previous[j] + 1, current[j - 1] + 1),
                        previous[j - 1] + substitutionCost);
                    if (i > 1
                        && j > 1
                        && source[i - 1] == target[targetStart + j - 2]
                        && source[i - 2] == target[targetStart + j - 1])
                    {
                        distance = Math.Min(distance, previousPrevious[j - 2] + 1);
                    }

                    current[j] = distance;
                    rowMinimum = Math.Min(rowMinimum, distance);
                }

                LastSuggestDistanceCells += targetLength;
                if (rowMinimum > maxDistance)
                {
                    return false;
                }

                var swap = previousPrevious;
                previousPrevious = previous;
                previous = current;
                current = swap;
            }

            distances = previous;
            return true;
        }

        private void EnsureDistanceRowCapacity(int length)
        {
            if (_distanceRowA.Length >= length)
            {
                return;
            }

            _distanceRowA = new int[length];
            _distanceRowB = new int[length];
            _distanceRowC = new int[length];
        }

        private static bool IsSuggestionWordStart(AssetRecord record, int index)
        {
            if (index <= 0)
            {
                return index == 0;
            }

            var name = record.NormalizedName;
            if (index >= name.Length || !char.IsLetterOrDigit(name[index]))
            {
                return false;
            }

            if (!char.IsLetterOrDigit(name[index - 1]))
            {
                return true;
            }

            var original = record.Name;
            return original.Length == name.Length
                && ((char.IsLower(original[index - 1]) && char.IsUpper(original[index]))
                    || (char.IsLetter(original[index - 1]) && char.IsDigit(original[index]))
                    || (char.IsDigit(original[index - 1]) && char.IsLetter(original[index])));
        }

        private static int FindSuggestionWordEnd(AssetRecord record, int start)
        {
            for (var i = start + 1; i < record.NormalizedName.Length; i++)
            {
                if (!char.IsLetterOrDigit(record.NormalizedName[i])
                    || IsSuggestionWordStart(record, i))
                {
                    return i;
                }
            }

            return record.NormalizedName.Length;
        }

        private void ConsiderSuggestion(SuggestionCandidate candidate, int limit)
        {
            var top = _reusableTopSuggestions;
            for (var i = 0; i < top.Count; i++)
            {
                if (top[i].Ordinal != candidate.Ordinal)
                {
                    continue;
                }

                if (CompareSuggestionCandidates(candidate, top[i]) >= 0)
                {
                    return;
                }

                top.RemoveAt(i);
                break;
            }

            var insertIndex = 0;
            while (insertIndex < top.Count
                && CompareSuggestionCandidates(top[insertIndex], candidate) <= 0)
            {
                insertIndex++;
            }

            if (insertIndex >= limit)
            {
                return;
            }

            top.Insert(insertIndex, candidate);
            if (top.Count > limit)
            {
                top.RemoveAt(top.Count - 1);
            }
        }

        private int CompareSuggestionCandidates(
            SuggestionCandidate left,
            SuggestionCandidate right)
        {
            var byScore = left.Score.CompareTo(right.Score);
            return byScore != 0
                ? byScore
                : CompareRecords(
                    _recordsByOrdinal[left.Ordinal],
                    _recordsByOrdinal[right.Ordinal]);
        }

        // The query shape that dominates typing: one unqualified partial term and
        // nothing else. Its verification can read the concatenated any-field text in a
        // single IndexOf (see the Search result loop), and at one or two characters
        // the short-gram posting list is already the exact answer.
        private static bool IsSoleUnqualifiedPartialTerm(
            ParsedSearchQuery query,
            out string value)
        {
            value = null;
            if (query.RequiredTerms.Count != 1
                || query.TypeTerms.Count != 0
                || query.LabelTerms.Count != 0
                || query.ExcludedTerms.Count != 0)
            {
                return false;
            }

            var token = query.RequiredTerms[0];
            if (token.Field != SearchField.Any
                || token.MatchMode != SearchMatchMode.Partial
                || token.Value.Length == 0)
            {
                return false;
            }

            value = token.Value;
            return true;
        }

        private static bool IsAuthoritativeShortAnyPosting(
            ParsedSearchQuery query)
        {
            return IsSoleUnqualifiedPartialTerm(query, out var value)
                && value.Length <= 2;
        }

        private void EnsureSearchIndexIsCurrent()
        {
            if (_indexedVersion == _index.RecordVersion)
            {
                return;
            }

            RebuildSearchIndex();
        }

        private void RebuildSearchIndex()
        {
            ZepPerfLog.Count(ZepPerfCounter.SearchFullBuilds);
            using var measurement = ZepPerfLog.Measure(
                "search-index",
                ZepPerfStage.SearchWarmBuild);
            var __sw = System.Diagnostics.Stopwatch.StartNew();
            // Result views retain the record generation they were published with.
            // Replace this list instead of clearing it so a catalog compaction or
            // explicit rebuild cannot mutate an already-bound UI collection.
            _recordsByOrdinal = new List<AssetRecord>(_index.Count);
            _anyFieldGramIndex.Clear();
            _anyFieldOneGramIndex.Clear();
            _anyFieldTwoGramIndex.Clear();
            _nameGramIndex.Clear();
            _nameFieldOneGramIndex.Clear();
            _nameFieldTwoGramIndex.Clear();
            _pathGramIndex.Clear();
            _typeGramIndex.Clear();
            _tagGramIndex.Clear();
            _labelGramIndex.Clear();
            _guidGramIndex.Clear();
            _ordinalsByTypeSearchText.Clear();
            _labelFieldOneGramIndex.Clear();
            _labelFieldTwoGramIndex.Clear();
            _guidToOrdinal.Clear();
            _tombstones.Clear();
            _alphabeticalOrdinals.Clear();

            var records = new List<AssetRecord>(_index.Enumerate());
            records.Sort(static (a, b) =>
            {
                var cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : string.Compare(a.AssetPath, b.AssetPath, StringComparison.OrdinalIgnoreCase);
            });
            var __ms_sort = __sw.Elapsed.TotalMilliseconds;

            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                var ordinal = _recordsByOrdinal.Count;
                _recordsByOrdinal.Add(record);
                _guidToOrdinal[record.Guid] = ordinal;
                IndexField(_anyFieldGramIndex, record.NormalizedSearchText, ordinal);
                IndexAnyFieldShortGrams(record.NormalizedSearchText, ordinal);
                IndexField(_nameGramIndex, record.NormalizedName, ordinal);
                IndexFieldShortGrams(
                    _nameFieldOneGramIndex,
                    _nameFieldTwoGramIndex,
                    record.NormalizedName,
                    ordinal);
                IndexField(_pathGramIndex, record.NormalizedAssetPath, ordinal);
                IndexField(_typeGramIndex, record.NormalizedTypeSearchText, ordinal);
                IndexTypeSearchText(record.NormalizedTypeSearchText, ordinal);
                IndexField(_tagGramIndex, record.NormalizedTagSearchText, ordinal);
                IndexField(_labelGramIndex, record.NormalizedUnityLabelSearchText, ordinal);
                IndexFieldShortGrams(
                    _labelFieldOneGramIndex,
                    _labelFieldTwoGramIndex,
                    record.NormalizedUnityLabelSearchText,
                    ordinal);
                IndexField(
                    _guidGramIndex,
                    record.NormalizedGuid,
                    ordinal,
                    SearchQuery.GuidIndexedPrefixLength);
                _alphabeticalOrdinals.Add(ordinal);
            }

            _candidateGenerationByOrdinal = new int[_recordsByOrdinal.Count];
            _suggestionCandidateHitsByOrdinal = new byte[_recordsByOrdinal.Count];
            _candidateGeneration = 0;
            _alphabeticalOrdinalsDirty = false;
            _allOrdinalsDirty = true;
            _indexedVersion = _index.RecordVersion;
            _hasBuiltIndex = true;
            _recordsAlphabeticallyOrdered = true;
            // A full build has no retired postings left to reclaim.
            _compactionRequested = false;
            __sw.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] AssetSearchService.RebuildSearchIndex records={records.Count} anyGrams={_anyFieldGramIndex.Count} oneGrams={_anyFieldOneGramIndex.Count} twoGrams={_anyFieldTwoGramIndex.Count} nameGrams={_nameGramIndex.Count} pathGrams={_pathGramIndex.Count} sortMs={__ms_sort:F2} ms={__sw.Elapsed.TotalMilliseconds:F2}");
        }

        private int[] GetAllOrdinals()
        {
            if (!_allOrdinalsDirty)
            {
                return _allOrdinals;
            }

            if (_tombstones.Count == 0)
            {
                var recordCount = _recordsByOrdinal.Count;
                if (_allOrdinals.Length == recordCount)
                {
                    if (!_allOrdinalsAreIdentity)
                    {
                        for (var i = 0; i < recordCount; i++)
                        {
                            _allOrdinals[i] = i;
                        }
                    }
                }
                else if (_allOrdinalsAreIdentity && _allOrdinals.Length < recordCount)
                {
                    // The ordinary case on an import: every existing entry is
                    // already its own index, so only the appended tail is new.
                    var appendFrom = _allOrdinals.Length;
                    Array.Resize(ref _allOrdinals, recordCount);
                    for (var i = appendFrom; i < recordCount; i++)
                    {
                        _allOrdinals[i] = i;
                    }
                }
                else
                {
                    _allOrdinals = new int[recordCount];
                    for (var i = 0; i < recordCount; i++)
                    {
                        _allOrdinals[i] = i;
                    }
                }

                _allOrdinalsAreIdentity = true;
            }
            else
            {
                var liveCount = _recordsByOrdinal.Count - _tombstones.Count;
                if (_allOrdinals.Length != liveCount)
                {
                    _allOrdinals = new int[liveCount];
                }

                var writeIndex = 0;
                for (var i = 0; i < _recordsByOrdinal.Count; i++)
                {
                    if (!_tombstones.Contains(i))
                    {
                        _allOrdinals[writeIndex++] = i;
                    }
                }

                // A retired ordinal shifts every later entry off its index, so
                // the next no-tombstone pass cannot trust the prefix.
                _allOrdinalsAreIdentity = false;
            }

            _allOrdinalsDirty = false;
            return _allOrdinals;
        }

        private void CompactIfNeeded()
        {
            if (_recordsByOrdinal.Count == 0
                || (float)_tombstones.Count / _recordsByOrdinal.Count < CompactionThreshold)
            {
                return;
            }

            _compactionRequested = true;
        }

        // True while retired postings are worth reclaiming. The controller polls
        // this after a catalog change and answers it with a worker build; a lost
        // build race simply leaves it set, and the next delta over the threshold
        // raises it again.
        internal bool IsCompactionRequested => _compactionRequested;

        internal void ClearCompactionRequest()
        {
            _compactionRequested = false;
        }

        // Retired ordinals still carried by the postings. Exposed so a test can
        // prove the worker build actually reclaimed them.
        internal int TombstoneCount => _tombstones.Count;

        private static bool IsCorpusWideScope(string normalizedFolderScopePath)
        {
            return string.Equals(normalizedFolderScopePath, "assets", StringComparison.Ordinal)
                || string.Equals(normalizedFolderScopePath, "packages", StringComparison.Ordinal);
        }

        private void EnsureCandidateGenerationCapacity()
        {
            if (_candidateGenerationByOrdinal.Length >= _recordsByOrdinal.Count)
            {
                return;
            }

            var doubledCapacity = _candidateGenerationByOrdinal.Length == 0
                ? 4
                : _candidateGenerationByOrdinal.Length * 2;
            Array.Resize(
                ref _candidateGenerationByOrdinal,
                Math.Max(_recordsByOrdinal.Count, doubledCapacity));
            Array.Resize(
                ref _suggestionCandidateHitsByOrdinal,
                _candidateGenerationByOrdinal.Length);
        }

        // A catalog delta only ever appends a contiguous run of ordinals and tombstones
        // ordinals that are already placed, so the maintained order can absorb it in one
        // pass: sort the appended run (k log k) and merge it in while dropping tombstoned
        // entries from both sides. Without this the delta left _alphabeticalOrdinalsDirty
        // set and the next broad query paid a full-corpus re-sort — measured at 100k
        // records, one upsert turned the same "player" query from 0.49 ms into 36.4 ms,
        // and the next delta re-armed it (audit §3.10).
        private void MergeAppendedOrdinalsIntoAlphabeticalOrder(int firstAppendedOrdinal)
        {
            if (!_alphabeticalOrdinalsDirty)
            {
                return;
            }

            var appended = _reusableDeltaAppendedOrdinals;
            appended.Clear();
            var hasTombstones = _tombstones.Count > 0;
            for (var ordinal = firstAppendedOrdinal; ordinal < _recordsByOrdinal.Count; ordinal++)
            {
                // A second upsert of the same GUID inside one delta retires the ordinal
                // the first one just appended, so the appended run is not all live.
                if (!hasTombstones || !_tombstones.Contains(ordinal))
                {
                    appended.Add(ordinal);
                }
            }

            appended.Sort((left, right) =>
                CompareRecords(_recordsByOrdinal[left], _recordsByOrdinal[right]));

            var merged = _alphabeticalMergeBuffer;
            merged.Clear();
            var wanted = _alphabeticalOrdinals.Count + appended.Count;
            if (merged.Capacity < wanted)
            {
                merged.Capacity = wanted;
            }

            var placedIndex = 0;
            for (var appendedIndex = 0; appendedIndex < appended.Count; appendedIndex++)
            {
                var appendedOrdinal = appended[appendedIndex];
                var insertionIndex = FindAlphabeticalInsertionIndex(
                    _recordsByOrdinal[appendedOrdinal],
                    placedIndex);
                for (; placedIndex < insertionIndex; placedIndex++)
                {
                    var placedOrdinal = _alphabeticalOrdinals[placedIndex];
                    if (!hasTombstones || !_tombstones.Contains(placedOrdinal))
                    {
                        merged.Add(placedOrdinal);
                    }
                }

                merged.Add(appendedOrdinal);
            }

            for (; placedIndex < _alphabeticalOrdinals.Count; placedIndex++)
            {
                var placedOrdinal = _alphabeticalOrdinals[placedIndex];
                if (!hasTombstones || !_tombstones.Contains(placedOrdinal))
                {
                    merged.Add(placedOrdinal);
                }
            }

            _alphabeticalMergeBuffer = _alphabeticalOrdinals;
            _alphabeticalOrdinals = merged;
            _alphabeticalOrdinalsDirty = false;
        }

        // First index at or after `fromIndex` whose placed record does not sort before
        // `record`. Binary rather than a scan because the merge above is dominated by
        // this decision: an ordinary one-asset delta would otherwise pay one
        // OrdinalIgnoreCase compare per live record (2.6 ms at 100k, ~13 ms at 500k)
        // to find a single insertion point, on every save whether or not anyone searches.
        private int FindAlphabeticalInsertionIndex(AssetRecord record, int fromIndex)
        {
            var low = fromIndex;
            var high = _alphabeticalOrdinals.Count;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (CompareRecords(_recordsByOrdinal[_alphabeticalOrdinals[middle]], record) < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        private void EnsureAlphabeticalOrdinalsAreCurrent()
        {
            if (!_alphabeticalOrdinalsDirty)
            {
                return;
            }

            ZepPerfLog.Count(ZepPerfCounter.SearchAlphabeticalResorts);
            _alphabeticalOrdinals.Clear();
            for (var ordinal = 0; ordinal < _recordsByOrdinal.Count; ordinal++)
            {
                if (!_tombstones.Contains(ordinal))
                {
                    _alphabeticalOrdinals.Add(ordinal);
                }
            }

            _alphabeticalOrdinals.Sort((left, right) =>
                CompareRecords(_recordsByOrdinal[left], _recordsByOrdinal[right]));
            _alphabeticalOrdinalsDirty = false;
        }

        private IReadOnlyList<int> OrderCandidatesAlphabetically(IReadOnlyList<int> candidates)
        {
            EnsureAlphabeticalOrdinalsAreCurrent();
            if (_candidateGeneration == int.MaxValue)
            {
                Array.Clear(_candidateGenerationByOrdinal, 0, _candidateGenerationByOrdinal.Length);
                _candidateGeneration = 0;
            }

            var generation = ++_candidateGeneration;
            for (var i = 0; i < candidates.Count; i++)
            {
                _candidateGenerationByOrdinal[candidates[i]] = generation;
            }

            _reusableAlphabeticalCandidates.Clear();
            for (var i = 0; i < _alphabeticalOrdinals.Count; i++)
            {
                var ordinal = _alphabeticalOrdinals[i];
                if (_candidateGenerationByOrdinal[ordinal] == generation)
                {
                    _reusableAlphabeticalCandidates.Add(ordinal);
                }
            }

            return _reusableAlphabeticalCandidates;
        }

        // TryGetCandidates with a single-slot memo in front. Only used for the
        // folder-scope and type-filter values, which repeat identically across
        // keystrokes.
        private bool TryGetFilterCandidates(
            SearchField field,
            string normalizedValue,
            FilterCandidateMemo memo,
            out IReadOnlyList<int> candidates)
        {
            if (memo.Version == _indexedVersion
                && string.Equals(memo.Value, normalizedValue, StringComparison.Ordinal))
            {
                candidates = memo.Candidates;
                return memo.Found;
            }

            memo.Found = TryGetCandidates(SearchToken.CreateNormalized(field, normalizedValue), out candidates);
            memo.Value = normalizedValue;
            memo.Version = _indexedVersion;
            memo.Candidates = candidates;
            return memo.Found;
        }

        private bool TryGetGroupCandidates(
            IReadOnlyList<SearchToken> terms,
            List<int> destination,
            out IReadOnlyList<int> candidates)
        {
            // The overwhelmingly common Unity query has one type or label value
            // (`t:prefab`, `l:environment`). Avoid materializing an OR union for
            // that case; the posting list already is the complete candidate set.
            if (terms.Count == 1)
            {
                return TryGetCandidates(terms[0], out candidates);
            }

            var union = _reusableGroupUnion;
            union.Clear();
            for (var i = 0; i < terms.Count; i++)
            {
                if (!TryGetCandidates(terms[i], out var termCandidates))
                {
                    // Positive t:/l: values are alternatives. One missing branch does
                    // not invalidate another branch that has matches.
                    continue;
                }

                for (var candidateIndex = 0; candidateIndex < termCandidates.Count; candidateIndex++)
                {
                    union.Add(termCandidates[candidateIndex]);
                }
            }

            if (union.Count == 0)
            {
                candidates = Array.Empty<int>();
                return false;
            }

            destination.Clear();
            foreach (var ordinal in union)
            {
                destination.Add(ordinal);
            }

            destination.Sort();
            candidates = destination;
            return true;
        }

        // Returns a SUPERSET of the matching ordinals (or false when the token
        // provably matches nothing). Callers never need to know whether the
        // candidate set is exact: Search re-verifies every candidate through
        // SearchQuery.Matches before it reaches the result list.
        private bool TryGetCandidates(SearchToken token, out IReadOnlyList<int> candidates)
        {
            if (token.MatchMode == SearchMatchMode.Glob)
            {
                return TryGetGlobCandidates(token.GlobPatterns, out candidates);
            }

            if (string.IsNullOrEmpty(token.Value))
            {
                candidates = GetAllOrdinals();
                return true;
            }

            var value = token.Value;

            if (token.Field == SearchField.Area)
            {
                candidates = GetAllOrdinals();
                return candidates.Count > 0;
            }

            // Fast path for any-field short queries: the multi-arity indices give
            // an exact posting list for 1- and 2-char queries with a single
            // dictionary lookup. No verify step required because the gram IS the
            // query (length <= GramLength).
            if (token.Field == SearchField.Any)
            {
                if (value.Length == 1)
                {
                    if (_anyFieldOneGramIndex.TryGetValue(value[0], out var postings))
                    {
                        candidates = postings;
                        return postings.Count > 0;
                    }
                    candidates = Array.Empty<int>();
                    return false;
                }

                if (value.Length == 2)
                {
                    if (_anyFieldTwoGramIndex.TryGetValue(Pack2Gram(value, 0), out var postings))
                    {
                        candidates = postings;
                        return postings.Count > 0;
                    }
                    candidates = Array.Empty<int>();
                    return false;
                }
            }

            if (token.Field == SearchField.Type && value.Length < GramLength)
            {
                candidates = GetShortTypeCandidates(value);
                return candidates.Count > 0;
            }

            if (TryGetFieldShortGramCandidates(token.Field, value, out candidates))
            {
                return candidates.Count > 0;
            }

            if (value.Length < GramLength)
            {
                // Field-scoped sub-gram queries (rare). Fall back to the full live
                // set; the caller's Matches() filter will catch false positives.
                candidates = GetAllOrdinals();
                return true;
            }

            // An unqualified term can still name a record by the head of its GUID,
            // but the GUID is no longer part of the any-field text (see
            // AssetRecord.BuildNormalizedSearchText), so those candidates have to come
            // from the GUID gram index and be unioned in. IsPossibleUnqualifiedGuidPrefix
            // keeps this off the ordinary typing path: only an all-hex term of at least
            // four characters can prefix a GUID, so "player" never pays for it.
            if (token.Field == SearchField.Any
                && SearchQuery.IsPossibleUnqualifiedGuidPrefix(value))
            {
                var hasAnyFieldCandidates = TryGetGramCandidates(
                    _anyFieldGramIndex,
                    value,
                    out var anyFieldCandidates);
                var hasGuidCandidates = TryGetGramCandidates(
                    _guidGramIndex,
                    value,
                    out var guidCandidates,
                    SearchQuery.GuidIndexedPrefixLength);
                if (!hasGuidCandidates)
                {
                    candidates = hasAnyFieldCandidates ? anyFieldCandidates : Array.Empty<int>();
                    return candidates.Count > 0;
                }

                if (!hasAnyFieldCandidates)
                {
                    candidates = guidCandidates;
                    return candidates.Count > 0;
                }

                candidates = UnionSortedCandidates(anyFieldCandidates, guidCandidates);
                return candidates.Count > 0;
            }

            // A `guid:` value longer than the indexed head has to be gram-resolved
            // from that head only; the result loop still verifies it against the whole
            // GUID (MatchesToken's SearchField.Guid arm), so a longer prefix stays exact.
            return TryGetGramCandidates(
                    GetGramIndex(token.Field),
                    value,
                    out candidates,
                    token.Field == SearchField.Guid
                        ? SearchQuery.GuidIndexedPrefixLength
                        : int.MaxValue)
                && candidates.Count > 0;
        }

        // Returns a superset of the ordinals whose indexed text contains every gram of
        // `value`, or false when one gram has no posting list at all (which proves the
        // value cannot occur). Shared scratch buffers are consumed inside the call, so
        // two consecutive calls may be compared against each other.
        private bool TryGetGramCandidates(
            Dictionary<ulong, List<int>> index,
            string value,
            out IReadOnlyList<int> candidates,
            int maxGramSourceLength = int.MaxValue)
        {
            BuildQueryGrams(value, _reusableQueryGrams, maxGramSourceLength);
            if (_reusableQueryGrams.Count == 0)
            {
                candidates = GetAllOrdinals();
                return true;
            }

            var gramCandidates = _reusableGramCandidates;
            gramCandidates.Clear();
            for (var i = 0; i < _reusableQueryGrams.Count; i++)
            {
                if (!index.TryGetValue(_reusableQueryGrams[i], out var postingList))
                {
                    candidates = Array.Empty<int>();
                    return false;
                }

                gramCandidates.Add(postingList);
            }

            if (gramCandidates.Count == 1)
            {
                candidates = gramCandidates[0];
                return true;
            }

            candidates = IntersectCandidateSources(gramCandidates, _reusableOrderedSources);
            return true;
        }

        // Both inputs are ascending posting order, so the union is one merge pass.
        // Only the rare all-hex unqualified term reaches this, so the fresh list is
        // not on any typing path worth pooling for.
        private static IReadOnlyList<int> UnionSortedCandidates(
            IReadOnlyList<int> left,
            IReadOnlyList<int> right)
        {
            var merged = new List<int>(left.Count + right.Count);
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Count && rightIndex < right.Count)
            {
                var leftValue = left[leftIndex];
                var rightValue = right[rightIndex];
                if (leftValue == rightValue)
                {
                    merged.Add(leftValue);
                    leftIndex++;
                    rightIndex++;
                    continue;
                }

                if (leftValue < rightValue)
                {
                    merged.Add(leftValue);
                    leftIndex++;
                    continue;
                }

                merged.Add(rightValue);
                rightIndex++;
            }

            while (leftIndex < left.Count)
            {
                merged.Add(left[leftIndex++]);
            }

            while (rightIndex < right.Count)
            {
                merged.Add(right[rightIndex++]);
            }

            return merged;
        }

        private bool TryGetGlobCandidates(
            IReadOnlyList<string> patterns,
            out IReadOnlyList<int> candidates)
        {
            if (patterns == null || patterns.Count == 0)
            {
                candidates = Array.Empty<int>();
                return false;
            }

            if (patterns.Count == 1)
            {
                var literal = GlobMatcher.GetLongestGlobLiteral(patterns[0]);
                if (literal.Length < GramLength)
                {
                    candidates = TooBroadGlobCandidates;
                    return false;
                }

                return TryGetCandidates(
                    SearchToken.CreateNormalized(SearchField.Path, literal),
                    out candidates);
            }

            // Brace expansion produces OR alternatives. Candidate each alternative
            // from its longest required literal, then union the postings; the full glob
            // matcher still verifies every record, so this can only remove impossible
            // paths. An alternative with no usable literal makes the WHOLE token too
            // broad: its own matches cannot be candidated, and widening the union to
            // the corpus is the cost this exists to avoid.
            var union = new HashSet<int>();
            for (var patternIndex = 0; patternIndex < patterns.Count; patternIndex++)
            {
                var literal = GlobMatcher.GetLongestGlobLiteral(patterns[patternIndex]);
                if (literal.Length < GramLength)
                {
                    candidates = TooBroadGlobCandidates;
                    return false;
                }

                if (!TryGetCandidates(
                        SearchToken.CreateNormalized(SearchField.Path, literal),
                        out var patternCandidates))
                {
                    continue;
                }

                for (var candidateIndex = 0; candidateIndex < patternCandidates.Count; candidateIndex++)
                {
                    union.Add(patternCandidates[candidateIndex]);
                }
            }

            if (union.Count == 0)
            {
                candidates = Array.Empty<int>();
                return false;
            }

            var merged = new List<int>(union);
            merged.Sort();
            candidates = merged;
            return true;
        }

        // A glob whose longest literal run is shorter than a trigram has no posting
        // list to start from. It used to fall back to the whole corpus and run
        // GlobMatcher's DP over every record - `glob:"**/*a*"` is ~180M cells at 500k,
        // on a keystroke. It is answered as "matches nothing" instead: the pattern is
        // too broad to answer from the index, and saying so beats freezing the panel.
        // See the glob line in Documentation~/overview.md.
        //
        // Only the LONGEST run is used, not all runs of at least GramLength. Every run
        // must occur in a matching path, so any of them is a valid superset and the
        // plan's "union all literal runs" would only WIDEN the set - but even ranking
        // them by candidate count costs a full posting merge per run, which the perf
        // harness measured at 100k: `glob:Assets/**/Player_05000.prefab` went from
        // 0.094 ms / 1.9 KB to 0.772 ms / 393 KB per call, because resolving the
        // "Assets/" run alone merges the whole corpus. The longest run is the most
        // selective one that can be identified for free, and if IT has no candidates
        // then no path contains it and the glob matches nothing anyway.
        private static readonly IReadOnlyList<int> TooBroadGlobCandidates = Array.Empty<int>();

        private Dictionary<ulong, List<int>> GetGramIndex(SearchField field)
        {
            switch (field)
            {
                case SearchField.Name:
                    return _nameGramIndex;
                case SearchField.Path:
                    return _pathGramIndex;
                case SearchField.Type:
                    return _typeGramIndex;
                case SearchField.Tag:
                    return _tagGramIndex;
                case SearchField.Label:
                    return _labelGramIndex;
                case SearchField.Guid:
                    return _guidGramIndex;
                case SearchField.Extension:
                case SearchField.Directory:
                case SearchField.Glob:
                    return _pathGramIndex;
                default:
                    return _anyFieldGramIndex;
            }
        }

        private bool TryGetFieldShortGramCandidates(
            SearchField field,
            string value,
            out IReadOnlyList<int> candidates)
        {
            Dictionary<char, List<int>> oneGramIndex;
            Dictionary<uint, List<int>> twoGramIndex;
            switch (field)
            {
                case SearchField.Name:
                    oneGramIndex = _nameFieldOneGramIndex;
                    twoGramIndex = _nameFieldTwoGramIndex;
                    break;
                case SearchField.Label:
                    oneGramIndex = _labelFieldOneGramIndex;
                    twoGramIndex = _labelFieldTwoGramIndex;
                    break;
                default:
                    candidates = null;
                    return false;
            }

            if (value.Length == 1)
            {
                candidates = oneGramIndex.TryGetValue(value[0], out var postings)
                    ? postings
                    : Array.Empty<int>();
                return true;
            }

            if (value.Length == 2)
            {
                candidates = twoGramIndex.TryGetValue(Pack2Gram(value, 0), out var postings)
                    ? postings
                    : Array.Empty<int>();
                return true;
            }

            candidates = null;
            return false;
        }

        private void IndexTypeSearchText(string typeSearchText, int ordinal)
        {
            if (string.IsNullOrEmpty(typeSearchText))
            {
                return;
            }

            if (!_ordinalsByTypeSearchText.TryGetValue(typeSearchText, out var ordinals))
            {
                ordinals = new List<int>();
                _ordinalsByTypeSearchText[typeSearchText] = ordinals;
            }

            ordinals.Add(ordinal);
        }

        // A 1- or 2-character `t:` cannot use the trigram index. The distinct type
        // search texts are two to three orders of magnitude fewer than the records, so
        // scanning them and unioning the matching postings answers the query exactly —
        // the same candidate set the removed one/two-gram type indices produced — for
        // one int of standing memory per record instead of ~95 postings per record.
        // The lists are disjoint (one type search text per record) and each is already
        // ascending, so a concatenate-and-sort restores the ascending candidate order
        // every caller downstream assumes. A fresh list, not a reusable one: the pane's
        // type filter memoizes the candidate list it is handed across Search calls.
        private IReadOnlyList<int> GetShortTypeCandidates(string value)
        {
            List<int> onlyMatch = null;
            var matchCount = 0;
            var totalOrdinals = 0;
            foreach (var pair in _ordinalsByTypeSearchText)
            {
                if (pair.Key.IndexOf(value, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                matchCount++;
                onlyMatch = pair.Value;
                totalOrdinals += pair.Value.Count;
            }

            if (matchCount == 0)
            {
                return Array.Empty<int>();
            }

            // One matching type text is the ordinary shape, and its posting list already
            // IS the exact ascending candidate set — hand it over the way the gram legs
            // hand over a single posting list, with no copy and no sort.
            if (matchCount == 1)
            {
                return onlyMatch;
            }

            var merged = new int[totalOrdinals];
            var written = 0;
            foreach (var pair in _ordinalsByTypeSearchText)
            {
                if (pair.Key.IndexOf(value, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                pair.Value.CopyTo(merged, written);
                written += pair.Value.Count;
            }

            Array.Sort(merged);
            return merged;
        }

        private void IndexField(
            Dictionary<ulong, List<int>> index,
            string value,
            int ordinal,
            int maxIndexedLength = int.MaxValue)
        {
            if (string.IsNullOrEmpty(value) || value.Length < GramLength)
            {
                return;
            }

            _reusableGramSet.Clear();
            var indexedLength = value.Length < maxIndexedLength
                ? value.Length
                : maxIndexedLength;
            var end = indexedLength - GramLength;
            for (var i = 0; i <= end; i++)
            {
                var gram = PackGram(value, i);
                if (!_reusableGramSet.Add(gram))
                {
                    continue;
                }

                if (!index.TryGetValue(gram, out var postingList))
                {
                    postingList = new List<int>();
                    index[gram] = postingList;
                }

                postingList.Add(ordinal);
            }
        }

        // Any-field wrapper for the shared short-gram builder. Name uses the
        // same builder for suggestion candidates; type and label use it for
        // their short qualified-query paths.
        private void IndexAnyFieldShortGrams(string value, int ordinal)
        {
            IndexFieldShortGrams(
                _anyFieldOneGramIndex,
                _anyFieldTwoGramIndex,
                value,
                ordinal);
        }

        private void IndexFieldShortGrams(
            Dictionary<char, List<int>> oneGramIndex,
            Dictionary<uint, List<int>> twoGramIndex,
            string value,
            int ordinal)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            _reusableOneGramSet.Clear();
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (!_reusableOneGramSet.Add(c))
                {
                    continue;
                }

                if (!oneGramIndex.TryGetValue(c, out var postingList))
                {
                    postingList = new List<int>();
                    oneGramIndex[c] = postingList;
                }
                postingList.Add(ordinal);
            }

            if (value.Length < 2)
            {
                return;
            }

            _reusableTwoGramSet.Clear();
            var end = value.Length - 2;
            for (var i = 0; i <= end; i++)
            {
                var gram = Pack2Gram(value, i);
                if (!_reusableTwoGramSet.Add(gram))
                {
                    continue;
                }

                if (!twoGramIndex.TryGetValue(gram, out var postingList))
                {
                    postingList = new List<int>();
                    twoGramIndex[gram] = postingList;
                }
                postingList.Add(ordinal);
            }
        }

        private static uint Pack2Gram(string value, int startIndex)
        {
            // Two UTF-16 code units packed into a uint. Same encoding shape as
            // PackGram, just one cell narrower. No allocation per lookup.
            return ((uint)value[startIndex] << 16) | value[startIndex + 1];
        }

        private void BuildQueryGrams(
            string value,
            List<ulong> buffer,
            int maxGramSourceLength = int.MaxValue)
        {
            buffer.Clear();
            if (string.IsNullOrEmpty(value) || value.Length < GramLength)
            {
                return;
            }

            _reusableGramSet.Clear();
            var sourceLength = value.Length < maxGramSourceLength
                ? value.Length
                : maxGramSourceLength;
            var end = sourceLength - GramLength;
            for (var i = 0; i <= end; i++)
            {
                var gram = PackGram(value, i);
                if (_reusableGramSet.Add(gram))
                {
                    buffer.Add(gram);
                }
            }
        }

        private static ulong PackGram(string value, int startIndex)
        {
            // Pack three UTF-16 code units into a single ulong (48 bits used).
            // Avoids allocating a substring for each dictionary key/lookup.
            return ((ulong)value[startIndex] << 32)
                 | ((ulong)value[startIndex + 1] << 16)
                 | value[startIndex + 2];
        }

        private static IReadOnlyList<int> IntersectCandidateSources(
            IReadOnlyList<IReadOnlyList<int>> sources,
            List<IReadOnlyList<int>> orderedBuffer)
        {
            if (sources.Count == 0)
            {
                return Array.Empty<int>();
            }

            if (sources.Count == 1)
            {
                return sources[0];
            }

            var orderedSources = orderedBuffer;
            orderedSources.Clear();
            for (var i = 0; i < sources.Count; i++)
            {
                orderedSources.Add(sources[i]);
            }

            orderedSources.Sort(static (left, right) => left.Count.CompareTo(right.Count));

            var intersection = CopyOrdinals(orderedSources[0]);
            var intersectionCount = intersection.Length;
            for (var i = 1; i < orderedSources.Count && intersectionCount > 0; i++)
            {
                intersectionCount = IntersectIntoBuffer(intersection, intersectionCount, orderedSources[i]);
            }

            if (intersectionCount == 0)
            {
                return Array.Empty<int>();
            }

            if (intersectionCount == intersection.Length)
            {
                return intersection;
            }

            var trimmed = new int[intersectionCount];
            Array.Copy(intersection, trimmed, intersectionCount);
            return trimmed;
        }

        private static int[] CopyOrdinals(IReadOnlyList<int> ordinals)
        {
            var copy = new int[ordinals.Count];
            for (var i = 0; i < ordinals.Count; i++)
            {
                copy[i] = ordinals[i];
            }

            return copy;
        }

        private static int IntersectIntoBuffer(int[] left, int leftCount, IReadOnlyList<int> right)
        {
            var leftIndex = 0;
            var rightIndex = 0;
            var writeIndex = 0;

            while (leftIndex < leftCount && rightIndex < right.Count)
            {
                var leftValue = left[leftIndex];
                var rightValue = right[rightIndex];
                if (leftValue == rightValue)
                {
                    left[writeIndex++] = leftValue;
                    leftIndex++;
                    rightIndex++;
                    continue;
                }

                if (leftValue < rightValue)
                {
                    leftIndex++;
                    continue;
                }

                rightIndex++;
            }

            return writeIndex;
        }

        private static bool MatchesFolderScope(string normalizedAssetPath, string normalizedFolderScopePath)
        {
            if (string.IsNullOrEmpty(normalizedFolderScopePath))
            {
                return true;
            }

            return string.Equals(normalizedAssetPath, normalizedFolderScopePath, StringComparison.Ordinal)
                || (normalizedAssetPath.StartsWith(normalizedFolderScopePath, StringComparison.Ordinal)
                    && normalizedAssetPath.Length > normalizedFolderScopePath.Length
                    && normalizedAssetPath[normalizedFolderScopePath.Length] == '/');
        }

        private static bool MatchesTypeName(string normalizedTypeName, string normalizedExpectedTypeName)
        {
            return string.IsNullOrEmpty(normalizedExpectedTypeName)
                || string.Equals(normalizedTypeName, normalizedExpectedTypeName, StringComparison.Ordinal);
        }

        private static int CompareRecords(AssetRecord left, AssetRecord right)
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName
                : string.Compare(left.AssetPath, right.AssetPath, StringComparison.OrdinalIgnoreCase);
        }

        private readonly struct SuggestionCandidate
        {
            internal SuggestionCandidate(int ordinal, int score, bool isFuzzy)
            {
                Ordinal = ordinal;
                Score = score;
                IsFuzzy = isFuzzy;
            }

            internal int Ordinal { get; }

            internal int Score { get; }

            internal bool IsFuzzy { get; }
        }

        private sealed class FilterCandidateMemo
        {
            public string Value;
            public int Version = -1;
            public bool Found;
            public IReadOnlyList<int> Candidates;
        }

        private sealed class OrdinalResultList : IReadOnlyList<AssetRecord>
        {
            private readonly IReadOnlyList<AssetRecord> _records;
            private readonly int[] _ordinals;
            private readonly int _count;

            internal OrdinalResultList(
                IReadOnlyList<AssetRecord> records,
                int[] ordinals,
                int count)
            {
                _records = records;
                _ordinals = ordinals;
                _count = count;
            }

            public int Count => _count;

            public AssetRecord this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)_count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    return _records[_ordinals[index]];
                }
            }

            public IEnumerator<AssetRecord> GetEnumerator()
            {
                for (var i = 0; i < _count; i++)
                {
                    yield return _records[_ordinals[i]];
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
                => GetEnumerator();
        }

        private sealed class OrdinalComparer : IComparer<int>
        {
            private readonly IReadOnlyList<AssetRecord> _records;

            internal OrdinalComparer(IReadOnlyList<AssetRecord> records)
            {
                _records = records;
            }

            public int Compare(int left, int right)
            {
                return CompareRecords(_records[left], _records[right]);
            }
        }
    }

    public static class SearchQuery
    {
        // Candidate postings and matching share this boundary: short fragments
        // read only the indexed head, while longer fragments must prefix the GUID.
        internal const int GuidIndexedPrefixLength = 8;

        public static ParsedSearchQuery Parse(string query)
        {
            var parsed = new ParsedSearchQuery();
            if (string.IsNullOrWhiteSpace(query))
            {
                return parsed;
            }

            var index = 0;
            while (TryReadToken(query, ref index, out var tokenStart))
                ParseToken(query, tokenStart, index, parsed);

            return parsed;
        }

        internal static int NameMatchRank(string name, ParsedSearchQuery query)
        {
            var rank = 0;
            foreach (var term in query.RequiredTerms)
            {
                if (term.Field != SearchField.Any && term.Field != SearchField.Name) continue;
                var at = name.IndexOf(term.Value, StringComparison.OrdinalIgnoreCase);
                var wordStart = at > 0 && (!char.IsLetterOrDigit(name[at - 1])
                    || char.IsLower(name[at - 1]) && char.IsUpper(name[at]));
                var match = string.Equals(name, term.Value, StringComparison.OrdinalIgnoreCase) ? 0
                    : at == 0 ? 1 : wordStart ? 2 : at > 0 ? 3 : 4;
                rank = Math.Max(rank, match);
            }
            return rank;
        }

        // Edit only recognized filter tokens. Retained text is copied verbatim,
        // including quoted phrases, escapes and unknown qualifiers.
        public static string RemoveFilters(string query, SearchField? field = null, string value = null)
        {
            if (string.IsNullOrEmpty(query)) return string.Empty;
            var result = new System.Text.StringBuilder(query.Length);
            var index = 0;
            var copied = 0;
            while (TryReadToken(query, ref index, out var start))
            {
                var fieldStart = start;
                if (query[fieldStart] == '-') fieldStart++;
                if (fieldStart < index && query[fieldStart] == '!') fieldStart++;
                var separator = FindFieldSeparator(query, fieldStart, index);
                if (separator < 0 || !TryResolveField(query, fieldStart, separator, out var tokenField)
                    || (field.HasValue ? tokenField != field.Value : tokenField == SearchField.Name)) continue;
                if (value != null)
                {
                    var single = new ParsedSearchQuery();
                    ParseToken(query, start, index, single);
                    if (single.TypeTerms.Count != 1
                        || !string.Equals(single.TypeTerms[0].Value, NormalizeValue(value), StringComparison.Ordinal)) continue;
                }
                result.Append(query, copied, start - copied);
                copied = index;
            }
            result.Append(query, copied, query.Length - copied);
            return result.ToString().Trim();
        }

        public static string ToggleTypeFilter(string query, string type)
        {
            var parsed = Parse(query);
            foreach (var token in parsed.TypeTerms)
                if (string.Equals(token.Value, NormalizeValue(type), StringComparison.Ordinal))
                    return RemoveFilters(query, SearchField.Type, type);
            var escaped = type.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return ((query ?? string.Empty).TrimEnd() + " t:\"" + escaped + "\"").TrimStart();
        }

        public static IEnumerable<(int Start, int Length, SearchField Field)> FilterSpans(string query)
        {
            if (string.IsNullOrEmpty(query)) yield break;
            var index = 0;
            while (TryReadToken(query, ref index, out var start))
            {
                var fieldStart = start;
                if (query[fieldStart] == '-') fieldStart++;
                if (fieldStart < index && query[fieldStart] == '!') fieldStart++;
                var separator = FindFieldSeparator(query, fieldStart, index);
                if (separator >= 0 && TryResolveField(query, fieldStart, separator, out var field)
                    && field != SearchField.Name)
                    yield return (start, index - start, field);
            }
        }

        private static bool TryReadToken(string query, ref int index, out int start)
        {
            while (index < query.Length && IsTokenSeparator(query[index])) index++;
            start = index;
            if (index >= query.Length) return false;
            var inQuotes = false;
            while (index < query.Length)
            {
                var character = query[index];
                if (character == '"' && !IsEscapedQuote(query, index, inQuotes))
                {
                    inQuotes = !inQuotes;
                    index++;
                    continue;
                }
                if (!inQuotes && IsTokenSeparator(character)) break;
                index++;
            }
            return true;
        }

        internal static bool Matches(AssetRecord record, ParsedSearchQuery query)
        {
            return record != null && Matches(SearchDocument.FromAssetRecord(record), query);
        }

        internal static bool Matches(SearchDocument document, ParsedSearchQuery query)
        {
            if (query == null || query.IsEmpty)
            {
                return true;
            }

            if (query.MatchesNothing)
            {
                return false;
            }

            for (var i = 0; i < query.RequiredTerms.Count; i++)
            {
                if (!MatchesToken(document, query.RequiredTerms[i]))
                {
                    return false;
                }
            }

            if (query.TypeTerms.Count > 0 && !MatchesAny(document, query.TypeTerms))
            {
                return false;
            }

            if (query.LabelTerms.Count > 0 && !MatchesAny(document, query.LabelTerms))
            {
                return false;
            }

            for (var i = 0; i < query.ExcludedTerms.Count; i++)
            {
                if (MatchesToken(document, query.ExcludedTerms[i]))
                {
                    return false;
                }
            }

            return true;
        }

        internal static string NormalizeValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = AssetRecord.NormalizePath(value.Trim());
            while (normalized.Length > 1 && normalized[normalized.Length - 1] == '/')
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }

            return normalized;
        }

        private static void ParseToken(
            string source,
            int rawStart,
            int rawEnd,
            ParsedSearchQuery parsed)
        {
            if (rawStart >= rawEnd)
            {
                return;
            }

            var start = rawStart;
            var excluded = false;
            var exact = false;
            if (source[start] == '-')
            {
                excluded = true;
                start++;
            }

            if (start < rawEnd && source[start] == '!')
            {
                exact = true;
                start++;
            }

            if (start >= rawEnd)
            {
                return;
            }

            var separator = FindFieldSeparator(source, start, rawEnd);
            var field = SearchField.Any;
            var valueStart = start;
            var recognizedField = false;
            if (separator >= 0)
            {
                recognizedField = TryResolveField(source, start, separator, out field);
                if (recognizedField)
                {
                    exact |= source[separator] == '=';
                    valueStart = separator + 1;
                    if (valueStart < rawEnd && source[valueStart] == '!')
                    {
                        exact = true;
                        valueStart++;
                    }

                    // A dangling qualifier is incomplete, so it must not broaden to
                    // the entire corpus while the user is still typing its value.
                    if (valueStart >= rawEnd)
                    {
                        parsed.MatchesNothing = true;
                        return;
                    }
                }
            }

            // Unknown qualifiers stay literal. Silently dropping `foo:` and searching
            // only for `bar` would broaden the query and surface unrelated assets.
            if (!recognizedField)
            {
                field = SearchField.Any;
                valueStart = start;
            }

            var valueWasQuoted = source[valueStart] == '"';
            var value = DecodeValue(source, valueStart, rawEnd);
            var normalizedValue = AssetRecord.NormalizePath(value.Trim());
            if (normalizedValue.Length == 0)
            {
                // Quoted whitespace is still an explicitly empty qualifier value.
                // Treat it like the dangling form (`t:`): an incomplete filter must
                // never broaden to the whole project while the user is typing.
                if (recognizedField)
                {
                    parsed.MatchesNothing = true;
                }

                return;
            }

            if (field == SearchField.Extension && normalizedValue[0] == '.')
            {
                normalizedValue = normalizedValue.Substring(1);
                if (normalizedValue.Length == 0)
                {
                    parsed.MatchesNothing = true;
                    return;
                }
            }

            if (field == SearchField.Area
                && !string.Equals(normalizedValue, "all", StringComparison.Ordinal)
                && !string.Equals(normalizedValue, "assets", StringComparison.Ordinal)
                && !string.Equals(normalizedValue, "packages", StringComparison.Ordinal))
            {
                parsed.MatchesNothing = true;
                return;
            }

            if (field == SearchField.Any && !valueWasQuoted && IsFullGuid(normalizedValue))
            {
                field = SearchField.Guid;
                exact = true;
            }

            if ((field == SearchField.Glob
                    || (field == SearchField.Any && !valueWasQuoted && ContainsGlobOperator(normalizedValue))))
            {
                if (!GlobMatcher.TryExpand(normalizedValue, out var patterns))
                {
                    parsed.MatchesNothing = true;
                    return;
                }

                for (var i = 0; i < patterns.Count; i++)
                {
                    if (GlobMatcher.GetLongestGlobLiteral(patterns[i]).Length < GlobMatcher.MinimumLiteralLength)
                    {
                        parsed.MatchesNothing = true;
                        return;
                    }
                }

                parsed.Add(SearchToken.CreateGlob(patterns), excluded);
                return;
            }

            parsed.Add(
                SearchToken.CreateNormalized(
                    field,
                    normalizedValue,
                    exact ? SearchMatchMode.Exact : SearchMatchMode.Partial),
                excluded);
        }

        private static bool MatchesAny(SearchDocument document, IReadOnlyList<SearchToken> terms)
        {
            for (var i = 0; i < terms.Count; i++)
            {
                if (MatchesToken(document, terms[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesToken(SearchDocument document, SearchToken token)
        {
            if (token.MatchMode == SearchMatchMode.Glob)
            {
                for (var i = 0; i < token.GlobPatterns.Count; i++)
                {
                    if (GlobMatcher.Matches(document.NormalizedAssetPath, token.GlobPatterns[i]))
                    {
                        return true;
                    }
                }

                return false;
            }

            switch (token.Field)
            {
                case SearchField.Name:
                    return MatchesText(document.NormalizedName, token);
                case SearchField.Path:
                    return MatchesText(document.NormalizedAssetPath, token);
                case SearchField.Directory:
                    return MatchesDirectory(document.NormalizedAssetPath, token.Value);
                case SearchField.Type:
                    return token.MatchMode == SearchMatchMode.Exact
                        || IsBuiltInTypeAlias(token.Value)
                        ? ContainsLineValue(document.NormalizedTypeSearchText, token.Value)
                        : Contains(document.NormalizedTypeSearchText, token.Value);
                case SearchField.Label:
                    return token.MatchMode == SearchMatchMode.Exact
                        ? ContainsValue(document.NormalizedUnityLabels, token.Value)
                        : Contains(document.NormalizedUnityLabelSearchText, token.Value);
                case SearchField.Tag:
                    return token.MatchMode == SearchMatchMode.Exact
                        ? ContainsLineValue(document.NormalizedTagSearchText, token.Value)
                        : Contains(document.NormalizedTagSearchText, token.Value);
                case SearchField.Extension:
                    return string.Equals(document.NormalizedExtension, token.Value, StringComparison.Ordinal);
                case SearchField.Area:
                    return string.Equals(token.Value, "all", StringComparison.Ordinal)
                        || string.Equals(document.Area, token.Value, StringComparison.Ordinal);
                case SearchField.Guid:
                    return token.MatchMode == SearchMatchMode.Exact
                        ? string.Equals(document.NormalizedGuid, token.Value, StringComparison.Ordinal)
                        : token.Value.Length > GuidIndexedPrefixLength
                            ? document.NormalizedGuid.StartsWith(token.Value, StringComparison.Ordinal)
                            : document.NormalizedGuid.IndexOf(
                                token.Value,
                                0,
                                Math.Min(document.NormalizedGuid.Length, GuidIndexedPrefixLength),
                                StringComparison.Ordinal) >= 0;
                default:
                    // Unity's !term convention is an exact asset-name match. Partial
                    // unqualified terms retain the panel's established name/type/tag/
                    // guid behavior while deliberately excluding parent folders and
                    // the extension-derived aliases reserved for explicit `t:` queries.
                    // The GUID leg is a PREFIX of at least MinimumUnqualifiedGuidPrefixLength
                    // characters, not a substring: a GUID is 32 hex characters, so a
                    // substring rule made any two hex-ish characters match roughly half
                    // the project. A prefix is what someone pasting the head of a GUID
                    // actually wants; `guid:` still matches anywhere inside it.
                    return token.MatchMode == SearchMatchMode.Exact
                        ? string.Equals(document.NormalizedName, token.Value, StringComparison.Ordinal)
                        : Contains(document.NormalizedName, token.Value)
                            || Contains(document.NormalizedTypeName, token.Value)
                            || MatchesGuidPrefix(document.NormalizedGuid, token.Value)
                            || Contains(document.NormalizedTagSearchText, token.Value)
                            || Contains(document.NormalizedUnityLabelSearchText, token.Value);
            }
        }

        private static bool MatchesText(string source, SearchToken token)
        {
            return token.MatchMode == SearchMatchMode.Exact
                ? string.Equals(source, token.Value, StringComparison.Ordinal)
                : Contains(source, token.Value);
        }

        private static bool MatchesDirectory(string normalizedPath, string expectedSegment)
        {
            var lastSlash = normalizedPath.LastIndexOf('/');
            var segmentStart = 0;
            while (segmentStart < lastSlash)
            {
                var slash = normalizedPath.IndexOf('/', segmentStart);
                var segmentEnd = slash < 0 || slash > lastSlash ? lastSlash : slash;
                if (segmentEnd - segmentStart == expectedSegment.Length
                    && string.CompareOrdinal(normalizedPath, segmentStart, expectedSegment, 0, expectedSegment.Length) == 0)
                {
                    return true;
                }

                segmentStart = segmentEnd + 1;
            }

            return false;
        }

        private static bool ContainsValue(IReadOnlyList<string> values, string expected)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsBuiltInTypeAlias(string value)
        {
            switch (value)
            {
                case "script":
                case "prefab":
                case "model":
                case "image":
                case "audio":
                case "video":
                case "scene":
                case "file":
                case "object":
                case "folder":
                    return true;
                default:
                    return false;
            }
        }

        private static bool ContainsLineValue(string values, string expected)
        {
            var start = 0;
            while (start <= values.Length)
            {
                var end = values.IndexOf('\n', start);
                if (end < 0)
                {
                    end = values.Length;
                }

                if (end - start == expected.Length
                    && string.CompareOrdinal(values, start, expected, 0, expected.Length) == 0)
                {
                    return true;
                }

                if (end == values.Length)
                {
                    return false;
                }

                start = end + 1;
            }

            return false;
        }

        private static bool Contains(string source, string value)
        {
            return source.IndexOf(value, StringComparison.Ordinal) >= 0;
        }

        // An unqualified term matches a GUID only as a prefix, and only once it is
        // long enough to be a deliberate GUID paste rather than an accident. Four
        // hex characters name one record in ~65k, which is the point at which the
        // answer is useful; below it the term is almost certainly ordinary text.
        internal const int MinimumUnqualifiedGuidPrefixLength = 4;

        private static bool MatchesGuidPrefix(string normalizedGuid, string value)
        {
            return value.Length >= MinimumUnqualifiedGuidPrefixLength
                && normalizedGuid.StartsWith(value, StringComparison.Ordinal);
        }

        // Cheap necessary condition for the leg above, evaluated ONCE per query
        // instead of once per candidate: a normalized GUID is lowercase hex, so a
        // term carrying any other character cannot prefix one. This is what keeps
        // the ordinary typing path ("play", "controller") off the GUID posting
        // list entirely.
        internal static bool IsPossibleUnqualifiedGuidPrefix(string value)
        {
            if (value == null || value.Length < MinimumUnqualifiedGuidPrefixLength)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var character = value[i];
                var isHex = (character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsTokenSeparator(char character)
        {
            return character == ' ' || character == '\t' || character == '\r' || character == '\n';
        }

        private static bool IsEscapedQuote(string source, int quoteIndex, bool inQuotes)
        {
            if (!inQuotes || quoteIndex == 0 || source[quoteIndex - 1] != '\\')
            {
                return false;
            }

            var slashCount = 0;
            for (var i = quoteIndex - 1; i >= 0 && source[i] == '\\'; i--)
            {
                slashCount++;
            }

            return (slashCount & 1) != 0;
        }

        private static int FindFieldSeparator(string source, int start, int end)
        {
            var inQuotes = false;
            for (var i = start; i < end; i++)
            {
                var character = source[i];
                if (character == '"' && !IsEscapedQuote(source, i, inQuotes))
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && (character == ':' || character == '='))
                {
                    return i;
                }
            }

            return -1;
        }

        // Every keystroke reparses the whole query, so the qualifier key is
        // compared where it sits: the Substring + ToLowerInvariant pair this
        // replaced allocated two strings per qualified token per parse. The
        // length switch keeps the dispatch as cheap as the string switch was,
        // and OrdinalIgnoreCase agrees with invariant lowering for every key
        // below (they are ASCII, so no culture form can collide with one).
        private static bool TryResolveField(string source, int start, int end, out SearchField field)
        {
            var key = source.AsSpan(start, end - start);
            switch (key.Length)
            {
                case 1:
                    switch (key[0])
                    {
                        case 'n':
                        case 'N':
                            field = SearchField.Name;
                            return true;
                        case 'd':
                        case 'D':
                            field = SearchField.Directory;
                            return true;
                        case 't':
                        case 'T':
                            field = SearchField.Type;
                            return true;
                        case 'l':
                        case 'L':
                            field = SearchField.Label;
                            return true;
                        case 'e':
                        case 'E':
                            field = SearchField.Extension;
                            return true;
                        case 'a':
                        case 'A':
                            field = SearchField.Area;
                            return true;
                    }

                    break;
                case 2:
                    if (KeyEquals(key, "id"))
                    {
                        field = SearchField.Guid;
                        return true;
                    }

                    break;
                case 3:
                    if (KeyEquals(key, "dir"))
                    {
                        field = SearchField.Directory;
                        return true;
                    }

                    if (KeyEquals(key, "tag"))
                    {
                        field = SearchField.Tag;
                        return true;
                    }

                    if (KeyEquals(key, "ext"))
                    {
                        field = SearchField.Extension;
                        return true;
                    }

                    break;
                case 4:
                    if (KeyEquals(key, "name"))
                    {
                        field = SearchField.Name;
                        return true;
                    }

                    if (KeyEquals(key, "path"))
                    {
                        field = SearchField.Path;
                        return true;
                    }

                    if (KeyEquals(key, "type"))
                    {
                        field = SearchField.Type;
                        return true;
                    }

                    if (KeyEquals(key, "area"))
                    {
                        field = SearchField.Area;
                        return true;
                    }

                    if (KeyEquals(key, "guid"))
                    {
                        field = SearchField.Guid;
                        return true;
                    }

                    if (KeyEquals(key, "glob"))
                    {
                        field = SearchField.Glob;
                        return true;
                    }

                    break;
                case 5:
                    if (KeyEquals(key, "label"))
                    {
                        field = SearchField.Label;
                        return true;
                    }

                    break;
                case 9:
                    if (KeyEquals(key, "directory"))
                    {
                        field = SearchField.Directory;
                        return true;
                    }

                    if (KeyEquals(key, "extension"))
                    {
                        field = SearchField.Extension;
                        return true;
                    }

                    break;
            }

            field = SearchField.Any;
            return false;
        }

        private static bool KeyEquals(ReadOnlySpan<char> key, string expected)
        {
            return key.Equals(expected.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        private static string DecodeValue(string source, int start, int end)
        {
            if (source.IndexOf('"', start, end - start) < 0)
            {
                return source.Substring(start, end - start);
            }

            var buffer = new System.Text.StringBuilder(end - start);
            var inQuotes = false;
            for (var i = start; i < end; i++)
            {
                var character = source[i];
                if (character == '"' && !IsEscapedQuote(source, i, inQuotes))
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (inQuotes
                    && character == '\\'
                    && i + 1 < end
                    && (source[i + 1] == '"' || source[i + 1] == '\\'))
                {
                    buffer.Append(source[++i]);
                    continue;
                }

                buffer.Append(character);
            }

            return buffer.ToString();
        }

        private static bool ContainsGlobOperator(string value)
        {
            return value.IndexOf('*') >= 0
                || value.IndexOf('?') >= 0
                || value.IndexOf('{') >= 0
                || value.IndexOf('}') >= 0;
        }

        private static bool IsFullGuid(string value)
        {
            if (value.Length != 32)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var character = value[i];
                if ((character < '0' || character > '9')
                    && (character < 'a' || character > 'f'))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public sealed class ParsedSearchQuery
    {
        private static readonly IReadOnlyList<SearchToken> EmptyTerms = Array.Empty<SearchToken>();

        private List<SearchToken> _requiredTerms;
        private List<SearchToken> _typeTerms;
        private List<SearchToken> _labelTerms;
        private List<SearchToken> _excludedTerms;

        public IReadOnlyList<SearchToken> RequiredTerms => _requiredTerms ?? EmptyTerms;

        public IReadOnlyList<SearchToken> TypeTerms => _typeTerms ?? EmptyTerms;

        internal IReadOnlyList<SearchToken> LabelTerms => _labelTerms ?? EmptyTerms;

        public IReadOnlyList<SearchToken> ExcludedTerms => _excludedTerms ?? EmptyTerms;

        internal bool MatchesNothing { get; set; }

        internal int Count => (_requiredTerms?.Count ?? 0)
            + (_typeTerms?.Count ?? 0)
            + (_labelTerms?.Count ?? 0)
            + (_excludedTerms?.Count ?? 0);

        internal bool IsEmpty => Count == 0 && !MatchesNothing;

        internal void Add(SearchToken token, bool excluded)
        {
            if (excluded)
            {
                (_excludedTerms ?? (_excludedTerms = new List<SearchToken>())).Add(token);
            }
            else if (token.Field == SearchField.Type)
            {
                (_typeTerms ?? (_typeTerms = new List<SearchToken>())).Add(token);
            }
            else if (token.Field == SearchField.Label)
            {
                (_labelTerms ?? (_labelTerms = new List<SearchToken>())).Add(token);
            }
            else
            {
                (_requiredTerms ?? (_requiredTerms = new List<SearchToken>())).Add(token);
            }
        }
    }

    internal readonly struct SearchDocument
    {
        private SearchDocument(
            string normalizedName,
            string normalizedAssetPath,
            string normalizedTypeName,
            string normalizedTypeSearchText,
            string normalizedExtension,
            string normalizedGuid,
            string normalizedTagSearchText,
            IReadOnlyList<string> normalizedUnityLabels,
            string normalizedUnityLabelSearchText)
        {
            NormalizedName = normalizedName;
            NormalizedAssetPath = normalizedAssetPath;
            NormalizedTypeName = normalizedTypeName;
            NormalizedTypeSearchText = normalizedTypeSearchText;
            NormalizedExtension = normalizedExtension;
            NormalizedGuid = normalizedGuid;
            NormalizedTagSearchText = normalizedTagSearchText;
            NormalizedUnityLabels = normalizedUnityLabels;
            NormalizedUnityLabelSearchText = normalizedUnityLabelSearchText;
        }

        internal string NormalizedName { get; }

        internal string NormalizedAssetPath { get; }

        internal string NormalizedTypeName { get; }

        internal string NormalizedTypeSearchText { get; }

        internal string NormalizedExtension { get; }

        internal string NormalizedGuid { get; }

        internal string NormalizedTagSearchText { get; }

        internal IReadOnlyList<string> NormalizedUnityLabels { get; }

        internal string NormalizedUnityLabelSearchText { get; }

        internal string Area => NormalizedAssetPath.StartsWith("packages/", StringComparison.Ordinal)
            || string.Equals(NormalizedAssetPath, "packages", StringComparison.Ordinal)
                ? "packages"
                : NormalizedAssetPath.StartsWith("assets/", StringComparison.Ordinal)
                    || string.Equals(NormalizedAssetPath, "assets", StringComparison.Ordinal)
                        ? "assets"
                        : string.Empty;

        internal static SearchDocument FromAssetRecord(AssetRecord record)
        {
            return new SearchDocument(
                record.NormalizedName,
                record.NormalizedAssetPath,
                record.NormalizedTypeName,
                record.NormalizedTypeSearchText,
                record.NormalizedExtension,
                record.NormalizedGuid,
                record.NormalizedTagSearchText,
                record.NormalizedUnityLabels,
                record.NormalizedUnityLabelSearchText);
        }

        internal static SearchDocument Create(
            string name,
            string assetPath,
            string typeName,
            string guid = "",
            string tagSearchText = "",
            IReadOnlyList<string> unityLabels = null,
            bool isFolder = false)
        {
            var normalizedName = AssetRecord.NormalizeText(name ?? string.Empty);
            var normalizedPath = AssetRecord.NormalizePath(assetPath ?? string.Empty);
            var normalizedTypeName = AssetRecord.NormalizeText(typeName ?? string.Empty);
            var extension = isFolder ? string.Empty : AssetRecord.GetNormalizedExtension(normalizedPath);
            var typeSearchText = isFolder
                ? "folder\nobject"
                : AssetRecord.BuildNormalizedTypeSearchText(normalizedTypeName, extension);
            var normalizedGuid = AssetRecord.NormalizeText(guid ?? string.Empty);
            var normalizedTags = AssetRecord.NormalizeText(tagSearchText ?? string.Empty);
            var normalizedLabels = NormalizeLabels(unityLabels);
            var labelSearchText = string.Join("\n", normalizedLabels);
            return new SearchDocument(
                normalizedName,
                normalizedPath,
                normalizedTypeName,
                typeSearchText,
                extension,
                normalizedGuid,
                normalizedTags,
                normalizedLabels,
                labelSearchText);
        }

        internal static SearchDocument CreateNormalizedFolder(
            string normalizedName,
            string normalizedPath,
            string normalizedGuid,
            string normalizedTagSearchText,
            IReadOnlyList<string> normalizedUnityLabels,
            string normalizedUnityLabelSearchText)
        {
            return new SearchDocument(
                normalizedName ?? string.Empty,
                normalizedPath ?? string.Empty,
                "folder",
                "folder\nobject",
                string.Empty,
                normalizedGuid ?? string.Empty,
                normalizedTagSearchText ?? string.Empty,
                normalizedUnityLabels ?? Array.Empty<string>(),
                normalizedUnityLabelSearchText ?? string.Empty);
        }

        private static string[] NormalizeLabels(IReadOnlyList<string> labels)
        {
            if (labels == null || labels.Count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[labels.Count];
            for (var i = 0; i < labels.Count; i++)
            {
                result[i] = AssetRecord.NormalizeText(labels[i] ?? string.Empty);
            }

            return result;
        }
    }

    public readonly struct SearchToken
    {
        private SearchToken(
            SearchField field,
            string value,
            SearchMatchMode matchMode,
            IReadOnlyList<string> globPatterns)
        {
            Field = field;
            Value = value;
            MatchMode = matchMode;
            GlobPatterns = globPatterns ?? Array.Empty<string>();
        }

        public SearchField Field { get; }

        public string Value { get; }

        internal SearchMatchMode MatchMode { get; }

        internal IReadOnlyList<string> GlobPatterns { get; }

        internal static SearchToken CreateNormalized(
            SearchField field,
            string normalizedValue,
            SearchMatchMode matchMode = SearchMatchMode.Partial)
        {
            return new SearchToken(field, normalizedValue, matchMode, null);
        }

        internal static SearchToken CreateGlob(IReadOnlyList<string> normalizedPatterns)
        {
            return new SearchToken(SearchField.Glob, string.Empty, SearchMatchMode.Glob, normalizedPatterns);
        }
    }

    internal enum SearchMatchMode
    {
        Partial = 0,
        Exact = 1,
        Glob = 2,
    }

    public enum SearchField
    {
        Any = 0,
        Name = 1,
        Path = 2,
        Type = 3,
        Tag = 4,
        Label = 5,
        Extension = 6,
        Directory = 7,
        Area = 8,
        Guid = 9,
        Glob = 10,
    }

    internal static class GlobMatcher
    {
        private const int MaxPatterns = 32;
        internal const int MinimumLiteralLength = 3;

        internal static string GetLongestGlobLiteral(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return string.Empty;
            }

            var bestStart = 0;
            var bestLength = 0;
            var runStart = 0;
            for (var i = 0; i <= pattern.Length; i++)
            {
                if (i < pattern.Length && pattern[i] != '*' && pattern[i] != '?')
                {
                    continue;
                }

                var runLength = i - runStart;
                if (runLength > bestLength)
                {
                    bestStart = runStart;
                    bestLength = runLength;
                }

                runStart = i + 1;
            }

            return bestLength == 0
                ? string.Empty
                : pattern.Substring(bestStart, bestLength);
        }

        internal static bool TryExpand(string pattern, out IReadOnlyList<string> patterns)
        {
            var pending = new Queue<string>();
            var expanded = new List<string>();
            pending.Enqueue(pattern);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                var open = current.IndexOf('{');
                var close = current.IndexOf('}');
                if (open < 0 && close < 0)
                {
                    expanded.Add(TrimRootSlash(current));
                    if (expanded.Count > MaxPatterns)
                    {
                        patterns = Array.Empty<string>();
                        return false;
                    }

                    continue;
                }

                if (open < 0 || close < open || current.IndexOf('{', open + 1) >= 0 && current.IndexOf('{', open + 1) < close)
                {
                    patterns = Array.Empty<string>();
                    return false;
                }

                var choices = current.Substring(open + 1, close - open - 1).Split(',');
                if (choices.Length < 2)
                {
                    patterns = Array.Empty<string>();
                    return false;
                }

                for (var i = 0; i < choices.Length; i++)
                {
                    if (choices[i].Length == 0 || pending.Count + expanded.Count >= MaxPatterns)
                    {
                        patterns = Array.Empty<string>();
                        return false;
                    }

                    pending.Enqueue(
                        current.Substring(0, open)
                        + choices[i]
                        + current.Substring(close + 1));
                }
            }

            patterns = expanded;
            return expanded.Count > 0;
        }

        internal static bool Matches(string normalizedPath, string pattern)
        {
            var sourceStart = pattern.IndexOf('/') < 0
                ? normalizedPath.LastIndexOf('/') + 1
                : 0;
            var sourceLength = normalizedPath.Length - sourceStart;
            var cellCount = sourceLength + 1;
            Span<byte> previous = cellCount <= 512
                ? stackalloc byte[cellCount]
                : new byte[cellCount];
            Span<byte> current = cellCount <= 512
                ? stackalloc byte[cellCount]
                : new byte[cellCount];
            previous[0] = 1;
            var patternIndex = 0;

            while (patternIndex < pattern.Length)
            {
                current.Clear();
                var patternCharacter = pattern[patternIndex];
                if (patternCharacter == '*')
                {
                    // A globstar followed by a slash is one path-segment unit. It can
                    // consume no directory at all (`Assets/**/Foo` -> `Assets/Foo`),
                    // or any number of complete segments including their trailing slash.
                    // Handling the slash here is what distinguishes this from a plain
                    // `**`, whose normal zero-width branch cannot also skip the next `/`.
                    var isGlobstarDirectory = (patternIndex == 0 || pattern[patternIndex - 1] == '/')
                        && patternIndex + 2 < pattern.Length
                        && pattern[patternIndex + 1] == '*'
                        && pattern[patternIndex + 2] == '/';
                    if (isGlobstarDirectory)
                    {
                        current[0] = previous[0];
                        var canConsume = previous[0] != 0;
                        for (var sourceIndex = 1; sourceIndex <= sourceLength; sourceIndex++)
                        {
                            if (previous[sourceIndex] != 0)
                            {
                                current[sourceIndex] = 1;
                            }

                            if (normalizedPath[sourceStart + sourceIndex - 1] == '/' && canConsume)
                            {
                                current[sourceIndex] = 1;
                            }

                            canConsume |= previous[sourceIndex] != 0;
                        }

                        patternIndex += 3;
                        var directorySwap = previous;
                        previous = current;
                        current = directorySwap;
                        continue;
                    }

                    var matchesSlash = patternIndex + 1 < pattern.Length
                        && pattern[patternIndex + 1] == '*';
                    current[0] = previous[0];
                    for (var sourceIndex = 1; sourceIndex <= sourceLength; sourceIndex++)
                    {
                        var sourceCharacter = normalizedPath[sourceStart + sourceIndex - 1];
                        if (previous[sourceIndex] != 0
                            || (current[sourceIndex - 1] != 0
                                && (matchesSlash || sourceCharacter != '/')))
                        {
                            current[sourceIndex] = 1;
                        }
                    }

                    patternIndex += matchesSlash ? 2 : 1;
                }
                else
                {
                    for (var sourceIndex = 1; sourceIndex <= sourceLength; sourceIndex++)
                    {
                        var sourceCharacter = normalizedPath[sourceStart + sourceIndex - 1];
                        if (previous[sourceIndex - 1] != 0
                            && (patternCharacter == '?'
                                ? sourceCharacter != '/'
                                : patternCharacter == sourceCharacter))
                        {
                            current[sourceIndex] = 1;
                        }
                    }

                    patternIndex++;
                }

                var swap = previous;
                previous = current;
                current = swap;
            }

            return previous[sourceLength] != 0;
        }

        private static string TrimRootSlash(string pattern)
        {
            return pattern.Length > 0 && pattern[0] == '/'
                ? pattern.Substring(1)
                : pattern;
        }
    }
}
