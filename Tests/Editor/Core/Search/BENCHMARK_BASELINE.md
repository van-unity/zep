# AssetSearchService benchmark baseline

Companion to `AssetSearchServicePerformanceRegressionTests` (same folder). That
test is `[Explicit]`: run it on demand from the Unity Test Runner when touching
the search engine, and update the baselines here and in the test when movement
is intentional.

> The original baseline document was lost in the repository incident (the file
> sat empty from the initial post-incident commit until 2026-07-09). The tables
> below re-establish it.

## ⚠ 2026-09-02: the corpus was replaced. Older numbers are not comparable.

The 2026-09-02 business-logic audit (§7) found the fixture was issuing
**7-character GUIDs** (`"g-" + n`) and picking types from an unweighted
`System.Random`. Real Unity GUIDs are 32 lowercase hex characters, and the
engine folds the GUID into the any-field search text *and* into a dedicated
GUID gram index — so the old fixture understated search memory by roughly an
order of magnitude and hid the "two hex-ish characters match half the project"
behaviour completely. That is why §3.11 was not caught by this benchmark.

The replacement fixture (identical in the Unity test and in
`Documentation~/qa-harness/search-perf`):

- **32-hex lowercase GUIDs**, derived from the record ordinal through a
  splitmix64 mixer, so consecutive ordinals share no prefix and no gram. Not
  sequential hex — that collapses onto a handful of grams and reproduces the old
  fixture's problem under a new name.
- **No `System.Random`.** The two copies must produce the same corpus under
  Unity Mono and under .NET 8; a seeded RNG's algorithm is a host-runtime
  detail, an ordinal-derived mixer is not.
- **Weighted type/extension mix** (MonoScript 26%, Texture2D 24%, Material 12%,
  GameObject 12%, ScriptableObject 8%, AudioClip 7%, TextAsset 6%, SceneAsset
  5%) instead of a uniform draw over eight types.
- **~2% labelled records** (`Gameplay` every 61st, `Urgent` every 149th) instead
  of ~15%.

Consequences for anyone reading older rows: **every number recorded before
2026-09-02 in this file — result counts, timings and allocations alike — was
produced against the old corpus and cannot be compared with a number produced
after it.** The historical tables further down are kept as a record of what was
measured then, not as baselines to compare against.

### Result counts (re-recorded 2026-09-02, .NET 8 harness, 50,000 records)

These are the fidelity gate: both the Unity test and the harness assert them.

| shape            | query                                  | scope           | type         | results (new) | results (old, void) |
|------------------|----------------------------------------|-----------------|--------------|--------------:|--------------------:|
| bare ab          | `ab`                                   |                 |              | 18,106        | 19,837 |
| bare prefab      | `prefab`                               |                 |              | 7,970         | 8,170  |
| path query       | `path:assets/players/player_05000`     |                 |              | 1             | 1      |
| bare narrow      | `player_05000`                         |                 |              | 1             | 1      |
| token type combo | `type:prefab combo`                    |                 |              | 995           | 1,044  |
| label combo      | `l:gameplay combo`                     |                 |              | 125           | 806    |
| type OR combo    | `t:prefab t:script combo`              |                 |              | 3,050         | 2,048  |
| label OR combo   | `l:gameplay l:urgent combo`            |                 |              | 172           | 1,252  |
| glob narrow      | `glob:Assets/**/Player_05000.prefab`   |                 |              | 1             | 1      |
| scoped ab        | `ab`                                   | `Assets`        |              | 18,106        | 19,837 |
| scoped prefab    | `prefab`                               | `Assets`        |              | 7,970         | 8,170  |
| scoped narrow    | `player_05000`                         | `Assets`        |              | 1             | 1      |
| deep-scope combo | `combo`                                | `Assets/player` |              | 367           | 355    |
| scope+type combo | `combo`                                | `Assets`        | `GameObject` | 995           | 1,044  |

`bare ab` matched **43.5%** of the corpus (21,755 / 50,001) when this fixture
replaced the old one — the audit's §3.11 correctness half, which the old
7-character GUIDs could not produce.

**Re-recorded 2026-09-02 for T3.17 (§3.11a).** Both `ab` rows moved 21,755 →
**18,106** (36.2%): an unqualified term no longer reads the 32-hex GUID as a
substring, so the ~3.6k records that matched `ab` only through their GUID are
gone. The remaining 36% is the fixture's own doing — two of its twelve name
words (`ability`, `prefab`) and one of its eight type names
(`ScriptableObject`) contain `ab` — not residual GUID matching. **The other 12
shapes were unchanged by that edit**, which is the evidence that it is confined
to the unqualified GUID leg.

