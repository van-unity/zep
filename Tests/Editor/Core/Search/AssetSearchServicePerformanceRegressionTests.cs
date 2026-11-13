using System;
using System.Collections.Generic;
using System.Diagnostics;
using Zep.Core.Indexing;
using Zep.Core.Search;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Search
{
    /// <summary>
    /// In-process performance regression guard for AssetSearchService.Search.
    ///
    /// Build an in-memory 50,000-record index, then for each query shape measure:
    ///   - median Search() wall time across 20 iterations (Stopwatch)
    ///   - total allocation bytes across those iterations using
    ///     GC.GetAllocatedBytesForCurrentThread() — thread-local and deterministic,
    ///     unlike GC.GetTotalMemory.
    /// Assert the observed timings are within 20% of baseline. Allocation
    /// numbers are reported only when the active Unity runtime exposes a
    /// working thread-allocation counter; Unity Mono commonly does not.
    ///
    /// The shapes cover both bare queries and the production shape: the panel
    /// always passes a folderScopePath (the default search scope resolves to
    /// "Assets") and, when a type filter is active, a typeName. A regression in
    /// the scope/type filter path is invisible to bare-query timings.
    ///
    /// Baseline numbers and methodology live in BENCHMARK_BASELINE.md (same
    /// folder) with machine/Unity-version context; they are *this* author's
    /// machine, so the test is intentionally Explicit — CI boxes will vary. Run
    /// on demand when verifying a change and update the baseline when
    /// intentional movement occurs.
    /// </summary>
    public sealed class AssetSearchServicePerformanceRegressionTests
    {
        private const int DatasetSize = 50000;
        private const int WarmupIterations = 3;
        private const int MeasureIterations = 20;
        private const double TimingTolerance = 1.20; // +20%

        // Baseline entries: (label, query, folderScopePath, typeName, expectedCount,
        // medianMs). Timing is milliseconds (median). The count makes a search regression fail instead
        // of appearing as an artificial speedup from an unexpectedly empty result set.
        //
        // Timing is machine-dependent; the baseline comes from running this test
        // inside the Unity Test Runner (Mono, NET_4_6 profile) on the same
        // machine. The +20% tolerance covers normal run-to-run variance; bigger
        // drift means either a real regression or a baseline that needs
        // re-measuring on the current machine (see BENCHMARK_BASELINE.md).
        //
        private static readonly (string Label, string Query, string Scope, string Type, int ExpectedCount, double MedianMs)[] Baseline =
        {
            // COUNTS re-recorded 2026-09-02 against the replacement fixture
            // (32-hex GUIDs, weighted type mix, ~2% labelled). TIMINGS below are
            // still the 2026-08-08 Mono capture against the OLD 7-character-GUID
            // corpus and are therefore NOT a valid gate for this corpus — see
            // BENCHMARK_BASELINE.md. Re-record them on the next Mono run of this
            // test before treating a [time] failure as a regression.
            //
            // Original capture note: 2026-08-08 under Unity batch mode (Mono,
            // NET_4_6, Apple M3 Pro, macOS 26.5.1, Unity 6000.3.10f1); median of
            // three back-to-back runs after Phase 5 switched broad results from
            // object references to stable ordinal arrays and made a lone
            // one/two-character any-field posting authoritative.
            //
            // 2026-09-02 (audit §3.11a / plan T3.17): "bare ab" and "scoped ab"
            // re-recorded 21755 -> 18106. Unqualified terms no longer read the
            // 32-hex GUID as a substring, so the ~3.6k records that matched "ab"
            // only through their GUID are gone. Every other shape is unchanged,
            // which is the evidence that the change is confined to that leg.
            ("bare ab",          "ab",                          "",       "",             18106, 0.496),
            ("bare prefab",      "prefab",                      "",       "",              7970, 2.764),
            ("path query",       "path:assets/players/player_05000", "", "",                 1, 0.967),
            ("bare narrow",      "player_05000",                "",       "",                 1, 0.134),
            ("token type combo", "type:prefab combo",           "",       "",              995, 1.069),
            ("label combo",      "l:gameplay combo",             "",       "",               125, 0.824),
            ("type OR combo",    "t:prefab t:script combo",      "",       "",              3050, 6.359),
            ("label OR combo",   "l:gameplay l:urgent combo",    "",       "",              172, 2.244),
            ("glob narrow",      "glob:Assets/**/Player_05000.prefab", "", "",                 1, 0.292),
            ("scoped ab",        "ab",                          "Assets", "",             18106, 4.800),
            ("scoped prefab",    "prefab",                      "Assets", "",              7970, 3.838),
            ("scoped narrow",    "player_05000",                "Assets", "",                 1, 0.132),
            ("deep-scope combo", "combo",                       "Assets/player", "",        367, 0.438),
            ("scope+type combo", "combo",                       "Assets", "GameObject",    995, 0.753),
        };

        [Test, Explicit("Heavy: builds 50k records and runs 20 iterations per query. Run on demand against BENCHMARK_BASELINE.md.")]
        public void Search_TimingStaysWithinBaseline_AndReportsAllocationsWhenAvailable()
        {
            var index = BuildCorpus(DatasetSize);
            var search = new AssetSearchService(index);
            search.WarmIndex();
            var allocationMeasurementAvailable = HasUsableThreadAllocationCounter();

            if (!allocationMeasurementAvailable)
            {
                TestContext.Out.WriteLine(
                    "allocation measurement unavailable: this Unity runtime does not expose a working GC.GetAllocatedBytesForCurrentThread counter");
            }

            var failures = new List<string>();
            foreach (var shape in Baseline)
            {
                var actualCount = search.Search(shape.Query, shape.Scope, shape.Type).Count;
                if (actualCount != shape.ExpectedCount)
                {
                    failures.Add(
                        $"[count] shape=\"{shape.Label}\" observed={actualCount} expected={shape.ExpectedCount}");
                }

                var measured = Measure(
                    search,
                    shape.Query,
                    shape.Scope,
                    shape.Type,
                    allocationMeasurementAvailable);

                var allowedMs = shape.MedianMs * TimingTolerance;
                if (measured.MedianMs > allowedMs)
                {
                    failures.Add(
                        $"[time]  shape=\"{shape.Label}\" observed={measured.MedianMs:F3}ms baseline={shape.MedianMs:F3}ms (+20% = {allowedMs:F3}ms)");
                }

                var allocation = measured.AllocBytesPerCall.HasValue
                    ? $"{measured.AllocBytesPerCall.Value}B/call"
                    : "unavailable";
                TestContext.Out.WriteLine(
                    $"shape=\"{shape.Label}\" results={actualCount} median={measured.MedianMs:F3}ms alloc={allocation} (timing baseline {shape.MedianMs:F3}ms)");
            }

            if (failures.Count > 0)
            {
                Assert.Fail("Regression detected vs BENCHMARK_BASELINE.md:\n  " + string.Join("\n  ", failures));
            }
        }

        private static (double MedianMs, long? AllocBytesPerCall) Measure(
            AssetSearchService search,
            string query,
            string folderScopePath,
            string typeName,
            bool allocationMeasurementAvailable)
        {
            for (var i = 0; i < WarmupIterations; i++)
            {
                _ = search.Search(query, folderScopePath, typeName);
            }

            // Allocation measurement: snapshot thread-local allocated bytes around the
            // full measure loop so GC activity outside the loop does not contaminate
            // the number. Per-call is total / iterations.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocStart = allocationMeasurementAvailable
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0L;

            var samples = new double[MeasureIterations];
            for (var i = 0; i < MeasureIterations; i++)
            {
                var sw = Stopwatch.StartNew();
                _ = search.Search(query, folderScopePath, typeName);
                sw.Stop();
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }

            long? allocPerCall = allocationMeasurementAvailable
                ? (GC.GetAllocatedBytesForCurrentThread() - allocStart) / MeasureIterations
                : (long?)null;

            Array.Sort(samples);
            var median = MeasureIterations % 2 == 1
                ? samples[MeasureIterations / 2]
                : 0.5 * (samples[(MeasureIterations / 2) - 1] + samples[MeasureIterations / 2]);

            return (median, allocPerCall);
        }

        private static bool HasUsableThreadAllocationCounter()
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var probe = new byte[4096];
            GC.KeepAlive(probe);
            return GC.GetAllocatedBytesForCurrentThread() > before;
        }

        // Deterministic synthetic corpus, kept byte-identical with
        // Documentation~/qa-harness/search-perf/Program.cs so the two runtimes
        // measure the same thing. Repeated runs (before/after a code change)
        // produce identical posting lists and the same sorting order.
        //
        // 2026-09-02 FIXTURE REPLACEMENT (audit §7). This used to issue
        // 7-character GUIDs ("g-" + n) and pick types from an unweighted
        // System.Random. Real Unity GUIDs are 32 lowercase hex characters, and
        // the engine folds the GUID into the any-field search text and into a
        // dedicated GUID gram index — so the old fixture understated search
        // memory by roughly an order of magnitude and hid the
        // two-characters-match-half-the-project behaviour entirely. Every
        // expected count and every timing baseline above was re-recorded
        // against this corpus; pre-2026-09-02 numbers are NOT comparable.
        //
        // System.Random is deliberately gone: this fixture must produce the
        // same corpus under Unity Mono and under .NET 8, and the seeded-Random
        // algorithm is a host-runtime detail. An ordinal-derived splitmix64
        // mixer is not.
        private static InMemoryAssetIndex BuildCorpus(int count)
        {
            var index = new InMemoryAssetIndex();
            for (var i = 0; i < count; i++)
            {
                var n = i.ToString("D5");
                var typeName = TypeForOrdinal(i);
                var w1 = Words[(int)(Mix((ulong)i, 11) % (ulong)Words.Length)];
                var w2 = Words[(int)(Mix((ulong)i, 13) % (ulong)Words.Length)];
                var name = w1 + "_" + w2 + "_" + n;
                var path = "Assets/" + w1 + "/" + w2 + "/" + name + "." + ExtensionFor(typeName);
                index.Upsert(new AssetRecord(
                    GuidFor(i),
                    path,
                    name,
                    typeName,
                    unityLabels: LabelsFor(i)));
            }

            // Ensure "player_05000" is a uniquely-resolvable record for the narrow query.
            index.Upsert(new AssetRecord(
                GuidFor(-1),
                "Assets/Players/Player_05000.prefab",
                "Player_05000",
                "GameObject"));

            return index;
        }

        private static readonly string[] Words =
        {
            "player", "enemy", "ui", "combo", "ability", "config",
            "music", "system", "shader", "icon", "sprite", "prefab"
        };

        // Weighted to roughly mirror a mid-to-large Unity project: scripts and
        // textures dominate, scenes are rare. Weights sum to 100.
        private static readonly (string TypeName, int Weight)[] TypeMix =
        {
            ("MonoScript", 26),
            ("Texture2D", 24),
            ("Material", 12),
            ("GameObject", 12),
            ("ScriptableObject", 8),
            ("AudioClip", 7),
            ("TextAsset", 6),
            ("SceneAsset", 5),
        };

        // A real Unity GUID is 32 lowercase hex characters with no visible
        // structure. Two independently mixed 64-bit words give that:
        // consecutive ordinals share no prefix and no gram, which is exactly
        // what sequential hex fails to reproduce.
        private static string GuidFor(int ordinal)
        {
            var seed = unchecked((ulong)ordinal);
            return Mix(seed, 0x51ED).ToString("x16") + Mix(seed, 0x2F73).ToString("x16");
        }

        // splitmix64.
        private static ulong Mix(ulong value, ulong salt)
        {
            var z = unchecked((value * 0x9E3779B97F4A7C15UL) + (salt * 0xD1B54A32D192ED03UL));
            z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
            z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
            return z ^ (z >> 31);
        }

        private static string TypeForOrdinal(int ordinal)
        {
            var roll = (int)(Mix((ulong)ordinal, 7) % 100UL);
            var cumulative = 0;
            for (var i = 0; i < TypeMix.Length; i++)
            {
                cumulative += TypeMix[i].Weight;
                if (roll < cumulative)
                {
                    return TypeMix[i].TypeName;
                }
            }

            return TypeMix[TypeMix.Length - 1].TypeName;
        }

        // ~2% of records carry a label, which is what a real project looks
        // like — the previous fixture labelled ~15%.
        private static IReadOnlyList<string> LabelsFor(int ordinal)
        {
            var gameplay = ordinal % 61 == 0;
            var urgent = ordinal % 149 == 0;
            if (gameplay && urgent)
            {
                return new[] { "Gameplay", "Urgent" };
            }

            if (gameplay)
            {
                return new[] { "Gameplay" };
            }

            return urgent
                ? new[] { "Urgent" }
                : Array.Empty<string>();
        }

        private static string ExtensionFor(string typeName)
        {
            switch (typeName)
            {
                case "MonoScript": return "cs";
                case "Texture2D": return "png";
                case "GameObject": return "prefab";
                case "Material": return "mat";
                case "SceneAsset": return "unity";
                case "TextAsset": return "txt";
                case "ScriptableObject": return "asset";
                case "AudioClip": return "wav";
                default: return "asset";
            }
        }
    }
}
