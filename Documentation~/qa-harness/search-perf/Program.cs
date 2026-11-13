// Manual-QA-Test-Plan.md section 28 - PRF-03, the "KB per keystroke" half, plus
// the memory and post-upsert instrumentation the 2026-09-02 business-logic audit
// (§3.10, §3.11, §3.12) is measured against.
//
// Drives the REAL AssetSearchService (the package's own Core sources compile
// into this assembly) over the SAME corpus the package's [Explicit]
// AssetSearchServicePerformanceRegressionTests builds, using the same per-shape
// protocol (3 warmups, then 20 timed iterations, median), and prints median ms
// and allocated bytes per Search() call.
//
// The corpus builder below is transcribed from
// Tests/Editor/Core/Search/AssetSearchServicePerformanceRegressionTests.cs
// (BuildCorpus / LabelsFor / TypeForOrdinal / ExtensionFor). It is a FIXTURE,
// not the thing under test - the thing under test is AssetSearchService, which
// is real product code. Fidelity is not assumed: at the baselined corpus size
// every shape asserts the result count documented in BENCHMARK_BASELINE.md, so
// a corpus that drifted from the Unity test's would fail the run instead of
// silently producing plausible numbers.
//
// 2026-09-02 FIXTURE REPLACEMENT (audit §7). The previous fixture issued
// 7-character GUIDs ("g-" + n) and an unweighted type mix. Real Unity GUIDs are
// 32 lowercase hex characters, and the engine folds the GUID into the any-field
// search text and into a dedicated GUID gram index - so the old fixture
// understated search memory by roughly an order of magnitude and hid the
// two-character-matches-half-the-project behaviour entirely. This fixture now
// issues 32-hex GUIDs derived deterministically from the record ordinal through
// a splitmix64 mixer (real trigram diversity, no sequential-hex collapse), a
// weighted extension mix, and ~2% labelled records. Numbers produced before
// 2026-09-02 are NOT comparable to numbers produced after it.
//
// System.Random is deliberately not used any more: the fixture must produce a
// byte-identical corpus under Unity Mono and under .NET 8, and an index-derived
// mixer makes that independent of the host runtime's RNG implementation.
//
// Nothing here asserts a timing or an allocation threshold. PRF-03 says
// "near zero", which is a human word, and .NET 8 timings are not comparable to
// the Mono baselines anyway. Measurements are printed; only fidelity is graded.
//
// Usage:  dotnet run [-- <corpusSize> [<folderCount>]]
//   corpusSize  default 50000 (the size the fidelity gate is baselined at)
//   folderCount default 10000 (the FolderSearchIndex section)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.Search;

internal static class Program
{
    // The corpus size the result counts in BENCHMARK_BASELINE.md were recorded
    // at. The fidelity gate only runs at this size; other sizes are measurement
    // runs and report their counts without grading them.
    private const int BaselinedDatasetSize = 50000;
    private const int DefaultFolderCount = 10000;
    private const int WarmupIterations = 3;
    private const int MeasureIterations = 20;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    // (Label, Query, Scope, Type, ExpectedCount) - the 14 shapes and the counts
    // recorded in Tests/Editor/Core/Search/BENCHMARK_BASELINE.md. Counts are the
    // fidelity gate; the Mono medians are printed alongside for orientation only.
    // Both columns were re-recorded on 2026-09-02 against the replacement
    // fixture; the pre-2026-09-02 values are not comparable.
    //
    // 2026-09-02 (audit 3.11a / plan T3.17): "bare ab" and "scoped ab" moved
    // 21755 -> 18106 because an unqualified term no longer reads the 32-hex GUID
    // as a substring. The other 12 shapes were unchanged by that edit, which is
    // the evidence that it is confined to the unqualified GUID leg.
    private static readonly (string Label, string Query, string Scope, string Type, int ExpectedCount, double MonoBaselineMs)[] Shapes =
    {
        ("bare ab", "ab", "", "", 18106, 0.496),
        ("bare prefab", "prefab", "", "", 7970, 2.764),
        ("path query", "path:assets/players/player_05000", "", "", 1, 0.967),
        ("bare narrow", "player_05000", "", "", 1, 0.134),
        ("token type combo", "type:prefab combo", "", "", 995, 1.069),
        ("label combo", "l:gameplay combo", "", "", 125, 0.824),
        ("type OR combo", "t:prefab t:script combo", "", "", 3050, 6.359),
        ("label OR combo", "l:gameplay l:urgent combo", "", "", 172, 2.244),
        ("glob narrow", "glob:Assets/**/Player_05000.prefab", "", "", 1, 0.292),
        ("scoped ab", "ab", "Assets", "", 18106, 4.800),
        ("scoped prefab", "prefab", "Assets", "", 7970, 3.838),
        ("scoped narrow", "player_05000", "Assets", "", 1, 0.132),
        ("deep-scope combo", "combo", "Assets/player", "", 367, 0.438),
        ("scope+type combo", "combo", "Assets", "GameObject", 995, 0.753),
    };

