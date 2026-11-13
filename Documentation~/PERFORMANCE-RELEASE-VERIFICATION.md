# Performance rewrite release verification

Recorded 2026-08-08 for Phase 7 of the
[performance rewrite specification](PERFORMANCE-REWRITE-SPEC.md). This is an
engineering verification record, not a release sign-off. It distinguishes
completed local evidence from gates that require another editor patch,
filesystem, machine, or operating system.

## Disposition

The Phase 7 cleanup and automated macOS gates are complete. The package has
zero EditMode failures on each locally installed semantic lane,
and ordinary measured mutations issue no panel full scan or projection
rebuild. Cross-platform release verification remains **open**: the current CI
targets have not yet run, Windows/Linux live smoke is unavailable on this
machine, and a physical cross-volume local-package test needs a second writable
volume.

## Environment

- Mac15,6, Apple M3 Pro, 18 GB memory, macOS 26.5.1.
- Local editor lanes: 2022.3.62f3, 6000.0.64f1, and 6000.3.10f1.
- CI targets: 2022.3.76f1, 6000.0.79f1, and 6000.3.19f1.
- Synthetic fixture observations: about 2,200 assets; about 22,000 assets;
  and about 127,800 assets in about 10,000 folders.

The local editors are older than the current CI targets. The local results
therefore verify the three compile-time/semantic lanes but do not substitute
for a successful workflow run on the exact release patches.

## Before Phase 0 versus after Phase 7

This is the direct comparison from the recorded blocking-action artifacts,
not a reconstruction from intermediate phase notes. Both endpoints used the
same Mac, Unity 6000.3.10f1, domain reload enabled, the Unity Profiler disabled,
and the same `Stopwatch` plus structured-counter harness. Each endpoint is one
observation rather than a median or P95 distribution.

| Fixture | Before Phase 0 assets / folders | After Phase 7 assets / folders |
| --- | ---: | ---: |
| ~2.2k | 2,200 / 200 | 2,204 / 202 |
| ~22k | 23,804 / 2,002 | 22,004 / 2,002 |
| ~127.8k | 127,808 / 10,004 | 127,820 / 10,010 |

The small and stress fixtures are closely matched. The large fixture was
regenerated and is scale-equivalent rather than count-matched, so its elapsed
time is directional evidence only.

| Fixture | Blocking action | Before Phase 0 | After Phase 7 | Change |
| --- | --- | ---: | ---: | ---: |
| ~2.2k | cold open | 197.935 ms | 225.255 ms | 13.8% slower |
| ~2.2k | warm open | 37.265 ms | 25.390 ms | 31.9% faster |
| ~2.2k | folder switch | 3.510 ms | 4.409 ms | 25.6% slower |
| ~2.2k | create folder | 55.408 ms | 36.900 ms | 33.4% faster |
| ~2.2k | create text | 283.592 ms | 19.023 ms | 14.91x faster |
| ~22k | cold open | 206.840 ms | 225.346 ms | 8.9% slower |
| ~22k | warm open | 41.410 ms | 30.588 ms | 26.1% faster |
| ~22k | folder switch | 6.227 ms | 8.007 ms | 28.6% slower |
| ~22k | create folder | 210.341 ms | 122.879 ms | 41.6% faster |
| ~22k | create text | 1,894.969 ms | 47.892 ms | 39.57x faster |
| ~127.8k | cold open | 239.790 ms | 323.089 ms | 34.7% slower |
| ~127.8k | warm open | 81.842 ms | 98.282 ms | 20.1% slower |
| ~127.8k | folder switch | 15.301 ms | 20.169 ms | 31.8% slower |
| ~127.8k | create folder | 1,104.215 ms | 1,272.779 ms | 15.3% slower |
| ~127.8k | create text | 22,537.984 ms | 200.050 ms | 112.66x faster |

This is not a blanket speedup. The rewrite removed the catastrophic
project-size scaling from text creation, improved warm open and folder creation
on the two smaller fixtures, and regressed the measured cold-open and
folder-switch boundaries. The stress open, switch, and folder-create
observations also regressed and remain explicit optimization work rather than
being hidden by the text-create result.

The text-create change is supported by the same exact call-count difference at
all three sizes:

| Operation issued by one text create | Before Phase 0 | After Phase 7 |
| --- | ---: | ---: |
| targeted import | 1 | 1 |
| full synchronizer run | 1 | 0 |
| full record enumeration | 2 | 0 |
| folder-tree walk | 1 | 0 |
| whole-project projection rebuild | 1 | 0 |
| global `AssetDatabase.Refresh` | 0 | 0 |

