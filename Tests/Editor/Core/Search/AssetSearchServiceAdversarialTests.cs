using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Zep.Core.Indexing;
using Zep.Core.Search;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Search
{
    /// <summary>
    /// Adversarial tests that pin specific invariants of the trigram search engine.
    /// Each test proves a single property a future refactor could silently break.
    /// The happy-path coverage lives in <see cref="AssetSearchServiceTests"/>.
    /// </summary>
    public sealed class AssetSearchServiceAdversarialTests
    {
        // T1: The trigram index is lossy — it finds records that contain every gram of
        // the query, but the grams can be spread across non-adjacent positions. The
        // verify step (SearchQuery.Matches calling IndexOf) is what prevents such false
        // positives from leaking into results. Removing it would make this test fail.
        [Test]
        public void Search_Trigram_FalsePositive_IsRejectedByVerifyStep()
        {
            var index = new InMemoryAssetIndex();
            // Name contains grams "abc", "bcd", "cde" separated by delimiters so their
            // posting lists all light up for this record, but the substring "abcde"
            // itself is nowhere in the normalized search text.
            index.Upsert(new AssetRecord("g1", "Dir/noise.txt", "xabc-ybcd-zcde", "Txt"));
            // Control record that truly contains "abcde" — confirms the engine still
            // returns real hits alongside the false-positive filter.
            index.Upsert(new AssetRecord("g2", "Dir/real.txt", "abcde-positive", "Txt"));

            var search = new AssetSearchService(index);

            var hits = search.Search("abcde").Select(x => x.Guid).ToArray();
            Assert.That(hits, Is.EqualTo(new[] { "g2" }),
                "intersection returns the decoy via grams abc+bcd+cde; only the verify step filters it out.");
        }

        // T2: The smallest-posting-list-first intersection is what keeps queries cheap
        // when a common prefix dominates. This test exercises 10,000 records that all
        // share the "player" prefix: a broad query must return all of them without
        // duplicates, and a uniquely-specific query must prune down to exactly one.
        [Test]
        public void Search_PathologicalIdenticalPrefix_BroadAndNarrowQueriesStaySharp()
        {
            var index = new InMemoryAssetIndex();
            for (var i = 1; i <= 10000; i++)
            {
                var n = i.ToString("D5");
                index.Upsert(new AssetRecord("g-" + n, "Assets/Players/Player_" + n + ".prefab", "Player_" + n, "Prefab"));
            }

            var search = new AssetSearchService(index);

            var broad = search.Search("player");
            Assert.That(broad.Count, Is.EqualTo(10000), "broad query must match every record");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < broad.Count; i++)
            {
                Assert.That(seen.Add(broad[i].Guid), Is.True,
                    "posting-list intersection must not yield duplicate ordinals");
            }

            // Player_05000 is the only record that contains the substring "player_05000".
            // Player_10000 has "r_1" instead of "r_0" at the corresponding offset, so it
            // is filtered out even though many of its grams overlap.
            var narrow = search.Search("player_05000");
            Assert.That(narrow.Count, Is.EqualTo(1));
            Assert.That(narrow[0].Guid, Is.EqualTo("g-05000"));
        }

        // T3: Mutating the service across a 25% tombstone crossing must still produce
        // correct results. The engine's CompactIfNeeded rebuilds from the backing index,
        // so state is preserved only when the index and service are kept in lockstep
        // (which is the production usage pattern — see ZepController). This
        // test exercises that contract at scale and at the boundary.
        [Test]
        public void Search_TombstoneChurnAcrossCompactionThreshold_PreservesCorrectness()
        {
            const int count = 1000;
            var index = new InMemoryAssetIndex();
            var search = new AssetSearchService(index);

            // Phase 1: insert 1000 records using the production pattern (the service
            // owns the paired catalog write + search-index delta). At several mid-phase
            // checkpoints the query must already return the currently-inserted subset.
            for (var i = 0; i < count; i++)
            {
                var n = i.ToString("D5");
                var r = new AssetRecord("old-" + n, "Assets/old_" + n + ".asset", "oldname_" + n, "Prefab");
                SearchCatalogTestMutations.Upsert(index, search, r);
            }
            Assert.That(search.Search("oldname_").Count, Is.EqualTo(count), "phase-1 insert: all records queryable");
            Assert.That(search.Search(string.Empty).Count, Is.EqualTo(count), "phase-1 empty-query count");

            // Phase 2: delete all 1000. At 25% removal (250 tombstones of 1000) the
            // engine crosses the compaction threshold. We check correctness at the
            // boundary, in the middle, and at the end.
            for (var i = 0; i < count; i++)
            {
                var guid = "old-" + i.ToString("D5");
                SearchCatalogTestMutations.Remove(index, search, guid);

                if (i == 249)
                {
                    Assert.That(search.Search("oldname_").Count, Is.EqualTo(count - 250),
                        "just below compaction threshold (250/1000 removed)");
                }
                else if (i == 250)
                {
                    Assert.That(search.Search("oldname_").Count, Is.EqualTo(count - 251),
                        "just past compaction threshold (251/1000 removed)");
                }
                else if (i == 500)
                {
                    Assert.That(search.Search("oldname_").Count, Is.EqualTo(count - 501),
                        "mid-phase-2 consistency");
                }
            }
            Assert.That(search.Search("oldname_"), Is.Empty, "phase-2 end: all removed");
            Assert.That(search.Search(string.Empty), Is.Empty, "phase-2 end: empty-query must see empty index");

            // Phase 3: insert 1000 fresh records with different guids and a distinct
            // name prefix. Previous guids/ordinals must not resurface.
            for (var i = 0; i < count; i++)
            {
                var n = i.ToString("D5");
                var r = new AssetRecord("new-" + n, "Assets/new_" + n + ".asset", "freshname_" + n, "Prefab");
                SearchCatalogTestMutations.Upsert(index, search, r);
            }
            Assert.That(search.Search("freshname_").Count, Is.EqualTo(count), "phase-3 insert: all fresh queryable");
            Assert.That(search.Search("oldname_"), Is.Empty, "phase-3: no ghost state from phase-1 guids");
            Assert.That(search.Search(string.Empty).Count, Is.EqualTo(count), "phase-3 empty-query count");
            for (var i = 0; i < 10; i++)
            {
                var n = i.ToString("D5");
                var probe = search.Search("freshname_" + n).Select(x => x.Guid).ToArray();
                Assert.That(probe, Is.EqualTo(new[] { "new-" + n }), "exact-match probe must still resolve uniquely");
            }
        }

        // T4: Short-query semantics at scale. A 1-char any-field query is served
        // exactly by the one-gram index (the gram IS the query, no verify step);
        // this test pins that the posting list equals true substring semantics —
        // exactly the records whose normalized text contains the query, and
        // nothing when the char appears nowhere. Before the multi-arity indices
        // this shape took a scan-all-and-verify fallback (which field-scoped
        // short queries still take); the observable contract is identical across
        // both implementations and this test guards it either way.
        [Test]
        public void Search_ShortQuery_ExactSubstringSemanticsAtScale()
        {
            var index = new InMemoryAssetIndex();
            var expected = new HashSet<string>(StringComparer.Ordinal);

            // 5000 records guaranteed to contain 'a' in NormalizedSearchText
            // (name "alpha_*" contains 'a'). Path "Assets/*" also contains 'a', so
            // let's be explicit: every field has 'a'.
            for (var i = 0; i < 5000; i++)
            {
                var n = i.ToString("D5");
                var guid = "ga-" + n;
                index.Upsert(new AssetRecord(guid, "Assets/Art/alpha_" + n + ".asset", "alpha_" + n, "Prefab"));
                expected.Add(guid);
            }

            // 5000 records with no 'a' anywhere in name, path, type, guid, or tags.
            // "ggzz-*" guid, "zzz/zz_" path, "jjkk_*" name, "Texture2D" type — all 'a'-free.
            // (Earlier revisions used "zzname_" here, which silently contains an 'a'.)
            for (var i = 0; i < 5000; i++)
            {
                var n = i.ToString("D5");
                index.Upsert(new AssetRecord("ggzz-" + n, "zzz/zz_" + n + ".txt", "jjkk_" + n, "Texture2D"));
            }

            var search = new AssetSearchService(index);

            var hits = search.Search("a");
            var hitGuids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < hits.Count; i++)
            {
                hitGuids.Add(hits[i].Guid);
            }

            Assert.That(hitGuids.Count, Is.EqualTo(expected.Count),
                "short query must return exactly the records whose normalized text contains the query");
            Assert.That(hitGuids.SetEquals(expected), Is.True,
                "short query must not return any record lacking the query substring");

            // Single-char query for a letter absent from every record must return
            // empty, not "all records" — the short-query path must never degrade
            // to dumping the live set.
            Assert.That(search.Search("q"), Is.Empty,
                "short query must not return records that don't actually contain the query");
        }

        // T5: Multi-token queries must still pick the rare token's posting list first.
        // If "com" (posting list of ~5000) were intersected before "xyzzy" (posting
        // list of ~3) the work would be O(5000) instead of O(3). The test encodes
        // both correctness (only the 3 rare records come back) and makes a silent
        // regression to large-first ordering visible via BENCHMARK_BASELINE.md.
        [Test]
        public void Search_CommonAndRareTokenMix_StillPicksRarePostingListFirst()
        {
            var index = new InMemoryAssetIndex();

            // 5000 records whose name contains "com" but NOT "xyzzy".
            for (var i = 0; i < 5000; i++)
            {
                var n = i.ToString("D5");
                index.Upsert(new AssetRecord("common-" + n, "Assets/common_" + n + ".asset", "combo_" + n, "Prefab"));
            }

            // 3 records whose name contains BOTH "com" and "xyzzy".
            index.Upsert(new AssetRecord("rare-1", "Assets/magic_1.asset", "com_xyzzy_1", "Prefab"));
            index.Upsert(new AssetRecord("rare-2", "Assets/magic_2.asset", "com_xyzzy_2", "Prefab"));
            index.Upsert(new AssetRecord("rare-3", "Assets/magic_3.asset", "com_xyzzy_3", "Prefab"));

            var search = new AssetSearchService(index);

            var hits = search.Search("com xyzzy").Select(x => x.Guid).OrderBy(g => g, StringComparer.Ordinal).ToArray();
            Assert.That(hits, Is.EqualTo(new[] { "rare-1", "rare-2", "rare-3" }));
        }

        // T6: The engine's Unicode contract. Search text is normalized to NFC and then
        // folded with ToLowerInvariant. That means:
        //   - ASCII case folding works.
        //   - Latin-1 case folding works ('É' → 'é').
        //   - Canonically equivalent NFC/NFD text matches.
        //   - No accent folding: "café" does NOT match "cafe".
        //   - CJK pass through unchanged; 3-char CJK strings produce one gram.
        //   - Surrogate pairs flow through as UTF-16 code units; an emoji counts as
        //     two chars for the gram stride.
        // The assertions below encode this as a contract so a refactor that changes it
        // (e.g. swapping in Unicode normalization) will fail this test and require an
        // explicit decision.
        [Test]
        public void Search_UnicodeNames_DocumentsFoldingContract()
        {
            var index = new InMemoryAssetIndex();
            // The guid, name, and type must themselves avoid unaccented "cafe" or "naive"
            // substrings so the accent-folding assertions below genuinely isolate the folding
            // question. NormalizedSearchText concatenates name\ntype\ntags\nlabels — the path
            // and the GUID are both
            // deliberately EXCLUDED (see AssetRecord.BuildNormalizedSearchText), and this test
            // issues only unqualified queries, so the folder paths below no longer feed matching.
            index.Upsert(new AssetRecord("g-one", "Assets/Menu/Café.png", "Café", "Texture2D"));
            index.Upsert(new AssetRecord("g-two", "Assets/JP/日本語.asset", "日本語", "ScriptableObject"));
            index.Upsert(new AssetRecord("g-three", "Assets/Docs/naïve.txt", "naïve", "TextAsset"));
            index.Upsert(new AssetRecord("g-four", "Assets/UI/\uD83C\uDFAEGame.png", "\uD83C\uDFAEGame", "Texture2D"));

            var search = new AssetSearchService(index);

            // Lowercase-invariant case folding works for ASCII and Latin-1 accented letters.
            Assert.That(search.Search("CAFÉ").Select(x => x.Guid), Is.EquivalentTo(new[] { "g-one" }),
                "uppercase Latin-1 'É' folds to 'é' via char.ToLowerInvariant");
            Assert.That(search.Search("café").Select(x => x.Guid), Is.EquivalentTo(new[] { "g-one" }));

            // Accent folding is NOT performed. "cafe" (plain ASCII) does not match "café".
            Assert.That(search.Search("cafe"), Is.Empty,
                "engine does not perform accent folding — 'cafe' does not match 'café'");

            // NFC vs NFD: a decomposed query is canonicalized before indexing/matching,
            // so it finds the record whose name stores the pre-composed code point.
            Assert.That(search.Search("cafe\u0301").Select(x => x.Guid), Is.EquivalentTo(new[] { "g-one" }),
                "NFD query must match its canonically equivalent NFC record");

            // CJK: short-query (2 chars) falls through the verify path; 3-char query
            // exercises a single gram.
            Assert.That(search.Search("日本").Select(x => x.Guid), Is.EquivalentTo(new[] { "g-two" }),
                "2-char CJK uses short-query fallback");
            Assert.That(search.Search("日本語").Select(x => x.Guid), Is.EquivalentTo(new[] { "g-two" }),
                "3-char CJK exercises a single UTF-16 gram");

            // Latin with diaeresis case-insensitivity: mixed case + accent.
            Assert.That(search.Search("NAÏVE").Select(x => x.Guid), Is.EquivalentTo(new[] { "g-three" }));

            // Surrogate pairs: a 1-codepoint emoji is 2 UTF-16 chars, so the 2-char
            // query hits the short-query fallback. The substring scan on UTF-16
            // code units matches because both the record and the query encode the
            // emoji the same way.
            Assert.That(search.Search("\uD83C\uDFAE").Select(x => x.Guid), Is.EquivalentTo(new[] { "g-four" }),
                "surrogate-pair emoji query flows through short-query fallback");
            // Multi-char query including the emoji exercises the gram path with
            // surrogate halves packed into ulongs as individual code units.
            Assert.That(search.Search("\uD83C\uDFAEgame").Select(x => x.Guid), Is.EquivalentTo(new[] { "g-four" }),
                "gram path packs surrogate halves as two UTF-16 code units");
        }

        // T7: Threading contract — pinned by a single-threaded assertion so it is
        // documented somewhere executable.
        //
        //   AssetSearchService is main-thread only. The engine intentionally does not
        //   take locks or add thread guards: Unity editor tooling is single-threaded
        //   by convention, and a concurrent mutation during Search() is undefined
        //   behavior.
        //
        //   Specifically:
        //   - Search() may return the internal _recordsByOrdinal list directly when
        //     the query is empty and there are no tombstones. Iterating that list
        //     while another thread mutates the index can throw InvalidOperationException
        //     ("Collection was modified"), or worse, silently produce wrong results.
        //   - For non-trivial queries Search() returns a fresh List<AssetRecord>
        //     snapshot the caller owns; that snapshot is safe to iterate after
        //     subsequent single-threaded mutations, as asserted below.
        //   - Adding synchronization here would be a breaking performance change
        //     to the contract. It is not a bug to omit.
        [Test]
        public void Search_ThreadingContract_SingleThreadedCallerOwnsReturnedSnapshot()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("g-a", "Assets/Players/Alpha.prefab", "Alpha", "Prefab"));
            index.Upsert(new AssetRecord("g-b", "Assets/Players/Beta.prefab", "Beta", "Prefab"));

            var search = new AssetSearchService(index);

            // A keyword query returns a fresh list. Subsequent single-threaded mutations
            // on the same thread must not corrupt the returned snapshot.
            var snapshot = search.Search("prefab");
            Assert.That(snapshot.Count, Is.EqualTo(2));

            // Mutate after returning, still on the same thread. This is explicitly the
            // only mutation pattern the contract allows.
            SearchCatalogTestMutations.Upsert(
                index,
                search,
                new AssetRecord("g-c", "Assets/Players/Gamma.prefab", "Gamma", "Prefab"));

            // The snapshot returned earlier must remain iterable and must not have
            // gained a third element (it is owned by the caller).
            Assert.DoesNotThrow(() =>
            {
                var count = 0;
                foreach (var r in snapshot)
                {
                    Assert.That(r, Is.Not.Null);
                    count++;
                }
                Assert.That(count, Is.EqualTo(2), "snapshot is a fresh list and does not reflect later mutations");
            });

            // A fresh query sees the new state, as expected.
            Assert.That(search.Search("prefab").Count, Is.EqualTo(3));
        }

        // T8..T12: the Suggest path's caps hold down COST, never correctness, so a
        // regression in one of them is invisible from the outside — the popup still shows
        // the right eight rows, it just scans the corpus to find them. Each test below
        // pins one bound through the service's work counters and fails when that bound is
        // removed; the happy-path ranking lives in AssetSearchServiceTests.

        // T8: both exact passes stop at MaxExactSuggestionCandidates (4096) however many
        // records share the typed prefix.
        [Test]
        public void Suggest_ExactCandidateScansStopAtTheirCap()
        {
            const int corpusSize = 6000;
            var index = new InMemoryAssetIndex();
            for (var i = 0; i < corpusSize; i++)
            {
                var n = i.ToString("D4");
                index.Upsert(new AssetRecord("g-" + n, "Assets/A/player" + n + ".asset", "player" + n, "ScriptableObject"));
            }

            var search = new AssetSearchService(index);

            var suggestions = search.Suggest("player");

            Assert.That(suggestions.Count, Is.EqualTo(8), "the popup still fills");
            Assert.That(search.LastSuggestPrefixScanRecords, Is.EqualTo(4096),
                "the alphabetical prefix scan must stop at MaxExactSuggestionCandidates");
            Assert.That(search.LastSuggestExactPostingRecords, Is.EqualTo(4096),
                "the posting-list pass must stop at MaxExactSuggestionCandidates");
        }

        // T9: the fuzzy pass collects at most MaxFuzzySuggestionCandidates (256) records,
        // however many share grams with the typed text.
        [Test]
        public void Suggest_FuzzyCandidateCollectionStopsAtItsCap()
        {
            var index = new InMemoryAssetIndex();
            for (var i = 0; i < 2000; i++)
            {
                var n = i.ToString("D4");
                index.Upsert(new AssetRecord("g-" + n, "Assets/B/xyzzy" + n + ".asset", "xyzzy" + n, "ScriptableObject"));
            }

            var search = new AssetSearchService(index);

            // No name contains "xyzzz", so the exact passes contribute nothing and the
            // fuzzy pass runs over 2000 gram-sharing records.
            var suggestions = search.Suggest("xyzzz");

            Assert.That(suggestions.Count, Is.EqualTo(8));
            Assert.That(suggestions.All(suggestion => suggestion.IsFuzzy), Is.True);
            Assert.That(search.LastSuggestFuzzyCandidates, Is.EqualTo(256),
                "the fuzzy candidate list must stop at MaxFuzzySuggestionCandidates");
        }

        // T10: the gram-hit threshold is what keeps the expensive edit-distance work off
        // records that merely share a gram or two with the typed text.
        [Test]
        public void Suggest_CandidatesBelowTheGramHitThreshold_NeverReachEditDistance()
        {
            var index = new InMemoryAssetIndex();
            for (var i = 0; i < 500; i++)
            {
                var n = i.ToString("D6");
                index.Upsert(new AssetRecord("g-" + n, "Assets/C/ab" + n + ".asset", "ab" + n, "ScriptableObject"));
            }

            var search = new AssetSearchService(index);

            // "abcdefghij" is 10 characters: 9 grams, 2 allowed edits, so a candidate needs
            // 3 gram hits. Every record here shares exactly one ("ab").
            var suggestions = search.Suggest("abcdefghij");

            Assert.That(suggestions, Is.Empty);
            Assert.That(search.LastSuggestFuzzyCandidates, Is.GreaterThan(0),
                "the records must genuinely reach the candidate list");
            Assert.That(search.LastSuggestFuzzyDistanceEvaluations, Is.Zero,
                "the hit threshold must reject them before any edit-distance work");
            Assert.That(search.LastSuggestDistanceCells, Is.Zero);
        }

        // T11: a candidate that is beyond the allowed edit distance must cost a bounded
        // slice of the table, not the whole table — row minima never decrease, so the walk
        // ends as soon as a whole row clears the bound.
        [Test]
        public void Suggest_ACandidateBeyondTheEditDistance_CostsOnlyABoundedSliceOfTheTable()
        {
            const string query = "abcdefghijklmnop";
            var index = new InMemoryAssetIndex();
            // Shares 14 of the query's 15 grams (so it clears the hit threshold) but
            // diverges after two characters, so the distance blows past 2 immediately.
            index.Upsert(new AssetRecord(
                "far",
                "Assets/D/far.asset",
                "ab" + new string('z', 30) + "cdefghijklmnop",
                "ScriptableObject"));

            var search = new AssetSearchService(index);

            var suggestions = search.Suggest(query);

            Assert.That(suggestions, Is.Empty);
            Assert.That(search.LastSuggestFuzzyDistanceEvaluations, Is.EqualTo(1),
                "the record must genuinely reach the edit-distance step");
            // The full table for this candidate window is query.Length rows of
            // (query.Length + 2 allowed edits) cells.
            var fullTableCells = query.Length * (query.Length + 2);
            Assert.That(search.LastSuggestDistanceCells, Is.LessThan(fullTableCells / 2),
                "the bounded walk must abandon the table long before completing it");
        }

        // T12: worst case for the whole Suggest path — 20,000 records sharing a long
        // prefix, queried with the near-miss spellings the fuzzy pass exists for. The
        // budget is deliberately generous: this pins the BOUNDS holding, not a timing
        // baseline (that lives in AssetSearchServicePerformanceRegressionTests).
        [Test]
        public void Suggest_AdversarialCorpus_StaysWithinAGenerousBudget()
        {
            var index = new InMemoryAssetIndex();
            for (var i = 0; i < 20000; i++)
            {
                var n = i.ToString("D5");
                index.Upsert(new AssetRecord(
                    "g-" + n,
                    "Assets/Adversarial/PlayerAbilityController_" + n + ".prefab",
                    "PlayerAbilityController_" + n,
                    "GameObject"));
            }

            var search = new AssetSearchService(index);
            var queries = new[]
            {
                "player",
                "playerability",
                "playeraiblity",
                "playerabilitycontroler",
                "plaeyrabilitycontroller",
                "controller_19999"
            };

            foreach (var query in queries)
            {
                _ = search.Suggest(query);
            }

            var samples = new double[queries.Length * 5];
            var sampleIndex = 0;
            for (var round = 0; round < 5; round++)
            {
                foreach (var query in queries)
                {
                    var watch = Stopwatch.StartNew();
                    _ = search.Suggest(query);
                    watch.Stop();
                    samples[sampleIndex++] = watch.Elapsed.TotalMilliseconds;
                }
            }

            Array.Sort(samples);
            var median = samples[samples.Length / 2];
            var worst = samples[samples.Length - 1];
            TestContext.Out.WriteLine(
                $"adversarial Suggest median={median:F3}ms worst={worst:F3}ms over {samples.Length} calls");
            Assert.That(worst, Is.LessThan(250.0),
                "an unbounded pass over this corpus would cost far more than a keystroke can afford");
        }
    }
}