    private static int _failures;

    private static int Main(string[] args)
    {
        // The numbers go into a written record, so print them with a '.' decimal
        // point regardless of the host's locale.
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        var datasetSize = ParseArg(args, 0, BaselinedDatasetSize);
        var folderCount = ParseArg(args, 1, DefaultFolderCount);
        var gradeFidelity = datasetSize == BaselinedDatasetSize;

        Console.WriteLine("=== PRF-03 + audit §3.10/§3.11/§3.12: search cost, memory and post-upsert latency (.NET 8 side-harness) ===");
        Console.WriteLine($"runtime={Environment.Version} gcServer={System.Runtime.GCSettings.IsServerGC} corpus={datasetSize} folders={folderCount} warmups={WarmupIterations} iterations={MeasureIterations}");
        Console.WriteLine(gradeFidelity
            ? "corpus fidelity: GRADED (this is the baselined corpus size)"
            : $"corpus fidelity: NOT GRADED (baselined at {BaselinedDatasetSize}); counts below are measurements only");
        Console.WriteLine();

        RunSearchSections(datasetSize, gradeFidelity);

        // The search sections' 200-450 MB live graph must be unreachable before
        // the folder section takes its own heap baseline; measuring the folder
        // postings alongside it reported 931 B/folder at 20k against 15 KB at
        // 10k, which is a measurement artefact, not a discovery. Keeping those
        // objects in a separate method scope is what makes them collectable.
        Console.WriteLine();
        ReportFolderIndex(folderCount);
        Console.WriteLine();

        if (!gradeFidelity)
        {
            Console.WriteLine("CORPUS FIDELITY: not graded at this corpus size.");
            return 0;
        }

        Console.WriteLine(_failures == 0
            ? "CORPUS FIDELITY: all 14 shapes reproduce the counts in BENCHMARK_BASELINE.md."
            : $"CORPUS FIDELITY FAILED: {_failures} shape(s) disagree - the measurements above are NOT comparable to the Unity test's.");
        return _failures == 0 ? 0 : 1;
    }

    private static void RunSearchSections(int datasetSize, bool gradeFidelity)
    {
        var heapBeforeCorpus = SettledHeapBytes();
        var buildWatch = Stopwatch.StartNew();
        var index = BuildCorpus(datasetSize);
        buildWatch.Stop();
        var heapAfterCorpus = SettledHeapBytes();

        var search = new AssetSearchService(index);
        var warmWatch = Stopwatch.StartNew();
        search.WarmIndex();
        warmWatch.Stop();
        var heapAfterWarm = SettledHeapBytes();

        var catalogHeapMb = (heapAfterCorpus - heapBeforeCorpus) / BytesPerMegabyte;
        var searchHeapMb = (heapAfterWarm - heapAfterCorpus) / BytesPerMegabyte;

        Console.WriteLine("--- heap and build cost ---");
        Console.WriteLine($"OBS  corpus build {buildWatch.Elapsed.TotalMilliseconds:F1}ms, WarmIndex {warmWatch.Elapsed.TotalMilliseconds:F1}ms");
        Console.WriteLine($"OBS  catalog heap {catalogHeapMb:F1} MB ({catalogHeapMb * BytesPerMegabyte / datasetSize:F0} B/record)");
        Console.WriteLine($"OBS  search-index heap {searchHeapMb:F1} MB ({searchHeapMb * BytesPerMegabyte / datasetSize:F0} B/record)");
        Console.WriteLine();

        // Process-wide JIT pass so the first shape is not charged for compiling
        // the engine. The Unity test does not need this (Mono pre-JITs on the
        // warmups of every shape and its first shape is the same one every run),
        // and it changes no allocation number.
        foreach (var shape in Shapes)
        {
            _ = search.Search(shape.Query, shape.Scope, shape.Type);
        }

        Console.WriteLine("--- per-shape cost ---");
        Console.WriteLine("| shape | results | median ms (.NET 8) | B/call | KB/call | Mono baseline ms |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: |");

        foreach (var shape in Shapes)
        {
            var actualCount = search.Search(shape.Query, shape.Scope, shape.Type).Count;
            var measured = Measure(search, shape.Query, shape.Scope, shape.Type);

            Console.WriteLine(
                $"| {shape.Label} | {actualCount} | {measured.MedianMs:F3} | {measured.AllocBytesPerCall} | {measured.AllocBytesPerCall / 1024.0:F1} | {shape.MonoBaselineMs:F2} |");

            if (gradeFidelity)
            {
                Check(
                    $"PRF-03 corpus fidelity: shape \"{shape.Label}\" result count",
                    actualCount == shape.ExpectedCount,
                    $"{actualCount} (documented {shape.ExpectedCount})");
            }
        }

        Console.WriteLine();
        ReportPostUpsertQuery(index, search, datasetSize);
    }