## Full EditMode suites

All reports below are graphics-enabled runs against the exact Phase 7
worktree. “Skipped” includes explicit performance tests and tests whose
required package fixture was not installed. Inconclusive cases are
graphics/focus-dependent assertions; none is counted as passed.

| Unity | Total | Passed | Failed | Inconclusive | Skipped/ignored |
| --- | ---: | ---: | ---: | ---: | ---: |
| 2022.3.62f3 | 947 | 945 | 0 | 0 | 2 |
| 6000.0.64f1 | 947 | 942 | 0 | 1 | 4 |
| 6000.3.10f1 | 949 | 943 | 0 | 2 | 4 |

The initial no-graphics run was rejected as evidence because UI Toolkit
paint/layout tests need a graphics device. The table records the reruns.

## Fixture timings and call counts

Each cell below is one release-gate observation, not a distribution. Open
timings measure the blocking observation available to the harness; they are
not instrumented first-paint P95 values. Mutation timings include Unity-owned
AssetDatabase/importer time, so they must not be interpreted as panel-only
bookkeeping.

| Fixture | Cold open | Warm open | Folder switch | Create folder | Create text |
| --- | ---: | ---: | ---: | ---: | ---: |
| ~2.2k | 225.255 ms | 25.390 ms | 4.409 ms | 36.900 ms | 19.023 ms |
| ~22k | 225.346 ms | 30.588 ms | 8.007 ms | 122.879 ms | 47.892 ms |
| ~127.8k | 323.089 ms | 98.282 ms | 20.169 ms | 1,272.779 ms | 200.050 ms |

The 225-323 ms cold observations are above the specification's 200 ms initial
first-paint target, but the harness did not instrument first useful paint. They
are retained as blocking-call evidence and PRF-01 remains open rather than
being relabeled as a first-paint pass or failure.

The single stress folder-switch observation exceeded the initial 16 ms target.
That absolute target has since been superseded by the repeatable, paired
[folder-navigation parity benchmark](FOLDER-NAVIGATION-PARITY.md), which runs
the package and Unity's default Project window in the same project with
instrumentation disabled during timed samples. All three warm-open observations
remain within the initial 100 ms target.
For every create observation:

- folder creation issued exactly one `AssetDatabase.CreateFolder`;
- text creation issued exactly one targeted import;
- both issued zero panel full synchronizer scans, projection rebuilds, and
  global `AssetDatabase.Refresh` calls.

The same harness measured Unity's built-in Project window at 0.468/22.444/8.442
ms (switch/folder/text) on ~2.2k, 0.217/82.244/37.101 ms on ~22k, and
0.274/391.545/211.967 ms on the stress fixture. These are API-boundary
comparisons rather than manual first-paint comparisons. The panel folder-switch
observation is slower than that boundary at every size, particularly on the
stress fixture; the like-for-like built-in UI comparison remains a manual gate.

## Search publication on the stress fixture

Five measured cycles typed the ten prefixes of `asset00000` through the full
panel path. The median and maximum publication time, with the final result
count, were:

| Prefix length | Median | Maximum | Results |
| ---: | ---: | ---: | ---: |
| 1 | 98.845 ms | 148.193 ms | 126,756 |
| 2 | 51.643 ms | 59.719 ms | 117,818 |
| 3 | 78.590 ms | 90.138 ms | 117,812 |
| 4 | 73.485 ms | 86.839 ms | 117,812 |
| 5 | 79.908 ms | 81.730 ms | 117,812 |
| 6 | 92.997 ms | 97.579 ms | 117,800 |
| 7 | 18.122 ms | 20.439 ms | 19,800 |
| 8 | 6.533 ms | 8.063 ms | 1,800 |
| 9 | 5.773 ms | 8.181 ms | 100 |
| 10 | 7.108 ms | 10.177 ms | 10 |

The narrow tenth-character query meets the 16 ms target. Broad publication is
slower but uses stable IDs and lazy record projection; no measured cycle
rebuilt the search index. `GC.GetAllocatedBytesForCurrentThread()` returned its
known zero sentinel on this Unity Mono runtime, so thread allocation is
recorded as unavailable, not as a zero-allocation result.

## Phase 6 retained exceptions rechecked for release