### ⚠ The Mono timing column has NOT been re-measured

`AssetSearchServicePerformanceRegressionTests.Baseline` still carries the
2026-08-08 Mono medians, which were captured against the **old** corpus. They
are left in place so the shape table stays complete, but a `[time]` failure
from that test is not evidence of a regression until the column is re-recorded
on Mono against this corpus. The counts in that same table *are* current.

### .NET 8 side-harness, 2026-09-02 (replacement corpus)

Apple M3 Pro, macOS 26.5.1, .NET 8.0.1, workstation GC, non-concurrent.
`Documentation~/qa-harness/search-perf`, `dotnet run -c Release -- <records> <folders>`.
Timings on .NET 8 are not comparable to Mono medians; these are the red-state
record for audit §3.10, §3.11 and §3.12.

| corpus  | catalog heap | search-index heap | per record | `WarmIndex` | `"player"` settled | `"player"` first call after one upsert |
|--------:|-------------:|------------------:|-----------:|------------:|-------------------:|---------------------------------------:|
| 50,000  | 97.9 MB      | **139.4 MB**      | 2,923 B    | 755 ms      | 0.252 ms           | **22.1 ms** (88×)                       |
| 100,000 | 195.7 MB     | **259.0 MB**      | 2,716 B    | 1,337 ms    | 0.544 ms           | **36.0 ms** (66×)                       |

The post-upsert column is measured as a **single** call after a one-record
`ApplyCatalogDelta`, repeated nine times with a fresh delta before each timed
call. It cannot be measured as a warm median: the full-corpus re-sort happens
once per delta, so 3 warmups + 20 iterations hide the entire finding behind 22
already-sorted calls.

`FolderSearchIndex` (`BrowseProjectionService.cs`), folders carrying real 32-hex
GUIDs:

| folders | `Replace` | postings  | per folder |
|--------:|----------:|----------:|-----------:|
| 5,000   | 217 ms    | 78.2 MB   | 16,394 B   |
| 10,000  | 478 ms    | 145.8 MB  | 15,289 B   |
| 20,000  | 1,002 ms  | 273.7 MB  | 14,351 B   |

The audit measured 8.6 KB/folder with *empty* folder GUIDs and predicted real
GUIDs would be worse; they are, by ~1.7×.

### .NET 8 side-harness, 2026-09-02 after T1.13 and T1.14

Same host, same corpus, same protocol. T1.13 merges a delta's new ordinals into
the maintained alphabetical order inside `ApplyCatalogDelta` instead of leaving
the order dirty for the next broad query; T1.14 posts trigrams only in
`FolderSearchIndex`. Neither changes a result: the 14 shapes still reproduce
their documented counts, and `BrowseProjectionServiceTests`
`FolderSearch_ForShortTokens_*` pins the 1-, 2- and 3-character folder queries.

| corpus  | search-index heap | `"player"` settled | `"player"` first call after one upsert |
|--------:|------------------:|-------------------:|---------------------------------------:|
| 50,000  | 139.4 MB          | 0.265 ms           | **0.359 ms** (1.4×, was 22.1 ms / 88×)  |
| 100,000 | 259.0 MB          | 0.571 ms           | **1.156 ms** (2.0×, was 36.4 ms / 75×)  |