    // Audit §3.10: AddIndexedRecord sets _alphabeticalOrdinalsDirty, which is
    // cleared only by EnsureAlphabeticalOrdinalsAreCurrent (and re-set by the
    // very next catalog delta) - so the FIRST broad query after any import pays
    // a full-corpus re-sort.
    //
    // This must be measured as a single call, not as a median: the re-sort
    // happens once per delta, so a warm-up-plus-20-iterations median hides the
    // entire finding behind 22 already-sorted calls. The loop below therefore
    // re-dirties the index before every timed call and takes the median of the
    // FIRST calls, which is what a user typing during a bulk import actually
    // pays. The clean baseline is the same single-call protocol with no delta
    // in between.
    private static void ReportPostUpsertQuery(
        InMemoryAssetIndex index,
        AssetSearchService search,
        int datasetSize)
    {
        const string Query = "player";
        const int Repeats = 9;

        Console.WriteLine("--- audit §3.10: one broad query, first call after one incremental upsert ---");

        // Baseline: the index is already sorted and stays sorted, so every one
        // of these calls takes the fast path.
        _ = search.Search(Query, string.Empty, string.Empty);
        var cleanSamples = new double[Repeats];
        for (var i = 0; i < Repeats; i++)
        {
            cleanSamples[i] = TimeOneSearch(search, Query);
        }

        var dirtySamples = new double[Repeats];
        for (var i = 0; i < Repeats; i++)
        {
            var probe = new AssetRecord(
                GuidFor(datasetSize + 7 + i),
                "Assets/player/probe/player_upsert_probe_" + i.ToString("D3", CultureInfo.InvariantCulture) + ".prefab",
                "player_upsert_probe_" + i.ToString("D3", CultureInfo.InvariantCulture),
                "GameObject");
            var delta = index.ApplyFileChanges(new[] { probe }, Array.Empty<string>());
            search.ApplyCatalogDelta(delta);
            dirtySamples[i] = TimeOneSearch(search, Query);
        }

        var clean = Median(cleanSamples);
        var dirty = Median(dirtySamples);
        var ratio = clean > 0.0 ? dirty / clean : 0.0;
        Console.WriteLine(
            $"OBS  \"{Query}\" single call {clean:F3}ms on a settled index -> {dirty:F3}ms as the first call after one upsert ({ratio:F1}x)");
        Console.WriteLine(
            $"OBS  (medians of {Repeats} single calls each; the post-upsert cost recurs after EVERY catalog delta)");
    }