The scoped third-party create fallback took 0.196 ms, 0.191 ms, and 0.368 ms on
the normal, large, and stress fixtures and made zero `FindAssets` calls. The
project-wide GUID diff remains only as an explicitly warned/counted fallback
when scoped discovery cannot begin.

## Thirty-minute session

The recording harness is
[`qa-harness/Phase7SessionProbe.cs`](qa-harness/Phase7SessionProbe.cs). It records
per-minute native/managed memory, available Unity frame-allocation samples,
garbage-collection counts, Editor updates, and panel-window count through:

1. five minutes idle with the panel open;
2. fifteen minutes alternating folder selection and search;
3. five minutes idle with the panel open after interaction;
4. five minutes idle with the panel closed.

The live Editor run completed with exit code 0 and no recorded exception. The
Unity frame counter supplied real samples. Its buffer caps at 4,096 samples;
because Unity changes its update cadence when the last panel window closes,
open/closed allocation is compared per sample rather than by raw per-minute
sum.

| Phase | Current allocated-memory behavior | GC bytes/sample | Peak sampled frame | Gen 0/1/2 collections |
| --- | --- | ---: | ---: | ---: |
| initial idle open | startup/hydration +483.9 MB, then a 0.176 MB range over minutes 1-4 | 3,688.44 | 3,774 B | 68 / 68 / 68 |
| interactive | first search warm-up +52.38 MB; then +2.52 MB through minute 19 | 77,705.04 | 12.74 MB | 6 / 6 / 6 |
| idle open after use | −5 KB transition; exactly flat across four sampled minutes | 3,769.23 | 12.1 KB | 0 / 0 / 0 |
| idle closed | −16.49 MB including the close-time collection; 0.084 MB range thereafter | 3,700.59 | 46.7 KB | 53 / 53 / 53 |

Idle-open allocation was about 68.6 bytes/sample (1.85%) above the closed
Editor baseline. The session ended with current allocated memory about 40.6 MB
above the last pre-search idle sample after the global search/catalog state had
been built, but post-warm interaction and both idle plateaus were bounded rather
than monotonic. Closing the panel set the window count to zero and immediately
released about 0.89 MB before the next collection.

Visual inspection during the active phase confirmed successive `F000xx`
folder reveals, the single outer sidebar scroller following selection, breadcrumb
updates, and matching list publication. This run supports “no unbounded session
growth” and near-baseline idle allocation; it is not a Memory Profiler
object-ownership snapshot, so manual PRF-13 remains open. The raw disposable
record is `/private/tmp/pp-perf-phase01-results/phase7-session-30m.tsv`.

## Section 12 coverage

| Area | Local disposition | Remaining release gate |
| --- | --- | --- |
| 12.1 automated behavior | Complete in the full suites; zero failures on three installed lanes. | Run exact CI target patches. |
| 12.4 platform/safety | macOS case-insensitive APFS plus deterministic safety/fault injection is covered locally. | Windows NTFS, Linux case-sensitive filesystem, case-sensitive macOS if available, physical cross-volume package storage, and real VCS/focus-regain smoke. |

No Windows, Linux, case-sensitive APFS, or second writable volume was available
on the recording machine. Those cells are deliberately left open rather than
inferred from source compatibility or simulated tests.

---

# 2026-09-02 re-measurement

Recorded for T7.2 of the
business-logic audit fix plan of 2026-09-02,
after Phases 0-6 of that plan landed. **The 2026-08-08 record above is
unchanged**; this section is a second, independent capture and is written so the
two can be compared cell by cell where the action matches.

## Environment and fixture

- Mac15,6, Apple M3 Pro, 18,432 MB, macOS 26.5.1 — the same machine as
  2026-08-08.
- Unity 6000.3.10f1, `-batchmode`, domain reload enabled, Unity Profiler
  disabled, `ZepPerfLog` enabled only for each measured interval.
- Disposable project regenerated for this run (scratchpad only, deleted after):
  **128,000 files in 10,104 folders**, four-segment paths, one deliberately
  flat 50,000-file folder (`Assets/Stress/Big`) plus a
  100 x 100 `Area/Group` tree. `AssetDatabase.FindAssets` over the fixture root
  reports **138,102** entries (files + folders), against the 2026-08-08 stress
  fixture's 127,820 assets / 10,010 folders. The two fixtures are
  **scale-equivalent, not count-matched**, so elapsed comparisons below are
  directional; the counters are the exact evidence.