Search-index heap is unchanged on purpose — §3.11 is a separate task. The work
T1.13 removed from the query did not simply move into the delta: a one-upsert
`ApplyCatalogDelta` at 100k costs **0.23 ms**, measured with a throwaway probe
copy of this harness. A plain linear merge (the audit's wording) cost 3.06 ms
there, because a single insertion point took one `OrdinalIgnoreCase` compare per
live record; the shipped merge binary-searches each insertion point instead. A
5,000-record delta is 51 ms either way — that is the gram indexing, not the merge.

| folders | `Replace` | postings | per folder | vs before |
|--------:|----------:|---------:|-----------:|----------:|
| 5,000   | 73 ms     | 33.4 MB  | 7,011 B    | 2.34×     |
| 10,000  | 175 ms    | 57.5 MB  | 6,027 B    | 2.54×     |
| 20,000  | 420 ms    | 112.2 MB | 5,883 B    | 2.44×     |

`Replace` drops with it (10k folders: 478 ms → 175 ms). The remaining postings
are the trigram level of five fields plus the 32-hex GUID, which is why the
saving is ~2.5× rather than the 3× a naive gram count predicts.

### .NET 8 side-harness, 2026-09-02 after T3.17 (§3.11a + 4.33)

Same host, same corpus, same protocol. T3.17 drops `NormalizedGuid` from
`AssetRecord.BuildNormalizedSearchText`, makes an unqualified GUID match a
prefix of at least four characters, and verifies the single-unqualified-term
query shape with one `IndexOf` over the any-field text instead of a
`SearchDocument` plus five field scans.

| corpus  | catalog heap | search-index heap | per record | `WarmIndex` | `"pla"` | `"prefab"` |
|--------:|-------------:|------------------:|-----------:|------------:|--------:|-----------:|
| 100,000 | 195.7 → **183.2 MB** | 259.0 → **189.9 MB** | 2,716 → **1,991 B** | 1,264 → 1,001 ms | 2.319 → **0.264 ms** (8.8×) | 2.245 → **0.478 ms** (4.7×) |
| 50,000  | 97.9 → **92.0 MB**  | 139.4 → **104.7 MB** | 2,923 → **2,196 B** | 783 → 700 ms | — | 1.265 → **0.269 ms** |

`"pla"` returns the same 17,233 records before and after, and `"p"` the same
67,153 (a GUID is hex, so it never carried a `p` in the first place). The
before column was produced by running this harness with `-p:PkgRoot=` pointed
at a copy of `Editor/Core` with T3.17's four edits reversed, so both columns
come from the same binary, the same corpus and the same protocol.

`"pla"` and `"p"` are not in the graded shape list; they were measured with a
scratch copy of this harness carrying two extra ungraded shapes.

### .NET 8 side-harness, 2026-09-02 after T3.20 (4.36)

`Search` fills a reused growing `int[]` instead of allocating one sized by the
CANDIDATE count per call. The array a result list is handed still has to be its
own — a split view keeps two panes' result lists live over one
`AssetSearchService` — so a sparse result is copied out of the buffer and a
result that already fills it takes it. Measured at 100,000 records with three
ungraded probe shapes on a scratch copy of this harness:

| probe shape | query | results / candidates | B/call before | B/call after |
|---|---|---:|---:|---:|
| zero result  | `pla -pla` | 0 / 17,233 | 69,497 | **537** (129×) |
| sparse result | `pla` minus the other eleven corpus words | 677 / 17,233 | 70,897 | **4,673** (15×) |
| dense result | `p` | 67,153 / 67,153 | 268,961 | 268,961 (unchanged) |

The dense row is the honest limit of this fix, and it **partly refutes audit
4.36**. That finding assumed "only one result list is ever live per pane", but
there is one `AssetSearchService` per controller and up to two live panes, each
retaining its own `OrdinalResultList` inside a UI-bound `LazyAssetEntryList`
(`ZepController.ApplyProjection` refreshes every live pane;
`ApplyPaneProjection` refreshes one while the other stays bound). A broad query's
ordinal array therefore has to outlive the call, and for a query that matches
nearly all of its candidates that array *is* the result. What the buffer removes
is the waste: the array allocated for candidates that did not match — which is
the state of every keystroke that has not yet resolved to something.

### .NET 8 side-harness, 2026-09-02 after T3.23 (§3.11b/c)

`_guidGramIndex` now carries only the first eight characters of each GUID, and
the type field's dedicated one- and two-gram indices are gone — a 1- or
2-character `t:` scans the distinct type search texts (a few dozen keys) and
unions their postings instead. All 14 graded shapes still reproduce their
documented counts.

| corpus  | search-index heap after T3.17 | after T3.23 | per record | `WarmIndex` |
|--------:|------------------------------:|------------:|-----------:|------------:|
| 50,000  | 104.7 MB | **80.9 MB** | 2,196 → **1,696 B** | 700 → 454 ms |
| 100,000 | 189.9 MB | **145.2 MB** | 1,991 → **1,523 B** | 1,001 → 729 ms |

Cumulative with T3.17: **259.0 → 145.2 MB at 100k (−44%)**, 2,716 → 1,523 B per
record. Extrapolated to the spec's 500k target that is ~1.33 GB → ~0.74 GB.

The cost side, stated rather than buried. A 1- or 2-character `t:` used to be a
single dictionary lookup returning a prebuilt posting list; when more than one
distinct type text matches, it now also allocates and sorts the merged candidate
array (ungraded probe shapes at 100k): `t:cr` (2 matching type texts, 34,023
candidates) 6.90 → 7.04 ms and 136,505 → 272,625 B/call; `t:c` (8 matching type
texts, the whole corpus) 9.50 → 11.69 ms and 400,401 → 800,433 B/call. When
exactly one type text matches, its posting list is handed over untouched and
there is no extra allocation at all. That is a transient keystroke cost on the
way to a real `t:` value, traded for ~45 MB of standing memory at 100k records.

Heap figures come from `GC.GetTotalMemory(true)` deltas taken after a
**LOH-compacting** collection. Without the compaction the 100k search index
reported *less* memory than the 50k one, because the corpus builder's large
transient arrays left uncompacted LOH space that the search index then reused.
For the same reason the folder section runs after the search sections' object
graph has gone out of scope.

### .NET 8 side-harness, 2026-09-02 after T4.7 (4.16) — CATALOG heap, not search heap

T4.7 changed `AssetRecord`, not the search index: `NormalizeText` returns the
input instance when the value is already ASCII lower-case (so `NormalizedGuid`
aliases `Guid` and `NormalizedAssetPath` aliases `AssetPath` for the corpus's
lower-case GUIDs and paths), and `NormalizedTypeSearchText` is interned through
its own dictionary because its cardinality is the number of distinct type shapes,
not the number of records. The **search-index** heap is therefore unchanged and
the per-shape timings and result counts are untouched.

Measured with `dotnet run -c Release -- 100000 1000` on the same host:

| corpus  | catalog heap before T4.7 | after T4.7 | per record | search-index heap |
|--------:|-------------------------:|-----------:|-----------:|------------------:|
| 100,000 | 183.2 MB | **140.1 MB** | 1,921 → **1,469 B** | 145.2 MB (unchanged) |

−43.1 MB, −23.5% of the catalog's standing memory at 100k records, with no
change to any normalised string's VALUE — only to how many instances hold it.

## How the numbers are produced

- Corpus: 50,000 synthetic records built by `BuildCorpus` in the test (mirrored
  byte-for-byte in the harness) — deterministic posting lists across runs and
  across runtimes.
- Correctness: every timed shape first asserts its deterministic result count, so
  a false-negative regression cannot pass by making the query artificially fast.
- Timing: median `Search()` wall time across 20 iterations after 3 warmups.
- Allocations: the test first probes `GC.GetAllocatedBytesForCurrentThread()`
  with a controlled allocation. When the active Unity runtime exposes a working
  counter, per-call bytes are reported as diagnostic output. On Unity Mono the
  API commonly remains at zero, so the test explicitly reports allocations as
  unavailable and performs no byte comparison. The .NET 8 harness carries the
  real allocation measurements; zero is never presented as a measured
  allocation baseline.
- Shapes cover bare queries and the production shape: the panel always passes
  `folderScopePath` (the default search scope resolves to `"Assets"`), plus
  `typeName` when a type filter is active. Dedicated shapes cover a Unity-label
  filter, repeated type/label OR groups, and a narrow `**/` glob.

## Historical Unity Mono baselines (2026-08-08, OLD corpus — not a current gate)

Superseded for result counts by the 2026-09-02 table above; the timings here
have not been re-measured against the replacement corpus.

Captured 2026-08-08, Unity batch mode, Mono NET_4_6, Apple M3 Pro, macOS
26.5.1, Unity 6000.3.10f1. These are the medians of three back-to-back runs
after Phase 5 retained the Unity-style grammar and narrow indices while
switching broad results from object references to stable ordinal arrays. A
lone one/two-character unqualified partial term now trusts its exact posting
instead of re-verifying the same substring against every candidate. The test
allows +20% timing variance. The prior July absolute baseline also failed
against the untouched Phase-4 control on this updated host; the same-session
control was slower than Phase 5 on every broad production shape.

| shape            | query                                  | scope           | type         | results | median ms |
|------------------|----------------------------------------|-----------------|--------------|---------|-----------|
| bare ab          | `ab`                                   |                 |              | 19,837  | 0.496     |
| bare prefab      | `prefab`                               |                 |              | 8,170   | 2.764     |
| path query       | `path:assets/players/player_05000`     |                 |              | 1       | 0.967     |
| bare narrow      | `player_05000`                         |                 |              | 1       | 0.134     |
| token type combo | `type:prefab combo`                    |                 |              | 1,044   | 1.069     |
| label combo      | `l:gameplay combo`                     |                 |              | 806     | 0.824     |
| type OR combo    | `t:prefab t:script combo`              |                 |              | 2,048   | 6.359     |
| label OR combo   | `l:gameplay l:urgent combo`            |                 |              | 1,252   | 2.244     |
| glob narrow      | `glob:Assets/**/Player_05000.prefab`   |                 |              | 1       | 0.292     |
| scoped ab        | `ab`                                   | `Assets`        |              | 19,837  | 4.800     |
| scoped prefab    | `prefab`                               | `Assets`        |              | 8,170   | 3.838     |
| scoped narrow    | `player_05000`                         | `Assets`        |              | 1       | 0.132     |
| deep-scope combo | `combo`                                | `Assets/player` |              | 355     | 0.438     |
| scope+type combo | `combo`                                | `Assets`        | `GameObject` | 1,044   | 0.753     |

## Historical .NET 8 side-harness reference (2026-07-09)

This predates the corrected real-Unity-type corpus and the expanded query
grammar. It is retained only as allocation evidence for the scope-candidate
memo + root-scope skip; its timings and result counts are not current baselines
and are not comparable 1:1 with Mono timings.

| shape            | before ms | after ms | before B/call | after B/call | results |
|------------------|-----------|----------|---------------|--------------|---------|
| bare ab          | 0.432     | 0.128    | 227,761       | 227,721      | 28,433  |
| scoped ab        | 2.477     | 0.529    | 541,593       | 227,761      | 28,433  |
| bare prefab      | 3.378     | 2.701    | 224,081       | 224,041      | 18,646  |
| scoped prefab    | 2.782     | 2.764    | 498,761       | 224,081      | 18,646  |
| bare narrow      | 0.034     | 0.019    | 785           | 745          | 1       |
| scoped narrow    | 0.918     | 0.017    | 200,889       | 785          | 1       |
| deep-scope combo | 1.009     | 0.138    | 104,001       | 54,665       | 355     |
| scope+type combo | 1.030     | 0.187    | 293,881       | 69,121       | 992     |
| token type combo | 0.245     | 0.226    | 93,809        | 93,769       | 992     |
| bare path query  | 0.615     | 0.137    | 385           | 345          | 1       |

Reading the table: pre-fix, the default `"Assets"` scope cost ~0.9–2 ms and
200–300 KB of allocation per keystroke on top of any query (the scope's trigram
posting lists contain nearly every record, so the engine ran full-corpus merge
passes to learn nothing). Post-fix, scoped ≈ bare on every shape. The remaining
~220 KB on broad shapes was the caller-owned results `List<AssetRecord>` for
~28k matches. Phase 5 replaces that historical shape with an ordinal array and
lazy record projection; the current allocation evidence is recorded in the
performance-rewrite specification.

## Phase 7 full-panel stress-fixture reference (2026-08-08)

This is a UI-publication reference, not the authoritative synthetic service
regression above. It used about 127,800 records on the same Apple M3 Pro/macOS
26.5.1/Unity 6000.3.10f1 host and typed the ten prefixes of `asset00000` through
the full panel path for five measured cycles. Values are wall-clock publication
time. No cycle rebuilt the search index.

| prefix length | results | median ms | maximum ms |
| ---: | ---: | ---: | ---: |
| 1 | 126,756 | 98.845 | 148.193 |
| 2 | 117,818 | 51.643 | 59.719 |
| 3 | 117,812 | 78.590 | 90.138 |
| 4 | 117,812 | 73.485 | 86.839 |
| 5 | 117,812 | 79.908 | 81.730 |
| 6 | 117,800 | 92.997 | 97.579 |
| 7 | 19,800 | 18.122 | 20.439 |
| 8 | 1,800 | 6.533 | 8.063 |
| 9 | 100 | 5.773 | 8.181 |
| 10 | 10 | 7.108 | 10.177 |

The tenth-character narrow result meets the specification's 16 ms target.
Broad prefixes deliberately publish tens of thousands of stable IDs lazily and
may exceed one frame. This Unity Mono runtime did not expose a working
per-thread allocation counter, so allocation is unavailable rather than zero.