    private static double TimeOneSearch(AssetSearchService search, string query)
    {
        var sw = Stopwatch.StartNew();
        _ = search.Search(query, string.Empty, string.Empty);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static double Median(double[] samples)
    {
        var copy = (double[])samples.Clone();
        Array.Sort(copy);
        return copy.Length % 2 == 1
            ? copy[copy.Length / 2]
            : 0.5 * (copy[(copy.Length / 2) - 1] + copy[copy.Length / 2]);
    }

    // Audit §3.12: FolderSearchIndex posts gram lengths 1, 2 AND 3 of five
    // fields into HashSet<string> postings that hold full path strings. The
    // per-folder cost is the finding; the folders carry real 32-hex GUIDs here,
    // which the audit's own probe did not.
    private static void ReportFolderIndex(int folderCount)
    {
        Console.WriteLine("--- audit §3.12: FolderSearchIndex cost per folder ---");
        if (folderCount <= 0)
        {
            Console.WriteLine("OBS  skipped (folderCount <= 0)");
            return;
        }

        var folders = BuildFolders(folderCount);
        var heapBefore = SettledHeapBytes();
        var folderIndex = new FolderSearchIndex();
        var watch = Stopwatch.StartNew();
        folderIndex.Replace(folders);
        watch.Stop();
        var heapAfter = SettledHeapBytes();
        GC.KeepAlive(folderIndex);

        var bytes = heapAfter - heapBefore;
        Console.WriteLine(
            $"OBS  {folderCount} folders: Replace {watch.Elapsed.TotalMilliseconds:F1}ms, postings {bytes / BytesPerMegabyte:F1} MB ({bytes / (double)folderCount:F0} B/folder)");
    }

    private static void Check(string what, bool ok, string observed)
    {
        if (ok)
        {
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL {what}   -> {observed}");
    }

    private static int ParseArg(string[] args, int position, int fallback)
    {
        if (args == null
            || position >= args.Length
            || !int.TryParse(args[position], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 0)
        {
            return fallback;
        }

        return value;
    }

    // GetTotalMemory(true) after a settled, LOH-compacting full collection.
    //
    // Two passes because the first collection can leave finalizable objects
    // queued, which would be charged to whichever section measures next. The
    // LOH compaction is load-bearing, not hygiene: the index builders allocate
    // large transient arrays, and without it an uncompacted large-object heap
    // inflates the "after corpus" reading and then absorbs the search index's
    // own large arrays into the already-reserved space — which reported a
    // *smaller* search-index heap at 100k than at 50k before this was added.
    private static long SettledHeapBytes()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return GC.GetTotalMemory(true);
    }

    // Verbatim protocol from AssetSearchServicePerformanceRegressionTests.Measure.
    private static (double MedianMs, long AllocBytesPerCall) Measure(
        AssetSearchService search, string query, string folderScopePath, string typeName)
    {
        for (var i = 0; i < WarmupIterations; i++)
        {
            _ = search.Search(query, folderScopePath, typeName);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocStart = GC.GetAllocatedBytesForCurrentThread();

        var samples = new double[MeasureIterations];
        for (var i = 0; i < MeasureIterations; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = search.Search(query, folderScopePath, typeName);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        var allocEnd = GC.GetAllocatedBytesForCurrentThread();
        var allocPerCall = (allocEnd - allocStart) / MeasureIterations;

        Array.Sort(samples);
        var median = MeasureIterations % 2 == 1
            ? samples[MeasureIterations / 2]
            : 0.5 * (samples[(MeasureIterations / 2) - 1] + samples[MeasureIterations / 2]);

        return (median, allocPerCall);
    }

    // ---- fixture, kept byte-identical with the Unity test's BuildCorpus ----

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

    private static InMemoryAssetIndex BuildCorpus(int count)
    {
        var index = new InMemoryAssetIndex();
        for (var i = 0; i < count; i++)
        {
            var n = i.ToString("D5", CultureInfo.InvariantCulture);
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

    private static IReadOnlyList<ZepFolderEntry> BuildFolders(int count)
    {
        var folders = new List<ZepFolderEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var n = i.ToString("D5", CultureInfo.InvariantCulture);
            var w1 = Words[(int)(Mix((ulong)i, 17) % (ulong)Words.Length)];
            var w2 = Words[(int)(Mix((ulong)i, 19) % (ulong)Words.Length)];
            var name = w2 + "_" + n;
            folders.Add(new ZepFolderEntry(
                name,
                "Assets/" + w1 + "/" + name,
                2,
                false,
                guid: GuidFor(1_000_000 + i)));
        }

        return folders;
    }

    // A real Unity GUID is 32 lowercase hex characters with no visible
    // structure. Two independently mixed 64-bit words give that: consecutive
    // ordinals share no prefix and no gram, which is exactly what sequential
    // hex ("0000...0001") fails to reproduce and why the old fixture's postings
    // collapsed onto a handful of grams.
    private static string GuidFor(int ordinal)
    {
        var seed = unchecked((ulong)ordinal);
        return Mix(seed, 0x51ED).ToString("x16", CultureInfo.InvariantCulture)
            + Mix(seed, 0x2F73).ToString("x16", CultureInfo.InvariantCulture);
    }

    // splitmix64. Deterministic, runtime-independent (System.Random's seeded
    // algorithm is a host-runtime detail; this is not), and well distributed in
    // every hex digit, which is what the GUID grams need.
    private static ulong Mix(ulong value, ulong salt)
    {
        var z = unchecked(value * 0x9E3779B97F4A7C15UL + salt * 0xD1B54A32D192ED03UL);
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

    // ~2% of records carry a label, which is what a real project looks like -
    // the previous fixture labelled ~15% and made label queries the cheapest
    // broad shape in the table for no reason that matched reality.
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