- Harness: `Assets/Editor/T72StressProbe.cs` in the disposable project — the
  same shape as `qa-harness/Phase7SessionProbe.cs` and
  `qa-harness/FolderNavigationParityProbe.cs` (real `ZepWindow`, real
  `ZepControllerHost`, `Stopwatch` around the blocking call,
  `ZepPerfLog.CaptureSnapshot()` over the same interval).

**One observation versus a distribution.** Every mutating cell below (open,
create folder, create text, create tag, the 5,000-item delete, its undo) is
**one observation**, exactly as the 2026-08-08 cells are: the action changes the
project, so it cannot be repeated in place without changing what is measured.
The only distribution here is folder switch, which is non-mutating: it is a
**median of 11 timed switches after 3 warm-ups**, and its minimum and maximum
are reported beside it. Nothing in this section is a P95 or a multi-run mean.

## The five blocking actions, against 2026-08-08

| Blocking action | 2026-08-08 (~127.8k) | 2026-09-02 (~138.1k) | Change |
| --- | ---: | ---: | ---: |
| cold open | 323.089 ms | 220.789 ms | 31.7% faster |
| warm open | 98.282 ms | 119.447 ms | 21.5% slower |
| folder switch | 20.169 ms | 1.681 ms | 12.0x faster |
| create folder | 1,272.779 ms | 856.535 ms | 32.7% faster |
| create text | 200.050 ms | 150.241 ms | 24.9% faster |

Definitions, because two of these cells needed to be pinned down before they
could be compared honestly:

- **cold open** = the first `ZepWindow.Open()` of a fresh Editor
  process with the persistent index cache present on disk. Counters for that
  interval: 0 full synchronizer runs, 0 full record enumerations, 0 folder-tree
  walks, 0 projection-cache rebuilds, 0 global `AssetDatabase.Refresh`, 2
  visible-state publications.
- **warm open** = closing every panel window and reopening it in the same
  process. Counters: **all zero**, including publications — the reopened window
  consumes the already-published pane projection.
- The harness's separate, explicit `ZepController.Refresh()` (a forced
  full synchronize of all 138,102 entries) took **7,320 ms** in the same
  process: 1 full synchronizer run, 1 full record enumeration, 1 projection
  rebuild, 2 `.meta` GUID reads, 0 global refreshes. That is a user-invoked
  Refresh command, not part of opening the panel, and it has **no 2026-08-08
  counterpart** — it is recorded here because folding it into "open" is what
  made a first attempt at this table read ~9.5 s for both open cells.
- **folder switch** alternates two 7-8 entry `Area/Group` folders: median
  1.681 ms, min 0.004 ms, max 2.829 ms, 10 publications over 11 switches, and 0
  tree walks / 0 rebuilds / 0 enumerations. A single switch **into the flat
  50,000-entry folder** cost **17.975 ms** with 1 publication and 0 rebuilds.
- **create folder** issued exactly 1 `AssetDatabase.CreateFolder`, 0 full
  synchronizer runs, 0 enumerations, 0 tree walks, 0 rebuilds, 0 global
  refreshes, 3 publications.
- **create text** wrote the file, imported it targeted, and recorded it for
  undo: 0 full synchronizer runs, 0 enumerations, 0 tree walks, 0 rebuilds, 0
  global refreshes, 0 publications, 2 `.meta` GUID reads.
  `AssetDatabaseImportAssetCalls` reads **0** for this cell because the harness
  calls `AssetDatabase.ImportAsset` directly rather than through the counted
  gateway method; the import is real, the counter simply does not see the
  harness's own call.

## New scenarios

Each of these is one observation with `ZepPerfLog` counters over the
same interval.

| Scenario | Elapsed | Counters |
| --- | ---: | --- |
| create a tag definition | 12.034 ms | `TagPresentationRebuilds` **0**, `RebuildProjectionCacheCalls` **0**, `MetaFileGuidReads` **0**, `AssetDatabaseGuidQueries` 0, `ProjectionPublications` 1 |
| fast grid scroll over the 50,000-entry folder, then 10 s idle | burst 4.498 ms | `SubAssetCatalogChangedRaised` **16 over 10.00 s**; `RebuildProjectionCacheCalls` 0, `ProjectionPublications` 0, `FullRecordEnumerations` 0, `MetaFileGuidReads` 0 across the idle window |

Reading of those five:
