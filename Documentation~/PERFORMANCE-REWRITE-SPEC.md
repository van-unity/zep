# Zep performance rewrite specification

Status: **Phase 7 locally executed; external release gates remain open**
Baseline inspected: package commit `328788a4707f077bb8e63109ef9f025ac6c9046e` on 2026-08-07
Independent review completed: 2026-08-07
Implementation checkpoint: 2026-08-08; Phases 0-7 implementation and local evidence recorded below; exact-patch CI, Windows/Linux, and unavailable physical safety gates remain open
Scope: browsing, indexing, search, creation, import, file operations, projection, startup, persistence, and performance verification
Authorization: the user explicitly permits rewriting any part of the package, including a whole subsystem, when that is the smallest safe route to substantially better performance.

This is an implementation specification, not a claim that every proposed optimization is already proven. Version-dependent and workload-dependent choices remain explicit implementation experiments with fail-closed gates. No Phase-0 large-fixture baseline was available during the original review. The implementation checkpoints below record the Phase-0 gate baseline, the Phase-1 through Phase-7 comparisons, and the compatibility evidence now available. Phase 7 is not a cross-platform release sign-off: exact-patch CI, Windows/Linux live smoke, and physical tests that require unavailable storage topology remain open.

## 1. Required outcome

The panel must feel immediate for ordinary work, and its panel-owned cost must not grow with the total project size when an operation affects only one folder or a small set of assets.

The package cannot eliminate Unity importer work, script compilation, domain reload, shader import, or other Unity-owned costs. It can eliminate duplicate imports, global scans caused by panel code, synchronous metadata work that is not required for the visible result, and whole-project projection rebuilding after local changes.

The final design must make two different latency measurements explicit:

1. **Visible latency:** time from a user commit to the created/moved/renamed item appearing in the correct panel location.
2. **Unity completion latency:** time until Unity finishes import, compilation, domain reload, thumbnail generation, and authoritative metadata resolution.

Visible latency is controlled by this package and should normally complete by the next repaint. Unity completion latency is often outside the package's control and must not be disguised as panel work.

## 2. Executive decision

Use native filesystem I/O as the panel's discovery and provisional-display data plane. Keep Unity APIs as the authoritative boundary for imported identity, native Unity objects, references, import state, and supported asset mutations.

This package already uses native I/O. `UseNativeFileSystemAssetSource` defaults to `true`, the synchronous source is hybrid, and the cold source is native. Therefore, “replace AssetDatabase with System.IO” is not the rewrite. The rewrite is:

- remove project-wide work from ordinary operations;
- stop making multiple subsystems independently react to the same mutation;
- show filesystem results before optional Unity metadata enrichment;
- make folder browsing independent of global search-index readiness;
- keep one canonical owner for asset/folder membership and route every derived index through one atomic delta pipeline;
- make global reconciliation asynchronous and exceptional;
- prove the result with operation counters and large-project benchmarks.

## 3. Evidence vocabulary

Every implementation decision in this document uses one of these labels:

- **VERIFIED:** directly observed in the inspected source at commit `328788a` or stated by current Unity documentation.
- **DECISION:** the target behavior to implement.
- **MEASURE:** profiling is required before choosing or keeping the proposed approach.
- **EXPERIMENT:** Unity behavior or a tradeoff must be tested on every declared compatibility lane before relying on it.
- **REJECTED:** deliberately excluded because it harms correctness, compatibility, or measured performance.

The reviewing agent must preserve these distinctions. It must not silently promote a hypothesis into a verified fact.

## 4. Performance contracts

Measure on at least three fixtures:

- a normal project around 2,000 assets;
- a large project with at least 20,000 assets, matching `Manual-QA-Test-Plan.md` Fixture B;
- a stress fixture with 100,000 or more assets and at least 10,000 folders.

Record panel timings beside Unity's built-in Project window on the same machine and project. Absolute targets may be adjusted after the baseline is captured, but the complexity contracts are mandatory.

### 4.1 Complexity contracts

| Action | Required panel-owned complexity |
| --- | --- |
| Open an already-known folder | `O(direct children)` |
| Create one folder | proportional to the affected parent's direct children plus visible tree delta; never `O(project)` |
| Create/import one file | proportional to the affected parent's direct children plus changed search tokens; never `O(project)` |
| Rename/move one file | proportional to that file and its two parent folders |
| Rename/move one folder | proportional to the moved subtree plus visible affected rows, not the whole project |
| Delete one file/folder | proportional to the deleted item/subtree |
| Import `k` files | `O(k + affected parent buckets)`, not `O(k²)` and not an unconditional full-project rebuild |
| Change one tag | proportional to the changed paths and affected tag counters |
| Idle frame | zero repeating panel allocations and no polling work without an active job |

### 4.2 Package-issued call-count contracts

These counters must be asserted by tests, not inferred from timings:

| Action | Package-issued mutation/import calls | Panel scans/rebuilds |
| --- | --- | --- |
| Create folder under `Assets` | exactly one `AssetDatabase.CreateFolder` | zero synchronizer scans; zero whole-project projection rebuilds |
| Create native `.asset`/`.mat` | exactly one `AssetDatabase.CreateAsset` | zero `SaveAssets`; zero additional `ImportAsset`; zero panel full scans |
| Create text/script | one native write and at most one targeted import | zero `AssetDatabase.Refresh`; zero panel full scans |
| Rename/move | one `AssetDatabase.MoveAsset` | zero panel full scans |
| Duplicate | one `AssetDatabase.CopyAsset` | zero panel full scans |
| External drop of `k` accepted top-level paths | one native stage/commit phase, one short balanced `StartAssetEditing`/`StopAssetEditing` pair around `k` targeted `ImportAsset` calls (`ImportRecursive` for directories) | zero global `Refresh`; zero overlapping force imports; one panel-originated batch publication |
| Unity acknowledgement of a panel operation | no package re-import for an identical authoritative path | each callback batch is applied idempotently; at most one view publication per editor turn |

These are mutation/import budgets, not claims that an operation makes no other Unity query. Phase 0 must count `FindAssets`, `GetAllAssetPaths`, GUID/type/label queries, unique-path queries, and other package-issued Unity calls separately. Ordinary local operations may make bounded path-local queries, but may not issue a project-wide Unity query or scan. Unity does not expose a reliable logical-operation identifier across arbitrary postprocessor callbacks, editor turns, or domain reloads, so the package must not promise exactly one catalog transaction for an unknowable callback window. It must instead normalize every concrete callback batch, make repeated acknowledgement a no-op, and coalesce UI publication within one editor turn.

### 4.3 Initial latency targets

These are starting service-level objectives and must be revised with recorded evidence rather than weakened without explanation:

- folder switch, panel-owned P95: under 16 ms for up to 1,000 direct children;
- ordinary mutation bookkeeping, excluding the Unity API call itself, P95: under 8 ms;
- created filesystem-backed row visible: by the next editor repaint after the required synchronous Unity mutation/import boundary returns, normally under 50 ms of panel bookkeeping; report the boundary wait and end-to-end user latency separately;
- warm window first useful browse render: under 100 ms;
- cold window first useful browse render: under 200 ms, while global search metadata continues in the background;
- narrow search query P95 at 100,000 records: under 16 ms;
- broad search may take longer, but results must be lazily represented and the editor must remain interactive;
- closed panel idle overhead after editor startup: effectively zero unless a deliberate background maintenance job is active.

Do not include Unity's importer/compiler time in “panel bookkeeping.” Report it separately.

### 4.4 Compatibility, support, and reviewed Unity evidence

The package manifest declares Unity `2022.3`. The source also contains separate `UNITY_6000_0_OR_NEWER` and `UNITY_6000_3_OR_NEWER` paths, so “Unity 6” is not one compatibility lane. A declared package-compatibility floor is not the same as current vendor support. Unity's published roadmap lists 2022 LTS full support through May 2025 with one additional year for Enterprise/Industry, both elapsed by this 2026-08-07 review. Unity currently lists 6.0 LTS support through October 2026 and 6.3 LTS support through December 2027. Unity 2022.3 is therefore a deliberate legacy-compatibility lane: retaining it has maintenance risk because upstream fixes and ordinary support have ended.

Implementation and release verification must use all three semantic/compile-time lanes:

| Lane | Review evidence | Release requirement |
| --- | --- | --- |
| Legacy minimum family | local live probe 2022.3.62f3; repository CI currently names 2022.3.72f1 | Keep the manifest floor only if every safety gate passes. Run CI/integration on the newest licensed 2022.3 patch available and record any patch/license limitation explicitly; do not call this a currently supported editor. |
| Unity 6.0 LTS | local live probe 6000.0.64f1; repository CI currently names 6000.0.64f1 | Run on the newest available 6000.0 patch because support fixes continue within the lane. |
| Unity 6.3 LTS | local live probe 6000.3.10f1; no repository CI lane at review time | Add/run this semantic lane and use the newest available 6000.3 patch for release verification. |

Patch versions in this table describe reviewed evidence, not a permanent pin. Unity published 2022.3.76f1 on 2026-05-06 after the locally executable 2022.3 probe, so “latest patch” must be checked at implementation/release time. If a correctness-critical API differs by patch, fail closed or raise the package floor; do not silently certify the family from an older probe.

**VERIFIED by live disposable-project probes on all three editors:**

- one `AssetDatabase.CreateAsset(new AnimationClip(), path)` created the physical file, returned a stable GUID through `AssetPathToGUID`, loaded through `LoadAssetAtPath`, and produced a postprocessor callback without a package-issued `SaveAssets` or `ImportAsset`;
- native writes followed by targeted `ImportAsset` calls inside one `StartAssetEditing`/`StopAssetEditing` scope registered a top-level text file and a nested text file when the directory call used `ImportRecursive`;
- a dot-prefixed file remained physically present but had no AssetDatabase GUID and was absent from imported callback paths;
- `ImportAsset(ForceSynchronousImport | ForceUpdate)` for a valid `.cs` file returned to managed code with a stable GUID before compilation/reload completed.

These probes establish narrow API behavior, not the complete recipe, Undo, package-root, callback-coalescing, or 1,000-item performance contracts. Their reports are review evidence in the disposable `/tmp/zep-performance-review-probes-20260807` projects, not package test artifacts.

Official versioned references used by this review:

- [`CreateAsset`, 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.CreateAsset.html) and [`CreateAsset`, 6000.0](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.CreateAsset.html);
- [`SaveAssets`, 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.SaveAssets.html) and [`SaveAssets`, 6000.0](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.SaveAssets.html);
- [`ImportAsset`, 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.ImportAsset.html), [`ImportAssetOptions`, 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ImportAssetOptions.html), and the corresponding [6000.0 import](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.ImportAsset.html) and [options](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ImportAssetOptions.html) pages;
- [`StartAssetEditing`, 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.StartAssetEditing.html), [`StopAssetEditing`, 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.StopAssetEditing.html), and the corresponding [6000.0 start](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.StartAssetEditing.html) and [stop](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.StopAssetEditing.html) pages;
- [`Refresh`, 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.Refresh.html), [`Refresh`, 6000.0](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.Refresh.html), and the [2022.3](https://docs.unity3d.com/2022.3/Documentation/Manual/AssetDatabaseRefreshing.html) and [6000.0](https://docs.unity3d.com/6000.0/Documentation/Manual/AssetDatabaseRefreshing.html) refresh-pipeline manuals;
- [`OnPostprocessAllAssets`, 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetPostprocessor.OnPostprocessAllAssets.html) and [`OnPostprocessAllAssets`, 6000.0](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetPostprocessor.OnPostprocessAllAssets.html);
- [`EditorApplication.projectChanged`, 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/EditorApplication-projectChanged.html) and [`projectChanged`, 6000.0](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/EditorApplication-projectChanged.html);
- [Unity 6 release support](https://unity.com/releases/unity-6/support), Unity's [2025 engine roadmap support recap](https://unity.com/blog/unity-engine-2025-roadmap), and the [Unity 2022.3.76f1 release page](https://unity.com/releases/editor/whats-new/2022.3.76f1).

## 5. Current architecture: verified facts

### 5.1 Native I/O is already enabled

**VERIFIED:** [`ZepSettings.cs`](../Editor/Bridge/Settings/ZepSettings.cs) defaults `UseNativeFileSystemAssetSource` and `EnablePersistentIndexCache` to `true`.

**VERIFIED:** [`ZepControllerHost.cs`](../Editor/Bridge/ZepControllerHost.cs) composes:

- `HybridAssetRecordSource` for synchronous enumeration;
- `NativeFileSystemAssetRecordSource` for worker-thread cold enumeration;
- `AssetDatabaseAssetRecordSource` only when the native-source kill switch is disabled.

**VERIFIED:** the hybrid source walks the filesystem, asks Unity for GUID/type metadata, and constructs records in parallel above a threshold. The native source enumerates the filesystem and reads `.meta` files in parallel.

**Conclusion:** changing only the record source cannot solve ordinary mutation latency.

### 5.2 One full panel refresh is expensive

**VERIFIED:** `ZepController.Refresh()` calls `RefreshIndex(true)` in [`ZepController.cs`](../Editor/Core/Application/ZepController.cs).

`RefreshIndex` performs all of the following:

1. clears presentation state;
2. calls `AssetIndexSynchronizer.Synchronize()`;
3. enumerates and sorts every indexed file record;
4. obtains a complete folder-record snapshot;
5. rebuilds folder entries and non-empty-folder state;
6. rebuilds tag caches;
7. rebuilds every per-folder browse bucket;
8. rebuilds global type-filter counts;
9. rebuilds first-path-by-type state;
10. reconciles and republishes pane/global state;
11. schedules deferred addressable and search warmup work.

This is intentionally authoritative but is not appropriate after a one-path mutation.

### 5.3 Folder creation currently requests duplicate full refreshes

**VERIFIED:** the command path itself already performs a synchronous incremental update:

```text
ZepController.CreateFolder
  -> ProjectFileCommandService.CreateFolder
  -> UnityProjectAssetGateway.CreateFolder
  -> AssetDatabase.CreateFolder
  -> HandleOperationResult
  -> CrossCacheCoordinator.ApplyIncrementalOperationUpdate
  -> ApplyProjection
```

**VERIFIED:** after this completes, [`ZepCreateOrchestrator.cs`](../Editor/UI/ZepCreateOrchestrator.cs) calls `NotifyAssetLanded`, which stages the landing and calls `_commandHandler.Refresh()`.

**VERIFIED:** [`ZepShellView.cs`](../Editor/UI/ZepShellView.cs) receives the landing, schedules a delay callback, and calls `_commandHandler.Refresh()` again for non-code assets. A folder has no code extension, so it takes this path.

**VERIFIED:** Unity can also report the same creation through `OnPostprocessAllAssets`, producing another incremental notification.

This is the first change to implement and benchmark.

### 5.4 Recipe-created assets perform redundant work

**VERIFIED:** [`ZepCreateAssetSupport.cs`](../Editor/UI/ZepCreateAssetSupport.cs) currently:

- writes text/code with `File.WriteAllText`, then calls `ImportAsset` with `ForceSynchronousImport | ForceUpdate`;
- creates native object assets using `CreateAsset`, `SaveAssets`, then `ImportAsset`;
- creates materials using the same three calls;
- returns to the orchestrator, which calls a full panel `Refresh`;
- stages another shell callback which can call a second full panel `Refresh`.

Unity documents `CreateAsset` as the operation that creates a native Unity-format asset; its current example does not add `SaveAssets` or another `ImportAsset`. Unity also documents that targeted APIs such as `CreateAsset` and `ImportAsset` queue refresh work for the specified asset rather than requiring a global refresh.

**VERIFIED:** Unity documents `SaveAssets` as writing all unsaved asset changes, not as a required finalizer for one `CreateAsset` call. Removing it from these simple recipes avoids a broader unrelated flush; every concrete recipe still has the Phase-1 compatibility-lane gate.

Sources:

- [Unity 2022.3 `AssetDatabase.CreateAsset`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.CreateAsset.html)
- [Unity 6000.0 `AssetDatabase.CreateAsset`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.CreateAsset.html)
- [Unity 2022.3 `AssetDatabase.SaveAssets`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.SaveAssets.html)
- [Unity 6000.0 `AssetDatabase.SaveAssets`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.SaveAssets.html)
- [Unity 2022.3 Asset Database refresh pipeline](https://docs.unity3d.com/2022.3/Documentation/Manual/AssetDatabaseRefreshing.html)
- [Unity 6000.0 Asset Database refresh pipeline](https://docs.unity3d.com/6000.0/Documentation/Manual/AssetDatabaseRefreshing.html)

### 5.5 External import uses overlapping import strategies

**VERIFIED:** both [`AssetDropOperations.cs`](../Editor/UI/AssetDropOperations.cs) and [`ExternalAssetImporter.cs`](../Editor/Bridge/FileOperations/ExternalAssetImporter.cs) currently:

1. call `StartAssetEditing`;
2. copy files/directories with native I/O;
3. call `StopAssetEditing`;
4. call global `AssetDatabase.Refresh()`;
5. call `AssetDatabase.ImportAsset(... ForceUpdate)` once for every imported top-level path.

**VERIFIED:** Unity documents that `StartAssetEditing`/`StopAssetEditing` suspend imports in order to group multiple asset operations. A global `Refresh` scans for changed project files and also triggers asset garbage collection. Doing both the global scan and a per-path forced import is a strong candidate for redundant work.

Sources:

- [Unity 2022.3 `AssetDatabase.StartAssetEditing`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.StartAssetEditing.html)
- [Unity 6000.0 `AssetDatabase.StartAssetEditing`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.StartAssetEditing.html)
- [Unity 2022.3 `AssetDatabase.Refresh`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.Refresh.html)
- [Unity 6000.0 `AssetDatabase.Refresh`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.Refresh.html)

**DECISION:** use targeted `ImportAsset` for each copied top-level path inside one balanced `StartAssetEditing`/`StopAssetEditing` scope, adding `ImportRecursive` for directories. The live probes in section 4.4 established complete nested discovery on the three review lanes. A global `Refresh` is an allowed measured compatibility fallback only if a compatibility-lane integration test proves targeted import incomplete for a concrete path/package case; it must replace, never precede or follow, the targeted strategy for that operation.

**VERIFIED:** the current `StartAssetEditing` scope surrounds only native `System.IO` copies and ends before all AssetDatabase import calls. It therefore does not currently batch those import calls. The selected implementation must put the targeted AssetDatabase calls inside the balanced scope and use `try/finally`, as Unity's documentation requires.

**VERIFIED:** `FilesystemEntryOperations.CopyDirectoryRecursive` copies every file returned by `Directory.GetFiles`, including `.meta` sidecars, and recursively follows every returned directory without an explicit Unity-ignore or symlink/reparse policy. Both external-import entry points use it. That existing behavior is why section 6 requires explicit exclusion, staging, and traversal policy rather than treating raw recursion as a harmless implementation detail.

### 5.6 The incremental path is incomplete

**VERIFIED:** `CrossCacheCoordinator.ApplyExternalChanges` handles small external batches incrementally only when:

- total changes are at most 32;
- the operation is not inside panel bulk scope;
- deleted/moved-from paths are known file records rather than folders.

Folder delete/move and larger batches fall back to direct index mutation plus `RebuildProjectionCache`, which is `O(project)`.

**VERIFIED:** the full rebuild recreates project-wide type counts. The incremental browse mutators patch parent lists, but no owned incremental type-count table exists.

**VERIFIED:** `FolderEntriesBuilder.UpsertFolder` copies all standard folders, linearly finds/replaces one entry, sorts on insertion, and re-merges the complete list. `FolderTreeWidget` then compares/copies folder slices, rebuilds its parent-child map, rebuilds visible rows, and refreshes the list. Even after removing the duplicate global refreshes, folder creation still contains `O(folder count)` work.

**VERIFIED:** the manual performance plan expects 1,000 external additions/deletions to use an incremental path, but the current hard cap of 32 makes that expectation impossible for a single 1,000-path callback.

### 5.7 Cache validation and “async” startup still do main-thread global work

**VERIFIED:** before worker enumeration, `AssetIndexSynchronizer.SynchronizeAsync` captures Unity labels and computes a persistent signature. On a cache miss it also captures authoritative type metadata on the main thread before starting `Task.Run`.

**VERIFIED:** `PersistentAssetIndexCache.ComputeSignature` walks directory trees, folds `GlobalArtifactDependencyVersion`, folds Unity label state, and checks external settings/package state.

**VERIFIED:** `BuildUnityLabelSnapshot` calls `FindAssets("l:")` and then obtains labels for every labeled GUID. `BuildAuthoritativeTypeSnapshot` calls `GetAllAssetPaths` and performs Unity type queries for ambiguous formats.

**VERIFIED:** `ZepControllerHost` is `[InitializeOnLoad]` and creates the shared host on a delayed callback after every domain reload, even when no Zep window is open. Worker enumeration may be asynchronous, but its setup and later search warmup can still consume editor time.

**VERIFIED:** the same session host constructs `ProjectCommandHistory` from `SessionUndoHistoryStorage`, installs `UnityProjectUndoBridge`, subscribes editor events, and calls `Core.InitializeIndex()`. The host is therefore both a heavy prewarm path and the owner that makes persisted panel Undo/callback handling available when the window is closed. Removing it wholesale would change correctness, not just startup cost.

**VERIFIED:** `RunDeferredStartupWork` calls `_searchService.WarmIndex()` synchronously on the editor thread and explicitly documents the resulting hitch.

**VERIFIED:** if a worker scan loses its generation race, `AssetIndexSynchronizer.SynchronizeAsync` calls `RebuildSynchronouslyAfterInvalidatedScan()` on the main thread. A live mutation during a cold scan can therefore turn the nominally asynchronous path into a synchronous full rebuild. The rewrite must discard/restart or merge forward without this fallback on the interactive thread.

**VERIFIED:** `RunDeferredStartupWork` also enumerates the complete record set and calls `BuildAddressableAssetGuids` synchronously on the editor thread before warming search. The addressables adapter uses reflection and can expand addressable folders or fall back to record-by-record lookup. Addressable presentation work is therefore another global startup hot path and must be timed and moved out of first-use work where its Unity calls permit.

### 5.8 Persistence is all-or-nothing

**VERIFIED:** the persistent index cache is saved after a full `ApplyRecords` path. Ordinary incremental changes patch memory but do not append an incremental durable catalog update.

**VERIFIED:** `TryHydrateFromCache` refuses hydration when the live index is already non-empty. Manual refresh therefore computes a signature and normally performs a full source rebuild rather than reusing the saved snapshot.

**Consequence:** a correct persistent cache helps a valid cold/warm startup, but it is not a durable source continuously kept current by the same deltas already processed in memory.

### 5.9 Creation fallback performs project-wide GUID diffs

**VERIFIED:** unsupported/third-party create menu items in `ZepCreateOrchestrator` snapshot GUIDs with `AssetDatabase.FindAssets`, run `ExecuteMenuItem`, then diff another GUID snapshot to discover the created object. The search roots are `Assets` plus relevant package roots, not literally every installed package.

This is a rare compatibility path, not the primary creation path, but it is still `O(search roots)` and its comment's “millisecond-cheap even on large projects” claim is unverified by the current performance suite.

### 5.10 Current performance coverage is insufficient for this rewrite

**VERIFIED:** search has a 50,000-record synthetic timing regression test and documented baselines.

**VERIFIED:** the manual plan defines a 20,000-asset performance fixture and end-to-end cases. The QA log records the synthetic search-engine half of PRF-03 as passed, but records the real-panel typing, performance-log, presentation, and no-full-rebuild half as not executed.

**VERIFIED:** the only located shell command fake whose `Refresh()` is a no-op lives inside `ZEP_PRE_INCIDENT_UI_TESTS` quarantine and is not active coverage. No active shell/controller test asserts the create-landing refresh count. The duplicate refresh path is therefore uncovered; the quarantined fake is evidence of obsolete tests, not evidence about the active test double.

There are no hard end-to-end contracts today proving that folder/file creation performs zero full synchronizations and zero duplicate refreshes.

### 5.11 Additional ordinary hot paths found by review

**VERIFIED:** [`ZepPingReceiver.cs`](../Editor/UI/ZepPingReceiver.cs) is initialized for the editor session. When Unity pings a just-created asset that is not in the panel index, its delayed `RefreshAndRevealCreatedAsset` calls `ZepControllerHost.Shared.Core.Refresh()`, performing an ordinary full synchronization before reveal. This must become a concrete-path import acknowledgement/direct-child reconciliation path, with full reconciliation reserved for a genuinely unresolvable ping.

**VERIFIED:** [`ZepController.RefreshTagPresentationCache`](../Editor/Core/Application/ZepController.cs) enumerates the complete index, asks the gateway for every record's tag IDs, sorts all records, then rebuilds tag and browse caches/type counts when the observed tag version changes. A one-path tag assignment normally takes a more incremental path, but external assignment changes and tag-definition changes can still trigger this global work. The rewrite must distinguish definition changes that truly affect many search documents from a single assignment and update only the affected set.

### 5.12 Package roots have multiple owners

**VERIFIED:** [`ProjectPaths.cs`](../Editor/Bridge/FileOperations/ProjectPaths.cs) resolves a package asset path through `PackageInfo.FindForAssetPath(...).resolvedPath`, while [`HybridAssetRecordSource.cs`](../Editor/Bridge/Indexing/HybridAssetRecordSource.cs) independently builds package mappings with `PackageInfo.GetAllRegisteredPackages()` and then scans raw `Library/PackageCache` directories for packages it assumes the API omitted. The host takes another snapshot from that indexing helper. Unity documents `GetAllRegisteredPackages` as returning all packages currently loaded, so raw cache contents are not equivalent to registered project packages and can contain stale or unrelated versions.

**DECISION:** one registered-package-root resolver/snapshot must own virtual-to-physical mappings for discovery, direct browse, mutations, persistence signatures, and fallback search roots. It must be based on Package Manager registration, update atomically when package topology changes, and never infer membership merely from a directory in `Library/PackageCache`. Preserve the deliberate exclusion of built-in module shells through Package Manager source/manifest metadata. If Package Manager is temporarily unavailable, retain the last valid snapshot (or mark an empty initial snapshot incomplete), retry on the main thread, and never publish package absence from that failure. If a compatibility-lane test finds a real registered-package omission, stop and document that exact case before adding a narrower fallback.

Source: [Unity `PackageInfo.GetAllRegisteredPackages`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/PackageManager.PackageInfo.GetAllRegisteredPackages.html).

## 6. Native I/O versus Unity API boundary

The implementation must follow this table unless a compatibility-lane experiment proves a safer/faster boundary.

| Operation/data | Native I/O | Unity API | Decision |
| --- | --- | --- | --- |
| Enumerate one folder's direct children | primary | optional visible-row enrichment | Use native mapped paths and return immediately. |
| Read size, timestamp, extension | primary | none | Keep off Unity APIs and load lazily for visible rows. |
| Read GUID/labels from `.meta` during background scan | compatibility hint only | authoritative correction | Unity does not document the `.meta` YAML schema as a public contract. Keep parsing isolated/versioned, fail closed on unknown forms, and verify against Unity on the main thread. |
| Infer type from unambiguous extension | primary | none | Use provisional/known extension type. |
| Resolve `.asset`, ScriptedImporter, sub-asset, importer-specific type | no authoritative answer | primary | Query Unity in a bounded main-thread enrichment budget. |
| Create folder under `Assets` | no | `CreateFolder` | Keep Unity. Raw `Directory.CreateDirectory` needs later discovery and `.meta` registration. |
| Mutate a writable local/embedded package | only behind a proven boundary | primary where documented/proven | The current `CanWrite` policy is not proof that each AssetDatabase mutation supports package paths. Gate create/move/rename/copy/import per API and lane; disable an unsupported operation rather than improvise native mutation. |
| Create text/script/json/uss/uxml | write file | one synchronous targeted import | Keep the current exact Undo chronology; apply/reveal the returned authoritative record locally without a global panel refresh. Defer import only in a separately reviewed design if Phase 0 proves the import call itself is a material panel-owned bottleneck. |
| Create native `.asset`/`.mat`/`.anim` | no | `CreateAsset` | One create call; no extra save/import unless a specific recipe proves it needs one. |
| Create prefab/scene/specialized editor asset | supporting I/O only | specialized Unity API | Preserve recipe-specific safety. |
| Rename/move imported file/folder | no | `MoveAsset` | Preserve GUID/reference/import semantics. |
| Duplicate imported asset | no | `CopyAsset` | Do not copy `.meta`; Unity assigns the duplicate's identity. |
| External copy into project | primary | grouped targeted imports | Preflight, copy each accepted top-level payload to an ignored same-volume staging name, then atomically commit it; only the short targeted-import enqueue section belongs inside one balanced editing scope. Add `ImportRecursive` for directories. |
| Search tokenization/postings | primary CPU/workers | none | Keep the package's grammar and build/update off the main thread where possible. |
| Authoritative final GUID/type/import state | provisional only | primary | One or more `OnPostprocessAllAssets` callbacks acknowledge/correct the operation; never assume callback count equals user-operation count. |

Boundary constraints:

- Replace the current competing `ProjectPaths` and indexing mappings with the one registered-package-root resolver specified in section 5.12. Registry/git packages remain read-only. Local/embedded writes remain product policy only where the concrete API/path/lane integration test passes.
- Worker jobs may use BCL/native filesystem and pure package code only. No `UnityEditor` or `UnityEngine` API is permitted from a worker merely because it happened to work in a probe; undocumented thread safety is not a contract.
- Native traversal must not follow symlinks/reparse points outside a registered root, recurse through cycles, or cross a package boundary. Resolve and validate every write destination before mutation.
- Direct discovery and ordinary external import share one Unity-visibility policy. Never expose or copy `.meta` sidecars. Ignore operating-system-hidden folders, files/folders whose names begin `.`, files/folders whose names end `~`, files/folders named `cvs`, and files with the `.tmp` extension, matching Unity's documented ignored-asset set. Probe case sensitivity and whether attribute-hidden files with otherwise ordinary names are ignored on each platform/lane, but never weaken the documented minimum exclusions.
- Ordinary external drag/drop is a data import, not a Unity-project migration: source `.meta` files are always excluded and Unity assigns destination identity. Preserving a foreign Unity tree, its GUIDs, and internal references is a separate future product feature requiring explicit UX, collision policy, and atomic-set semantics; it is not part of this rewrite.
- External import must preflight all source/destination paths, ignores, sidecars, symlink/reparse policy, collisions, and writable roots before making importable files visible. Stage each accepted top-level payload under an ignored name on the destination volume, clean up a failed stage, and commit top-level entries atomically. If the product preserves partial success, the unit of success/failure is one top-level entry and the result must say which entries committed; never leave a half-copied importable directory.
- Unity documents that `StartAssetEditing` prevents imports until the matching `StopAssetEditing`. Keep this global pause short: never hold it while walking or copying external payloads. Use `try/finally`, and put only the targeted `ImportAsset` enqueue calls in the group.
- “Exactly one Unity call” in shorthand means package-issued mutation/import calls in section 4.2. Auto Refresh, importer dependencies, compilation, and Unity's internal refresh-loop restarts remain Unity-owned and must be reported separately; package-issued queries are separately counted.
- `CreateAsset` must not be invoked from an importer or postprocessor callback. Unity 6000.0 documents that restriction; queue panel-originated creates outside import callbacks on every supported lane.

Sources: [Unity 2022.3 special/ignored folders](https://docs.unity3d.com/2022.3/Documentation/Manual/SpecialFolders.html), [Unity 6000.0 special/ignored folders](https://docs.unity3d.com/6000.0/Documentation/Manual/SpecialFolders.html), [`CreateFolder` 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.CreateFolder.html), [`CreateFolder` 6000.0](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.CreateFolder.html), [`StartAssetEditing` 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.StartAssetEditing.html), and [`StartAssetEditing` 6000.0](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.StartAssetEditing.html).

### 6.1 Rejected approaches

- **REJECTED:** launching Finder/Explorer to perform operations. It adds process/UI overhead and does not solve catalog synchronization.
- **REJECTED:** replacing `AssetDatabase.CreateFolder` with `Directory.CreateDirectory` as a general fast path. Unity's own `Refresh` documentation demonstrates that raw directories are not valid AssetDatabase folders until refresh.
- **REJECTED:** raw `File.Move`/`Directory.Move` for ordinary imported moves/renames. Moving payload plus `.meta` correctly is not enough to guarantee all Unity/VCS/callback semantics.
- **REJECTED:** making `FileSystemWatcher` authoritative. Watchers can emit duplicate, split, partially-written, reordered, and platform-specific events during imports. At most, a watcher may be tested as a provisional latency hint; Unity postprocessing remains authoritative.
- **REJECTED:** calling `AssetDatabase.FindAssets` or Unity Search on every keystroke without a benchmark proving it beats the existing grammar and allocations. The custom grammar is product behavior.
- **REJECTED:** creating a second catalog beside the current index/caches and keeping both indefinitely. The rewrite must migrate ownership and delete the superseded path.
- **REJECTED:** scanning raw `Library/PackageCache` directories as proof that a package belongs to the project. Package Manager registration is authoritative; cache contents are not.
- **REJECTED:** removing correctness calls merely because they look redundant. Each removal requires an API call-count test and compatibility-lane integration test.
- **REJECTED:** treating Unity's `.meta` serialization as a stable public API. Native parsing is a guarded optimization, never the only correctness path.
- **REJECTED:** copying external `.meta` files in ordinary drag/drop. Duplicate or foreign GUIDs are a correctness hazard; Unity-tree migration is outside this rewrite.
- **REJECTED:** holding `StartAssetEditing` during filesystem traversal/copy. It pauses Asset Database imports globally for time unrelated to Unity batching.
- **REJECTED:** the proposed deferred-create receipt/native-Undo-reservation subsystem in this rewrite. The reviewed compatibility-lane probes return a stable GUID from one synchronous targeted script import, and Phase 0 has not established that the import call itself is the panel-owned bottleneck. A new pending history state spanning reload and interleaved native Undo is disproportionate without that evidence. If measurement later proves it necessary, it requires a separate reviewed specification rather than a speculative branch in this one.

## 7. Target state ownership

The present design distributes one logical truth across `InMemoryAssetIndex`, the search mirror, folder entries, per-folder browse lists, type counts, tag projections, non-empty-folder paths, presentation state, and view-model copies. `CrossCacheCoordinator` exists because callers must keep those copies synchronized.

### 7.1 Final owner

**DECISION:** evolve `InMemoryAssetIndex` into the smallest canonical membership owner needed by the measured bottlenecks. Renaming it to `ProjectAssetCatalog` is optional. Replacing it is justified only if an owner-local extension would be more complex; implementation must not perform a name/layer rewrite for its own sake.

The canonical owner is responsible for invariants that cannot safely have two writers:

- canonical file records by GUID and path-to-GUID lookup;
- path-keyed pending filesystem entries for direct observations that have no Unity GUID yet; these are a distinct state, not fake `AssetRecord` instances or predicted GUIDs;
- canonical folder records by path;
- direct child membership/topology by parent folder;
- membership-derived counts only when owner-local co-location is the simplest measured design; otherwise a focused delta consumer owns them;
- a monotonically increasing version;
- atomic application of one normalized change batch and emission of one resulting delta.

It does **not** need to own every derived representation. Search postings, tag projections, presentation rows, thumbnails, addressable display state, and virtualized view collections have distinct reasons to change and may remain focused components. Their rule is one-way ownership: they consume catalog snapshots/deltas and expose queries, but callers cannot mutate the same asset independently in both the catalog and a derived component. Search may own its postings while catalog application owns the only route that schedules a posting update.

Current `AssetRecord` construction requires a non-empty GUID, so direct native discovery cannot be modeled by inserting a fabricated record into `InMemoryAssetIndex`. A pending entry is identified by its normalized virtual path and carries only filesystem-derived fields. Authoritative acknowledgement promotes it atomically to the GUID-keyed record exactly once, with no duplicate row. Pending entries do not claim imported identity, labels, sub-assets, addressable state, or durable absence, and are not persisted as imported assets.

Path comparison must be explicit rather than inherited accidentally from existing dictionaries. Preserve the product's case-insensitive logical lookup where intended, retain exact original spelling for display and case-only `MoveAsset` operations, and reject two live entries that collide under the selected canonical comparison. Test this on case-sensitive and case-insensitive filesystems.

**Architecture gate:** before Phase 2, compare (a) adding folders/child topology/atomic batch application to the existing index and simplifying `CrossCacheCoordinator` with (b) replacing the index. Choose the design with fewer owners, conversions, and public mutation paths. In either case, delete superseded mutation code in the same phase; do not run two canonical stores.

### 7.2 Change batch

Replace or evolve `ProjectAssetChanges` into a meaningful normalized batch with these semantics:

```text
Added or updated files: authoritative GUID records or path-keyed pending observations
Added or updated folders: authoritative folder records or path-keyed pending folder observations
Removed paths: file or subtree roots
Moves: paired old and new roots
Source: panel operation, Unity postprocessor, startup scan, external reconciliation
Provenance/completeness: pending direct-folder observation or authoritative Unity/global observation
```

Required invariants:

- arrays/collections are non-null;
- paths are normalized once at the boundary;
- duplicate paths inside one batch are coalesced;
- moves are paired and longest-parent mapping wins for subtree rewrites;
- one path has at most one live canonical entry;
- applying the same authoritative batch twice is idempotent;
- a later authoritative record promotes/replaces a path-keyed pending entry without duplicating a row;
- a stale background-scan generation cannot overwrite a later live change;
- a partial direct-folder observation can establish presence but never imply project-wide absence;
- background reconciliation merges/replays live deltas recorded after its starting generation, or discards/restarts without a main-thread full-scan fallback;
- one atomic application produces one delta for consumers;
- callback boundaries are not treated as operation identity; batches can be applied independently while view publication is coalesced per editor turn.

The delta can evolve `ProjectAssetChanges` or another existing result type; do not add a DTO merely to satisfy this document. It contains only what real consumers need: changed parent folders, inserted/removed/moved folder nodes, changed counters, changed paths, and whether a rare full replacement occurred.

### 7.3 No ordinary full panel reconciliation

**DECISION:** keep a full panel/catalog reconciliation command for:

- explicit user Refresh;
- recovery after detected corruption or a dropped/ambiguous external event;
- initial background reconciliation when no valid snapshot exists;
- package topology changes that cannot be expressed safely from concrete events;
- development diagnostics.

## 8. Required changes by phase

Do not implement this as one undifferentiated patch. Each phase must leave the package buildable, tested, and benchmarked. The same implementation agent can continue through phases, but it must stop and update evidence whenever a gate fails.

### Phase 0 — baseline and observability

Before behavior changes:

1. Add structured operation timing around:
   - Unity boundary call;
   - catalog/index mutation;
   - projection building;
   - view-model publication;
   - shell/sidebar/asset-list refresh;
   - postprocessor acknowledgement;
   - persistent-cache validation/hydration;
   - addressables discovery;
   - search warm/build;
   - generation-invalidated scan recovery.
2. Add counters for:
   - full synchronizer runs;
   - full record enumerations;
   - folder-tree walks;
   - `RebuildProjectionCache` calls;
   - search full builds;
   - projection publications;
   - raw postprocessor callbacks, normalized batches, deduplicated acknowledgements, and editor-turn publications;
   - package-issued `FindAssets`, `GetAllAssetPaths`, GUID/type/label, unique-path, import/mutation, and global `Refresh` calls;
   - synchronous invalidated-scan fallbacks and work triggered while no panel window is open.
3. Capture baselines for every action in section 4 on the three fixtures.
4. Capture Unity built-in Project-window comparisons for folder switch, folder create, text create, rename, and external import.
5. Save machine, OS, Unity version, fixture counts, domain-reload settings, and profiler configuration with the result.

The instrumentation must be disabled by default and near-zero cost when disabled. Avoid string formatting on disabled paths.

**Gate:** no optimization begins until at least folder-create, text-create, folder-switch, warm-open, and cold-open call graphs have measured timings and counts.

**REVIEW NOTE:** these large-fixture timings and call graphs were unavailable during document review and were not fabricated. Phase 0 subsequently captured the blocking-gate measurements recorded in the implementation evidence below. They are single observations for the checkpoint, not a substitute for the later repeated P50/P95 release matrix.

### Phase 1 — remove known duplicate work using the current architecture

This phase should deliver the fastest low-risk improvement and establish regression contracts before the larger ownership rewrite.

#### 1A. Folder and command-created operations

- Remove `_commandHandler.Refresh()` from `ZepCreateOrchestrator.NotifyAssetLanded`.
- Remove the delayed `_commandHandler.Refresh()` from `ZepShellView.OnCreatedAssetLanded`.
- Make landing responsible only for reveal, selection, focus, and domain-reload handoff.
- Confirm `HandleOperationResult -> ApplyIncrementalOperationUpdate -> ApplyProjection` makes the result visible before relying on postprocessor acknowledgement.
- Make repeated postprocessor acknowledgement a no-op when the canonical record/folder metadata is unchanged.
- Replace `ZepPingReceiver`'s just-created-asset full `Core.Refresh()` with a concrete-path acknowledgement/reveal retry. Keep full reconciliation only when the path cannot be resolved after a bounded retry.

**Gate:** folder create meets the exact counters in section 4 and remains correct with the built-in Project window both open and closed.

#### 1B. Native asset recipes

- Replace `CreateAsset + SaveAssets + ImportAsset` with `CreateAsset` for recipes where compatibility-lane tests prove the asset is immediately registered and postprocessed.
- Keep specialized scene/prefab/editor recipes on the APIs required by those formats.
- Remove the orchestrator full panel refresh after a successful recipe.
- Submit the created path as one local catalog/index change, then allow postprocessor to correct metadata idempotently.

**Gate:** every shipped recipe creates the same usable asset on all three section 4.4 lanes, Undo/Redo remains exact, and call-count assertions pass.

#### 1C. External imports

- Consolidate the duplicated import implementation in `AssetDropOperations` and `ExternalAssetImporter` into the existing filesystem/Unity boundary owner; do not create another generic helper.
- Preflight the full accepted top-level set before writes: canonical source/destination paths, collisions, writable roots, ignored paths, all `.meta` sidecars, symlinks/reparse points, and path escapes.
- Stage each accepted top-level payload once under a Unity-ignored name on the destination volume, then atomically rename it to the final destination. Clean up failed staging. Preserve current partial-success UX only at top-level granularity and report each committed/failed entry; never expose a half-copied importable directory.
- Ordinary external import always excludes source `.meta`; Unity-tree migration is out of scope. Apply the shared Unity-ignore/hidden policy consistently.
- Perform all traversal/copying before `StartAssetEditing`. Put only targeted `ImportAsset` calls inside one short balanced `StartAssetEditing`/`StopAssetEditing` group; use `ImportRecursive` for each directory and avoid `ForceUpdate` unless a concrete stale-artifact test requires it.
- Use no global `Refresh` in the normal external-drop path. If targeted imports fail a declared path/package compatibility test, measure and document one global-refresh-only branch for that exact case; never execute both strategies.
- Group projection changes by affected parent folder and publish one selection/view update.

**Gate:** files, nested directories, the complete documented ignore set, source `.meta` sidecars, symlinks/reparse points, collisions, stage/copy/commit failures, local packages, read-only package rejection, Auto Refresh on/off, and 1,000-item drops behave correctly on all three editor lanes; no overlapping global and targeted import passes remain and no long native copy occurs while asset editing is paused.

#### 1D. Bulk operations

- Stop `RunBulk` from unconditionally calling `RebuildProjectionCache` when the catalog/index and browse caches were already updated by concrete changes.
- Accumulate one normalized batch through the outermost bulk scope.
- Apply/publish once at depth zero.
- Preserve the current try/finally safety around `StartAssetEditing` and bulk depth.

**Gate:** a 1,000-file operation produces one catalog application and one view publication without a full project rebuild.

#### 1E. Writable-package API gate

The gateway currently treats local and embedded packages as writable, but Unity's `CreateFolder` documentation specifies an `Assets` parent and the present tests only exercise policy classification. Before preserving a package mutation, run create-folder, text/native create, targeted import, case-only rename, move, copy, delete, Undo/Redo, and callback tests in real local and embedded packages on every compatibility lane.

Use the documented Unity API where it passes. If an operation is unsupported or inconsistent for a package kind/lane, disable it with a clear result for that scope during this rewrite. Designing a native mutation protocol for writable packages is a separate safety decision and is not authorized by the policy predicate alone. Registry and git packages remain read-only.

**Gate:** the enabled-operation matrix is recorded by package kind, API, editor lane, and Auto Refresh state; no UI affordance promises an unproven mutation.

### Phase 0/1 implementation evidence — 2026-08-07

Phase 0 was captured before the Phase-1 behavior edits in a disposable Unity project, then repeated after Phase 1 on the same machine. The harness used `Stopwatch` around the blocking operation boundary plus the structured package counters/stages added in Phase 0. Each cell below is one observation, so it is evidence for the blocking Phase-0 call-graph gate and a before/after direction; it is deliberately not labelled a median or P95.

Environment: Mac15,6; Apple M3 Pro (12 logical cores); 18,432 MB; macOS 26.5.1; Unity 6000.3.10f1; domain reload enabled; Unity Profiler disabled; structured package instrumentation enabled only for the measured interval. Instrumentation is disabled by default and its disabled branches do not format operation strings.

| Fixture | Before discovered assets / folders | After discovered assets / folders |
| --- | ---: | ---: |
| normal | 2,200 / 200 | 2,200 / 200 |
| large | 23,804 / 2,002 | 22,000 / 2,000 |
| stress | 127,808 / 10,004 | 127,812 / 10,006 |

The normal and stress captures are count-matched or within four discovered entries. The large fixture was regenerated between captures and is scale-equivalent rather than exactly identical; use the call-count change, not its raw time alone, as the Phase-1 proof.

| Fixture | Action | Panel before -> after (ms) | Built-in Project window before -> after (ms) |
| --- | --- | ---: | ---: |
| normal | cold open | 197.935 -> 171.424 | — |
| normal | warm open | 37.265 -> 30.296 | — |
| normal | folder switch | 3.510 -> 3.228 | 0.657 -> 0.102 |
| normal | folder create | 55.408 -> 48.237 | 23.149 -> 19.569 |
| normal | text create | 283.592 -> 10.814 | 6.154 -> 5.826 |
| large | cold open | 206.840 -> 183.527 | — |
| large | warm open | 41.410 -> 38.012 | — |
| large | folder switch | 6.227 -> 5.323 | 0.256 -> 0.137 |
| large | folder create | 210.341 -> 184.715 | 80.718 -> 75.026 |
| large | text create | 1,894.969 -> 28.011 | 24.225 -> 21.796 |
| stress | cold open | 239.790 -> 213.974 | — |
| stress | warm open | 81.842 -> 72.749 | — |
| stress | folder switch | 15.301 -> 14.151 | 0.286 -> 0.134 |
| stress | folder create | 1,104.215 -> 541.359 | 413.974 -> 379.553 |
| stress | text create | 22,537.984 -> 123.153 | 136.272 -> 114.696 |

Text-create time fell by approximately 96.2%, 98.5%, and 99.45% in the normal, large, and stress observations. Before Phase 1, each text create issued one targeted import and then performed one full synchronizer run, two full record enumerations, one folder-tree walk, and one `RebuildProjectionCache`. After Phase 1 it still issued exactly one targeted import, but all four project-wide counters were zero; the concrete postprocessor acknowledgement was recognized as identical and made no duplicate publication. Neither path issued `AssetDatabase.Refresh`. Folder create issued exactly one `AssetDatabase.CreateFolder`, zero synchronizer scans, zero whole-project projection rebuilds, and one publication before and after.

The warm-open observations meet the initial 100 ms objective and the stress folder-switch observation is below 16 ms, but these are not P95 samples. Stress cold open remains 213.974 ms, above the initial 200 ms objective; that target is not weakened here and remains assigned to the startup/persistence work in Phase 4. Folder-create end-to-end time is dominated by Unity's synchronous boundary and postprocessor work; the exact call-count gate, not that end-to-end number, is the Phase-1 acceptance criterion.

Phase gate evidence:

- **Phase 0 — passed.** Structured timings cover the Unity boundary, catalog mutation, projection/view publication, shell/sidebar/list refresh, postprocessor acknowledgement, persistence validation/hydration, addressables discovery, search warm build, and invalidated-scan recovery. Counters cover the specified scans, rebuilds, callbacks/publications, Unity queries/mutations/imports, global refresh, invalidated fallback, closed-panel work, and bulk catalog applications. The five blocking call graphs were captured on all three fixtures before behavior changes.
- **1A — passed.** Folder creation with Unity's Project window closed and open both recorded `CreateFolder=1`, full synchronizer `=0`, projection rebuild `=0`, publication `=1`. Exact record/folder equality makes repeated acknowledgement a no-op; concrete ping retry is bounded before recovery refresh.
- **1B — passed.** The Phase-1-modified native recipe family (Material and 2D/3D physics materials) passed on 2022.3.62f3, 6000.0.64f1, and 6000.3.10f1 with one `CreateAsset` and zero package-issued `SaveAssets`/`ImportAsset`; the shared template/text family passed with one targeted import, zero global refresh, and zero full synchronizer runs. Legacy built-in template resolution was added for 2022.3. Generic create-history tests prove exact create -> Undo -> Redo restoration, and controller tests prove pane-local create/Undo navigation. Specialized scene/prefab/editor paths were retained on their format-required APIs.
- **1C — passed on the three declared editor lanes available on this machine.** Each lane passed 19 importer tests covering files, nested directories, the documented ignore policy, source `.meta`, symlink rejection on macOS, collision preflight, injected stage/commit rollback, package writability, read-only rejection, balanced targeted imports, and a 1,000-top-level-item batch. Traversal/copy completes before one short balanced asset-editing scope; directories use `ImportRecursive`; normal import issues no global refresh or force-overlap pass. Windows reparse-point behavior remains represented by the platform-specific guard but cannot be executed on this macOS checkpoint machine.
- **1D — passed.** The 1,000-add controller test records one bulk catalog application, one view publication, and zero whole-project projection rebuilds, including a callback during the owned catalog batch and a later `projectChanged` acknowledgement.
- **1E — passed.** The durable probe is `Documentation~/qa-harness/Phase1PackageMatrixProbe.cs`. On each of 2022.3.62f3, 6000.0.64f1, and 6000.3.10f1 it recorded 44/44 passing rows: embedded/local package x Auto Refresh on/off x writable policy, create folder, native create, targeted import, case-only rename, move, copy, delete, external targeted import, Undo/Redo, and postprocessor callback. The existing local/embedded writable policy is therefore preserved; registry and git packages remain read-only.

Verification summary at this checkpoint:

- final critical gate: 5/5 on all three editor lanes; final bulk-status replay: 7/7 on 2022.3 and 6000.0, 8/8 on 6000.3;
- recipe call-count integration: 4/4 on each editor lane;
- external importer integration: 19/19 on each editor lane;
- package API matrix: 132/132 recorded rows across the three editor lanes;
- full graphics-enabled Unity 6000.3 EditMode suite: 915 total, 909 passed, 0 failed, 2 environment-dependent UI tests inconclusive, 3 fixture-dependent tests ignored, and 1 heavy search benchmark explicit.

Raw checkpoint reports were produced in the disposable `/private/tmp/pp-perf-phase01-results` and `/private/tmp/pp-phase1-*` paths. The tables and gate outcomes above are the durable evidence; the disposable projects are not release artifacts. Phase 2 was subsequently implemented at the separately recorded checkpoint below.

### Phase 2 — complete incremental catalog ownership

This phase removes remaining `O(project)` mutation behavior.

1. Evolve the existing index or replace it according to the section 7 architecture gate; remove the mutation side of `CrossCacheCoordinator` that becomes redundant.
2. Move folder records into the canonical owner; folders must no longer exist only in a projection builder.
3. Maintain direct children by parent so plain browsing does not rebuild every folder bucket.
4. Maintain type counts incrementally using before/after records.
5. Maintain non-empty counts incrementally.
6. Maintain tag counts/search documents incrementally for changed paths.
7. Apply folder moves/deletes by enumerating the affected subtree through owned child links or a path-prefix index. Never scan unrelated records.
8. Apply large file batches grouped by parent. Remove the fixed “32 changes means full rebuild” behavior; use a measured crossover based on affected records/parents, and keep full replacement only when it is actually cheaper and runs off the critical UI path.
9. Make search mutation reachable only from catalog application/deltas. Search may remain a focused postings owner; remove paired public calls where callers independently write the index and gram mirror.
10. Delete superseded cache coordination code after migration. Do not leave the old and new architectures running together.

### Phase 2 implementation evidence — 2026-08-07

#### Architecture-gate decision

The section 7 architecture comparison selected option (a): evolve `InMemoryAssetIndex` and delete the redundant mutation paths. The existing index already owned GUID identity, record equality, search-version coupling, persistence hydration, and every controller query. Replacing it would have added a second conversion/migration boundary without removing a real owner. The evolved owner now also holds canonical folder records, case-insensitive path lookup with authoritative spelling preserved, direct file/folder children by parent, type membership/counts, non-empty-folder state, record/catalog versions, subtree traversal, and atomic batch application.

`AssetCatalogDelta` and `AssetCatalogMove` are the only new internal catalog concepts. They are defined beside the owner rather than as a new layer or public DTO family. The delta is earned because it is the atomic before/after result consumed by search postings, tag counts, folder presentation, browse buckets, presentation invalidation, and the small UI icon caches. No new interface, dependency, architectural layer, or parallel catalog was added.

`CrossCacheCoordinator` remains only as the existing boundary translator and one-way delta fanout. Its former size/eligibility gate, fixed 32-change fallback, independent file-index/search mutations, and full-projection fallback coordination were deleted. `AssetChangeSyncer` now coalesces publication for resolved deltas and schedules authoritative recovery for incomplete observations; it no longer mutates another index or rebuilds every projection. A mixed resolved/unresolved callback is atomic: it commits nothing and requests recovery, so no unresolved path can be silently dropped.

#### Owned invariants and operation shape

- Full synchronizer hydration/replacement is one catalog replacement; ordinary changes use one normalized `Apply` call.
- File and folder lookup is case-insensitive while stored record spelling remains authoritative. Case-only moves are not treated as no-ops.
- Direct browse buckets rebuild once per affected parent. A 1,000-file callback into one parent applies one catalog delta and sorts that parent once; there is no fixed size cap.
- Folder move/delete traversal uses the catalog's child links and visits only the owned subtree. Unrelated records are neither enumerated nor rewritten.
- Type counts, representative type membership, direct-child/non-empty counts, tag counts/path membership, and warm search postings update from before/after delta records. Tag-only views iterate matching tag paths rather than all catalog records.
- Search posting mutation consumes `AssetCatalogDelta`; production callers no longer issue paired index-plus-search writes.
- Folder-only catalog changes do not advance the record version and therefore cannot force a gram-index rebuild.
- File-only deltas do not rebuild or republish the complete folder-entry list. Non-empty-folder and asset-icon UI caches consume only the affected delta paths.

#### Before/after measurements

The before capture was taken after Phase 1 and before Phase 2 behavior edits. The after capture used the final Phase 2 code on the same disposable project, machine, editor, fixture construction, and operation order. Environment: Mac15,6; Apple M3 Pro; 18,432 MB; macOS 26.5.1; Unity 6000.3.10f1; domain reload enabled; Unity Profiler disabled; structured instrumentation enabled only for the measured interval. Each row is one observation, not P50/P95.

The controller fixture's `MutableRecordSource.BuildRecord` deliberately remained its existing linear list lookup for comparability. Consequently, the 1,000-path elapsed samples include an artificial `O(assets * 1,000)` test-boundary lookup that the production record sources do not use. Rebuild/enumeration counters and the catalog tests are the acceptance evidence for panel-owned scaling; elapsed numbers are still reported without adjustment.

| Fixture | Assets / folders | 1,000-path callback before -> after (ms) | Callback rebuilds / full enumerations before -> after | Folder delete before -> after (ms) | Delete rebuilds / full enumerations before -> after |
| --- | ---: | ---: | ---: | ---: | ---: |
| normal | 2,200 / 200 | 147.116 -> 145.861 | 1 / 1 -> 0 / 0 | 32.469 -> 4.344 | 1 / 1 -> 0 / 0 |
| large | 22,000 / 2,000 | 828.971 -> 691.110 | 1 / 1 -> 0 / 0 | 224.905 -> 32.984 | 1 / 1 -> 0 / 0 |
| stress | 100,000 / 10,000 | 8,971.278 -> 5,693.530 | 1 / 1 -> 0 / 0 | 1,047.770 -> 199.769 | 1 / 1 -> 0 / 0 |

Folder-delete elapsed time fell by approximately 86.6%, 85.3%, and 80.9% in the normal, large, and stress observations. The 1,000-path stress observation fell by approximately 36.5%; the normal delta is within single-sample noise. All six after operations performed zero `RebuildProjectionCache` calls and zero full record enumerations. The durable controller test also asserts one coalesced publication, zero full synchronizer runs, zero projection rebuilds, and zero full enumerations for a 1,000-path external callback.

#### Verification and gate disposition

- Persistence/hydration suite: 34/34 on each of the same three editor lanes after changing full synchronization to atomic catalog replacement.
- Focused Unity 6000.3 gates: index 14/14 before the final two added assertions; controller projection 51/51 before the durable callback assertion and 1/1 for the added 1,000-path callback gate; external coordinator 14/14 before the final atomic-recovery assertion; and `AssetChangeSyncer` 6/6. Every added assertion is included in the final 197/197 aggregate above.
- Final graphics-enabled Unity 6000.3 whole-package suite: 923 total, 918 passed, 0 failed, 1 graphics/focus-dependent test inconclusive, and 4 fixture/explicit tests skipped or ignored. The run also exposed a real recycled-row lifecycle fault: an abort was queued on a `TextField` immediately hidden by rebinding, whose element scheduler need not tick. Production cancellation now uses one editor-update notification for both hidden and detached pooled rows; its focused fixture finished with 10 passed, 0 failed, and the same 1 real-focus check inconclusive on each of 2022.3.62f3, 6000.0.64f1, and 6000.3.10f1.

Phase 2 steps 1-10 and the catalog/call-count portions of its gate pass. One literal wording conflict remains for review: the Phase 2 gate imports *all* section 4 complexity contracts, including “visible tree delta; never `O(project)`,” while Phase 3B explicitly owns “Stop rebuilding/copying the complete folder list for one folder mutation” and lazy visible-row updates. Phase 2 removed unrelated **record** scans and file-only folder-list publication, but a folder add/move/delete still materializes the current complete flat folder presentation for the existing eager sidebar. Therefore the full visible-tree complexity clause cannot honestly be marked passed without beginning Phase 3B. This checkpoint does not weaken that contract, does not start Phase 3, and stops for reviewer disposition on whether the clause is intentionally a Phase 3 gate or Phase 3B must be pulled forward.

### Phase 3 — direct native browsing and pending-entry promotion

This phase makes first browse independent of global indexing. It does not defer Unity import or change Undo chronology.

#### 3A. Direct folder browse

- Add a real filesystem boundary for enumerating exactly one virtual project folder's direct children.
- Map `Assets/...`, embedded packages, registry/git packages, and local `file:` packages through `ProjectPaths`/registered package roots. Never assume `Path.GetFullPath("Packages/...")` is the physical location.
- Feed those entries into the same canonical catalog as path-keyed pending entries; do not create fake GUID-bearing `AssetRecord` instances or a second browse-only truth.
- Mark direct-folder observations with provenance, observed folder, and generation. Seeing a path proves presence. A complete direct-child observation may reconcile only that folder against older pending observations; it cannot imply project-wide absence, delete authoritative state without Unity acknowledgement, or overwrite a later authoritative/live delta.
- Display filename, folder/file kind, extension-inferred type, and basic icon immediately.
- Resolve expensive Unity metadata only for visible ambiguous rows, or in a bounded background/main-thread enrichment job.
- Reuse the asset-area virtualization already present.
- Apply the shared Unity-ignore and symlink/reparse policy from section 6; never expose `.meta` sidecars or traverse outside a mapped root.

#### 3B. Lazy sidebar hierarchy

- Stop rebuilding/copying the complete folder list for one folder mutation.
- Give `FolderTreeWidget` an initial root plus folder deltas or child-query access from the catalog.
- Enumerate/materialize children when a node is expanded or must be revealed.
- Update only affected visible rows and ancestors on add/remove/move.
- Preserve the mandatory sidebar scroll contract: the single outer `zep-sidebar-scroll` remains the only sidebar scroll owner; the tree's nested `ListView` remains non-scrolling and fully content-sized.

#### 3C. Pending-entry promotion and synchronous creation

Direct browse can observe a physical path before Unity has imported it. The catalog must hold that observation as a path-keyed pending entry, display only filesystem-derived facts, and promote it atomically when `AssetPathToGUID`/postprocessor data becomes authoritative. A disappeared pending path may be removed after a complete newer observation of the same folder; an authoritative imported record is removed only by an authoritative delete/move or guarded reconciliation.

Text/script creation keeps this sequence:

```text
sanitize + choose unique path
  -> write file once
  -> one synchronous targeted ImportAsset
  -> obtain stable authoritative GUID
  -> record existing exact Undo transaction
  -> apply/reveal the authoritative catalog record locally
  -> later postprocessor acknowledgement is idempotent
```

The section 4.4 probes support this boundary on all three semantic lanes. Phase 0 must measure the targeted import separately from compiler/domain-reload time. If the import call itself makes the visible-latency SLO unattainable, stop and write a separately reviewed deferred-import/Undo design; do not add pending history states, manufacture GUIDs, or weaken Undo within this rewrite.

**Gate:** direct pending entries never masquerade as imported assets, promote exactly once without duplicate rows, and cannot overwrite a newer live mutation. Every shipped text/code recipe retains exact Undo/Redo across repeated reloads and issues one targeted import, zero global refreshes, and zero full panel scans.

### Phase 4 — startup, reconciliation, and persistence

The first useful browse must not wait for a global cache signature or global metadata snapshots. The lifecycle must still preserve window-closed Undo and authoritative event ownership.

1. Keep one lightweight session owner after domain reload to restore `SessionUndoHistoryStorage`, install `UnityProjectUndoBridge`, and subscribe required Unity events. Do not let that correctness owner automatically compute a signature, enumerate the project, build addressables/search, or publish panel projections.
2. If a concrete Unity callback or window-closed Undo occurs before catalog hydration, perform the required file/history action and record bounded concrete deltas or a durable dirty marker; do not synchronously hydrate the whole catalog.
3. On first actual panel use, construct/attach the catalog cheaply and render the current folder through direct native enumeration.
4. Start global reconciliation separately after the useful browse.
5. Create one main-thread registered-package-root snapshot from Package Manager and pass its immutable pure-data mapping to workers and all physical-path consumers. Refresh it atomically on package topology changes; remove the raw `Library/PackageCache` membership scan.
6. Move filesystem walking, guarded `.meta` parsing, normalization, addressable-folder expansion, and search-document construction to workers.
7. Do not call Unity APIs from workers. Capture only the minimum Package Manager, label, type, importer, and addressables state required on the main thread, under measured per-frame budgets where possible.
8. For ambiguous types that require Unity, collect the candidate set on workers and process it on the main thread under a measured per-frame time budget.
9. Isolate current `.meta` parsing behind a versioned compatibility adapter. Measure worker parsing against main-thread Unity snapshots, fail closed on unknown serialization, and make Unity acknowledgement/correction authoritative. Do not expand reliance on undocumented YAML without evidence.
10. Move search warm/build off the editor thread. Build an immutable/versioned posting snapshot on a worker and swap it on the main thread only if its catalog generation is still current.
11. Split addressables discovery into the smallest safe main-thread Unity/reflection capture plus pure record expansion and keep it off first useful paint. Select and test a real settings/dependency invalidation signal; if none is complete, rebuild asynchronously on relevant concrete events. Package absence produces an authoritative empty set. An unknown reflection shape publishes no positive badge, marks the snapshot incomplete for diagnostic/retry, and never falls back to record-by-record lookup on the critical path.
12. First remove cache validation from the first-use browse path. Then measure the current snapshot plus a generation/dirty marker against a snapshot-plus-incremental-journal design. Add a journal only if startup/reconciliation measurements justify its serialization, compaction, recovery, and reload complexity.
13. Whichever persistence format wins must use atomic, corruption-detecting writes. If it has a journal, compact it on a worker or at a measured safe lifecycle point.
14. Keep a background full reconciliation for files changed while Unity/the panel was not observing them. If its generation is invalidated, merge/replay newer deltas or discard/restart; never call the current synchronous main-thread rebuild fallback.
15. Never run synchronous search or addressables warmup merely because assemblies reloaded. Measure the remaining lightweight closed-panel owner and require effectively zero idle allocations/work when no event occurs.

#### Persistence validation experiments

**EXPERIMENT:** determine whether a stored `AssetDatabase.GlobalArtifactDependencyVersion` can be used as a cheap trust signal across:

- domain reload;
- editor restart;
- asset content changes;
- `.meta` GUID replacement;
- label changes;
- empty-folder create/delete;
- package add/remove/update;
- VCS branch checkout while Unity is closed.

Unity documents that the version changes during refresh when something changes that can invalidate an artifact. That does not by itself prove it captures empty folder topology or every metadata state used by this panel.

Sources: [Unity 2022.3 `AssetDatabase.GlobalArtifactDependencyVersion`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.GlobalArtifactDependencyVersion.html) and [Unity 6000.0](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.GlobalArtifactDependencyVersion.html).

Do not remove current correctness validation until these cases are measured. If no cheap complete signal exists, load direct folder contents immediately and reconcile the global search catalog in the background rather than blocking first paint.

**Gate:** cold/warm visible browse targets pass; no main-thread project-wide label/type/path/addressables loop remains on the critical open path; invalidated scans do not fall back to a main-thread full rebuild; stale background snapshots cannot overwrite live deltas; window-closed Undo/Redo still works after reload without catalog prewarm; and persistence evidence records why the simpler current-snapshot evolution or a journal won.

### Phase 3-4 implementation evidence — 2026-08-07

#### Direct browse, pending identity, and lazy hierarchy

Phase 3 uses one direct-folder boundary, `IDirectFolderAssetSource`, to observe exactly one mapped folder level. The production native implementation enumerates files and directories without recursion, applies the shared ignore/symlink policy, never emits `.meta` sidecars, and returns only filesystem facts: path, file/folder kind, and extension-inferred type. It does not manufacture a GUID. `ProjectPaths` now publishes one immutable registered-package-root snapshot from `PackageInfo.GetAllRegisteredPackages`; built-in module shells are excluded, all physical-path consumers share that snapshot, and the former raw `Library/PackageCache` membership fallbacks were deleted.

`InMemoryAssetIndex` remains the canonical owner. It now keeps path-keyed pending entries separately from authoritative `AssetRecord` identity, with observation provenance and catalog generation. A complete observation reconciles only older pending siblings in the observed folder. Authoritative records win over direct observations, stale observations are rejected, and an authoritative import promotes a pending path once by removing the pending row in the same catalog delta that installs the GUID record. Basic pending rows flow through the existing browse projection and virtualization; search, tags, and authoritative metadata do not consume them.

The sidebar now queries immutable parent-indexed child snapshots and a single-entry lineage lookup instead of receiving a copied project-wide folder list on every change. Current/selected lineage is materialized synchronously; potentially large expanded sibling sets are deferred. Catalog deltas invalidate only affected parents. Lazy selection is owned by stable path so Unity's native `ListView` selection bookkeeping cannot turn a two-row repaint into a project-size operation. Reveal scrolling targets only the existing outer `zep-sidebar-scroll`; the content-sized nested list remains non-scrolling and is never moved through `ListView.ScrollToItem`. This resolves the Phase-2/Phase-3B visible-tree gate conflict recorded above.

Focused tests prove one-level enumeration, no `.meta`/nested leakage, unknown `.meta` serialization failing closed, no fake identity, generation rejection, idempotent one-time promotion, authoritative-row protection, child-query laziness, affected-parent invalidation, and selected-lineage publication before complete sibling materialization. Existing synchronous text/code creation and its exact Undo chronology are unchanged: one write, one targeted synchronous import, stable identity, history record, local authoritative apply, and idempotent acknowledgement.

#### First-use lifecycle and background reconciliation

The reload-resident `ZepControllerHost` now restores Undo/event ownership without hydrating the catalog, computing a signature, or warming search/addressables. `BeginPanelUse` performs bounded direct observations of Packages and the current folder, publishes the useful browse, and schedules reconciliation for a later editor turn. Concrete callbacks before hydration are applied as bounded overlays and write a durable dirty marker; unknown changes schedule background reconciliation instead of synchronously rebuilding.

Cache signature walking and deserialization run on a worker after the useful browse. On a cache miss, the main thread captures Unity-owned state only after first paint: the all-path array and ScriptedImporter extension registry are captured once, ambiguous candidate filtering runs on a worker, and authoritative type calls are processed in measured 2 ms main-thread slices with at least one candidate of forward progress per update. Native walking, guarded version-1 `.meta` parsing, package-path normalization, addressable-folder expansion, and search-document construction are pure worker work. Search and addressables adopt only generation-current immutable results. Unknown addressables reflection shapes publish no positive state, package absence publishes an authoritative empty result, and neither path falls back to record-by-record critical-path lookup.

An asset callback increments the reconciliation generation and records a concrete scan overlay. A stale worker result is discarded and the asynchronous pipeline restarts; the invalidation path never invokes the synchronous fallback. The overlay also protects concrete imports/deletes that occur while a scan is running. The existing explicit Refresh and exceptional source/worker-fault fallback remain authoritative recovery paths and are not used for generation invalidation.

#### Persistence experiment and decision

The experiment retained the versioned atomic snapshot plus a five-byte durable dirty marker. A journal was rejected for this phase because direct browse already removes both cache validation and replay from first useful paint, while the marker makes a pre-hydration mutation reject the snapshot in about 2 ms. A journal would add replay ordering, compaction, corruption recovery, and reload states without improving the measured visible-open path. Atomic temp-write plus replace remains the snapshot commit; a successful replacement clears the marker.

One stress-class persistence observation used 118,107 records and 10,064 folders (the disposable persistence probe contained extra action fixtures, so it is not the count-matched latency fixture):

| Persistence operation | Observation |
| --- | ---: |
| Snapshot size | 13,556,085 bytes |
| Existing snapshot signature + deserialize | 2,084.589 ms |
| Dirty-marker atomic write | 1.961 ms / 5 bytes |
| Full snapshot validate + atomic rewrite | 1,015.196 ms |
| Revalidate + reload rewritten snapshot | 1,522.018 ms |

The multi-second snapshot work is therefore treated as background reconciliation evidence, not first-paint work. The dirty-marker test proves that a marked snapshot cannot hydrate and that a later atomic save both replaces it and restores loadability.

#### Count-matched before/after measurements

The before capture used the reviewed Phase-2 checkpoint code immediately before Phase-3 behavior edits. The after capture used the final Phase-4 code, including worker cache validation, budgeted authoritative-type capture, and outer-scroll-only lazy reveal. Machine, editor, fixture construction, action order, instrumentation, and counts match: Mac15,6; Apple M3 Pro; 18,432 MB; macOS 26.5.1; Unity 6000.3.10f1; domain reload enabled; Unity Profiler disabled; structured package instrumentation enabled only for each action.

As in the earlier checkpoints, each cell is one observation rather than P50/P95. These observations prove the blocking Phase-3/4 call graph and initial visible-latency gate; the repeated distribution/allocation matrix remains a Phase-7 release task.

| Fixture | Discovered assets / folders | Action | Panel before -> after (ms) | Built-in Project window after (ms) |
| --- | ---: | --- | ---: | ---: |
| normal | 2,204 / 202 | cold open | 195.384 -> 194.045 | — |
| normal | 2,204 / 202 | warm open | 32.530 -> 26.401 | — |
| normal | 2,204 / 202 | folder switch | 3.774 -> 4.905 | 0.302 |
| normal | 2,204 / 202 | folder create | 39.746 -> 36.446 | 21.053 |
| normal | 2,204 / 202 | text create | 13.106 -> 15.199 | 9.370 |
| large | 22,004 / 2,002 | cold open | 206.477 -> 177.521 | — |
| large | 22,004 / 2,002 | warm open | 40.010 -> 20.027 | — |
| large | 22,004 / 2,002 | folder switch | 5.730 -> 6.645 | 0.142 |
| large | 22,004 / 2,002 | folder create | 118.335 -> 96.470 | 60.239 |
| large | 22,004 / 2,002 | text create | 33.339 -> 32.360 | 26.400 |
| stress | 127,816 / 10,008 | cold open | 251.508 -> 176.140 | — |
| stress | 127,816 / 10,008 | warm open | 82.609 -> 20.033 | — |
| stress | 127,816 / 10,008 | folder switch | 15.631 -> 13.883 | 0.530 |
| stress | 127,816 / 10,008 | folder create | 580.285 -> 443.602 | 297.192 |
| stress | 127,816 / 10,008 | text create | 157.533 -> 148.607 | 139.750 |

The worst observed cold useful browse is 194.045 ms against the 200 ms objective; warm browse is 26.401 ms against 100 ms; and folder switch is 13.883 ms against 16 ms. The stress fixture exceeds the stated 1,000-direct-child switch scope with approximately 10,000 root siblings, yet the lazy tree keeps the observation below the objective. Normal/large switch variation and the normal text-create difference are single-sample noise candidates and are reported without adjustment.

For every cold and warm open, the counters for full synchronizer runs, full record enumeration, folder-tree walks, projection-cache rebuilds, search builds, `FindAssets`, `GetAllAssetPaths`, GUID/type/label queries, global refresh, invalidated synchronous fallback, and closed-panel work were all zero. Cold open performed three small visible-state publications and no global work. Across all fixtures, folder create issued exactly one `AssetDatabase.CreateFolder`, one publication, zero global refreshes/scans/rebuilds; text create issued exactly one targeted `ImportAsset`, no publication duplicate, zero global refreshes/scans/rebuilds.

#### Verification and gate disposition

- Core/catalog semantics: 202/202 on Unity 2022.3.62f3, 202/202 on 6000.0.64f1, and 202/202 on 6000.3.10f1.
- Native source, cache, synchronization, dirty-marker, generation, and pure candidate-filter suite: 38/38 on each of those three lanes.
- Shell/lazy-sidebar semantics: 101/101 on each of those three lanes. The graphics-backed selection and outer-owner reveal-scroll cases pass on 6000.3.10f1.
- Final graphics-enabled Unity 6000.3 whole-package suite: 934 total, 929 passed, 0 failed, 1 real-focus/fixture-dependent test inconclusive, and 4 fixture/explicit-heavy tests skipped or ignored.
- The existing window-closed filesystem Undo/Redo gate remains 2/2 without catalog prewarm; creation call-count remains 4/4; all are also covered by the clean whole-package run.

Raw disposable reports are under `/private/tmp/pp-perf-phase01-results`: the before series is `phase34-before-{2k,20k,100k}` and the final after series is `phase34-after-ultimate-{2k,20k,100k}`. Compatibility and whole-suite XML reports use the `/private/tmp/pp-phase34-*ultimate*` prefixes. The count-matched probe-created rows were removed from the disposable fixtures after the reports were written; the fixture cleanup is not a package/worktree change.

**Phase 3 — passed.** Direct entries never masquerade as authoritative records; same-folder generation rules and authoritative precedence are tested; promotion is atomic and idempotent; lazy hierarchy updates are parent-scoped; the one-scroll-owner contract is preserved; and synchronous text/code import/Undo behavior retains its exact call counts on all declared lanes.

**Phase 4 — passed.** Count-matched cold/warm useful-browse observations meet the initial targets. The critical open path contains no project-wide Unity loop or addressables/search work. Worker filtering plus budgeted main-thread enrichment keeps cold reconciliation outside first paint. Invalidated scans restart asynchronously, stale results cannot overwrite overlays, the reload owner remains lightweight, and the measured snapshot-plus-dirty-marker design won over a journal. Stop here for review before Phase 5; no Phase-5 result-publication or grammar work is included in this checkpoint.

### Phase 5 — search and result publication

1. Preserve the documented custom search grammar and result semantics.
2. Keep the existing narrow-query performance wins unless benchmarks prove a replacement is faster.
3. Build/rebuild postings on workers against an immutable catalog snapshot.
4. Apply single-record posting changes incrementally.
5. Return lazy result views or stable identifiers for broad matches; do not allocate full `ZepAssetEntry` objects for thousands of off-screen rows.
6. Remove the controller/shell double-build pattern rather than relying only on the current one-slot memo.
7. Make folder/tag search use catalog indices instead of scanning every merged folder/record on each query where possible.
8. Add end-to-end typing measurements, including controller projection, shell refresh, UI collection update, allocations, and repaint—not only `AssetSearchService.Search`.

**Gate:** current correctness corpus passes, existing search timings do not regress without an intentional recorded reason, and real-panel PRF-03 is executed.

#### Phase 5 implementation evidence — 2026-08-08

Broad file matches are published as stable ordinal arrays bound to the immutable record-list generation that produced them. `LazyAssetEntryList` resolves records and creates `ZepAssetEntry` objects only when UI virtualization indexes a visible row. A later mirror compaction/rebuild replaces the record generation rather than clearing one retained by an existing result. The existing immutable worker build/adoption path and per-record `ApplyCatalogDelta` posting mutation remain in place and are covered by stale-worker and compaction-stability tests.

##### Real-panel PRF-03 before/after

The before capture used the reviewed Phase-4 checkpoint immediately before Phase-5 behavior edits. The final after capture used the final Phase-5 code. Both drove the real `ZepWindow` path—pane search mutation, controller projection, pane publication, shell refresh, mounted UI collection update, and repaint—on the same disposable stress project, query prefixes, operation order, and machine. Environment: Mac15,6; Apple M3 Pro; 18,432 MB; macOS 26.5.1; Unity 6000.3.10f1; domain reload enabled; one warm-up cycle followed by five measured cycles of the ten prefixes in `asset00000`. The fixture contributed 100,000 files and 10,000 folders; the live catalog was 127k-class and the first prefix returned 126,747 results.

Unity Mono did not expose a usable thread-allocation counter; its zero delta is not reported as an allocation measurement. The table uses Unity's `GC Allocated In Frame` profiler recorder instead.

| Real-panel metric, 50 measured keystrokes | Phase 4 before | Phase 5 after | Change |
| --- | ---: | ---: | ---: |
| end-to-end wall median | 91.996 ms | 49.145 ms | -46.6% |
| end-to-end wall P95 | 133.615 ms | 78.179 ms | -41.5% |
| end-to-end wall maximum | 245.537 ms | 83.905 ms | -65.8% |
| frame GC median | 3,552,156 B | 2,903,028 B | -18.3% |
| frame GC P95 | 9,862,899 B | 6,589,551 B | -33.2% |
| frame GC maximum | 67,178,737 B | 37,427,315 B | -44.3% |
| exact ten-character prefix median, 10 results | 19.246 ms | 5.960 ms | -69.0% |
| exact ten-character prefix maximum | 24.320 ms | 6.987 ms | -71.3% |

Every measured keystroke performed exactly one search query, one projection request/build, one view-model projection publication, and one asset-collection publication; every one performed zero search full builds. Before Phase 5, the same keystroke issued two projection requests and relied on the one-slot memo to hide the second build. The final run materialized at most 45 asset entries for any keystroke—even when the published result held 126,747 stable IDs—and materialized only 10 for the exact final prefix.

##### Engine timing and allocation gate

The existing 50,000-record Unity Mono benchmark was measured three times after three warm-ups per shape. Its July absolute baseline was stale after the host moved from macOS 26.3 to 26.5.1 and also failed against an untouched same-session Phase-4 control. The control was therefore run in the same lean disposable project before recalibration. Final Phase 5 was faster on all 14 shapes; representative Phase-4 control -> Phase-5 three-run medians were: bare `ab`, 10.984 -> 0.496 ms; scoped `ab`, 12.782 -> 4.800 ms; bare `prefab`, 4.432 -> 2.764 ms; scoped `prefab`, 5.525 -> 3.838 ms; and bare narrow, 0.141 -> 0.134 ms. `BENCHMARK_BASELINE.md`, the explicit test constants, and the side-harness orientation values now record the final three-run medians; the explicit regression gate passes.

The independent .NET 8 PRF-03 harness reproduced all 14 result counts. Final representative allocations were 79,705 B/call for 19,837 bare `ab` matches (77.8 KiB, approximately one four-byte stable ID per match plus fixed overhead), 79,745 B/call for the scoped form, 841 B/call for a one-result bare narrow query, and 881 B/call for its scoped form. The result wrapper therefore remains proportional to match identifiers, not `ZepAssetEntry` objects or object references, while UI materialization remains proportional to visible rows.

##### Verification and gate disposition

- Final core/search/catalog semantics: 204/204 on Unity 2022.3.62f3, 204/204 on 6000.0.64f1, and 204/204 on 6000.3.10f1.
- Shell/result-collection semantics: 101/101 on each of those three lanes. The three directly affected view/metadata/Quick Look fixtures pass 44/44 with graphics on 6000.3; their 40 non-graphics-dependent cases pass on 2022.3 and 6000.0, while the remaining four correctly require a graphics device.
- Independent search grammar harness: 73/73. Final Unity search plus adversarial corpus: 33/33 on each declared lane.
- Explicit 50k search timing gate: passed on Unity 6000.3.10f1. Independent .NET allocation/corpus gate: all 14 shapes passed.
- Final graphics-enabled Unity 6000.3 whole-package suite: 937 total, 931 passed, 0 failed, 2 graphics/focus-dependent tests inconclusive, and 4 fixture/explicit-heavy tests skipped or ignored. A known reveal-scroll layout flake failed in the preceding aggregate run and passed its immediate targeted rerun; the final-code aggregate is clean.

Raw disposable reports are `/private/tmp/pp-perf-phase01-results/phase5-before-100k-search.tsv` and `phase5-after-final-100k-search.tsv`. The final compatibility, timing, and whole-suite XML reports use the `/private/tmp/pp-phase5-*final*` prefixes. These are evidence artifacts in disposable projects, not package/worktree changes.

### Phase 7 — cleanup and release verification

1. Delete obsolete full-refresh completion hooks, cache mirrors, compatibility shims, counters used only during migration, and superseded architecture.
2. Keep explicit user Refresh and corruption recovery.
3. Update `Documentation~/overview.md`, README performance claims, manual QA, and benchmark baselines with measured numbers.
4. Run the full EditMode suite on all three section 4.4 lanes.
5. Run live macOS QA plus Windows and Linux mutation/import smoke tests.
6. Run the performance matrix in section 12.
7. Profile a 30-minute session for memory growth and idle allocations.

#### Phase 7 implementation evidence (2026-08-08)

Graphics-enabled full EditMode runs against the exact Phase 7 worktree reported
zero failures on every installed semantic lane: 947 total / 945 passed / 0
inconclusive / 2 skipped on 2022.3.62f3; 947 total / 942 passed / 1
inconclusive / 4 skipped on 6000.0.64f1; and 949 total / 943 passed / 2
inconclusive / 4 skipped on 6000.3.10f1. The CI matrix now targets the current
2022.3.76f1, 6000.0.79f1, and 6000.3.19f1 patches; those exact workflow runs
must still complete before release.

Normal, large, and stress fixture observations, call counters, the full-panel
127k-record search series, Phase-6 retained-exception measurements, 30-minute
session profile, and explicit coverage gaps are recorded in
[`Documentation~/PERFORMANCE-RELEASE-VERIFICATION.md`](PERFORMANCE-RELEASE-VERIFICATION.md).
Warm-open observations were 25.390, 30.588, and 98.282 ms. The stress query's
tenth character measured a 7.108 ms median and 10.177 ms maximum across five
cycles. Folder/text creation emitted zero panel full scans, projection rebuilds,
or global refreshes at every size. A single 20.169 ms stress folder-switch
observation exceeded the initial 16 ms target and is retained rather than
hidden.

This machine exposes only case-insensitive APFS project storage and no second
writable volume. Windows, Linux, case-sensitive APFS, a physical cross-volume
local-package run, and the exhaustive median/P95 section-12 matrix were not
fabricated. Deterministic safety/fault-injection tests remain green, but these
live release cells stay open in the manual QA plan.

## 9. File-by-file implementation map

This map names likely owners; the implementation agent must revise it if the reviewed code changes first.

| File/subsystem | Required direction |
| --- | --- |
| `ZepCreateOrchestrator.cs` | Remove completion refreshes; return/use operation results; make fallback discovery scoped/event-driven. |
| `ZepShellView.cs` | Landing becomes reveal/selection/focus only; no delayed index refresh. |
| `ZepPingReceiver.cs` | Just-created ping uses concrete-path acknowledgement/reveal; no ordinary full refresh. Preserve 2022.3/6000.0 versus 6000.3 initialization paths. |
| `ZepCreateAssetSupport.cs` | Move Unity/filesystem mutation out of UI ownership or turn this into the concrete creation boundary; remove redundant native-asset save/import calls; keep one synchronous targeted text/code import and existing exact Undo chronology. |
| `ZepController.cs` | Ordinary operations apply one change batch and publish one delta; bulk accumulates; full refresh is explicit/recovery only. |
| `AssetChangeSyncer.cs` | Normalize each Unity callback batch; acknowledge already-applied paths idempotently; coalesce editor-turn publication; unknown `projectChanged` events schedule background reconciliation rather than immediate main-thread full work. |
| `CrossCacheCoordinator.cs` | Temporary Phase-1 owner; then remove or reduce after canonical catalog migration. Do not preserve it as a second architecture. |
| `InMemoryAssetIndex.cs` | Evolve/replace with canonical file+folder catalog, path-keyed pending entries, children, counts, explicit path/case semantics, version, atomic batch apply and pending-to-authoritative promotion. |
| `FolderEntriesBuilder.cs` | Eliminate whole-list rebuilding for local updates; likely absorbed by catalog/query output. |
| `BrowseProjectionService.cs` | Query direct children from catalog; remove all-folder cache rebuild and project-wide type recount; preserve lazy entry construction. |
| `TagProjectionService.cs` | Keep tag-definition/presentation responsibility, but update assignments/counters from deltas rather than full record/folder walks; replace `RefreshTagPresentationCache`'s global one-assignment path. |
| `AssetSearchService.cs` | Delta-only mutation input, background immutable build/swap, incremental posting changes, lazy broad results. |
| `FolderTreeWidget.cs` | Consume initial hierarchy plus deltas/child queries; patch affected nodes; preserve single outer sidebar scroll ownership. |
| `SidebarWidget.cs` | Forward folder deltas/child access without adding a scroller or parallel hierarchy cache. |
| `ZepState.cs` | Stop copying the complete folder list for one delta; expose meaningful folder/tree change notifications. |
| `AssetIndexSynchronizer.cs` | Background reconciliation, bounded Unity enrichment, generation-safe apply, no critical-path global snapshots. |
| `PersistentAssetIndexCache.cs` | Simplest measured durable format (current snapshot evolution or justified journal); atomic writes; background validation/reconciliation; no blocking whole-tree signature before useful browse. |
| `HybridAssetRecordSource.cs` | Retain only where measured useful; separate native discovery from bounded authoritative enrichment. |
| `NativeFileSystemAssetRecordSource.cs` | Reuse pure enumeration primitives for direct-folder and global worker scans; isolate/fail closed undocumented `.meta` parsing; keep package physical/virtual mapping correct. |
| `ProjectPaths.cs` / package-root discovery | Replace independent mapping/cache scans with one main-thread registered-package-root snapshot shared by every consumer; never infer registered packages from raw `Library/PackageCache`. |
| `ZepAddressableUtility.cs` | Instrument and split bounded Unity/reflection capture from pure expansion; cache by real dependencies and keep global work off first paint. |
| `ZepControllerHost.cs` | Retain a lightweight session owner for persisted Undo and Unity events; defer catalog hydration/search/addressables until actual use or explicit background maintenance. |
| `AssetDropOperations.cs` | Delegate external import to one boundary; no duplicate import implementation. |
| `ExternalAssetImporter.cs` | Own/test preflight, ignored same-volume staging, atomic top-level commit, short grouped targeted imports, cleanup, and per-top-level failure results. |
| `SessionUndoHistoryStorage.cs` / `ProjectCommandHistory.cs` / `UnityProjectUndoBridge.cs` | Preserve current exact creation chronology; keep the bridge available after reload even with no window. Measure full-stack JSON serialization separately and optimize only if material. Do not add deferred-create pending states in this rewrite. |

## 10. Event and lifecycle design

### 10.1 Panel-originated mutation

```text
User command
  -> one owned Unity/filesystem boundary transaction
  -> operation result/change batch
  -> canonical catalog apply
  -> one catalog delta
  -> affected pane/tree rows update immediately
  -> Unity postprocessor event arrives
  -> compare authoritative metadata
      -> no-op acknowledgement when equal
      -> corrective delta when different
```

### 10.2 External mutation

```text
each Unity OnPostprocessAllAssets callback
  -> normalize/coalesce duplicate paths inside that concrete batch
  -> idempotent canonical catalog apply
  -> queue/merge affected view work for the current editor turn
  -> publish at most once for that turn
```

Unity documents that `OnPostprocessAllAssets` is called after an import batch and provides imported, deleted, moved, and moved-from paths, paired by move index. Unity also documents that some bulk operations can cause the callback once per individual asset and that importing from the callback can trigger it again. Treat its concrete paths as the main authoritative event, not its callback boundaries. The package cannot reliably infer that callbacks separated by an editor turn or reload belong to one logical user operation, so correctness comes from atomic per-batch application plus idempotence, not a guessed operation window. Retain guarded reconciliation for events it cannot describe in supported versions.

Sources: [Unity 2022.3 `AssetPostprocessor.OnPostprocessAllAssets`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetPostprocessor.OnPostprocessAllAssets.html) and [Unity 6000.0](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetPostprocessor.OnPostprocessAllAssets.html).

### 10.3 `EditorApplication.projectChanged`

Current behavior can request a full refresh when a corresponding postprocessor batch was not suppressed.

**DECISION:** treat `projectChanged` as a debounced “something may have changed” signal, not a concrete mutation description. If a concrete postprocessor/catalog delta arrives in the same window, do nothing. Otherwise schedule background reconciliation. Do not synchronously scan the project in the event callback.

This matches Unity's contract: `projectChanged` is raised on the next editor update and carries no concrete path payload. See [Unity 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/EditorApplication-projectChanged.html) and [Unity 6000.0](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/EditorApplication-projectChanged.html).

### 10.4 Domain reload

Before any operation that can compile/reload:

- persist pending selection/reveal;
- persist the existing completed command/Undo state only after the synchronous targeted import has returned authoritative identity;
- persist the selected catalog durability format atomically;
- make callbacks generation-safe and disposable.

After reload:

- restore the visible folder directly;
- replay durable catalog deltas/snapshot;
- restore the lightweight session Undo/event owner even when the panel window is closed;
- reconcile in the background;
- never run duplicate event subscriptions, catalog hydration, addressables discovery, or synchronous search warm solely because the domain reloaded.

## 11. Correctness constraints that performance work must preserve

- Case-only renames must still reach Unity. Be careful: `AssetPath` equality is case-insensitive while the gateway deliberately treats exact string equality differently for case-only moves.
- Registry and git packages remain read-only. Embedded/local package writes are enabled only for the operation/API/editor combinations proven by Phase 1E; policy classification alone is insufficient.
- Package virtual paths must map to the registered package's real physical root through one atomic package-root snapshot. Raw PackageCache contents never establish membership.
- Native traversal must not follow symlink/reparse cycles or escape a registered asset/package root.
- `.meta` sidecars are never panel rows and ordinary external import never copies source sidecars. Unity-tree migration is outside this rewrite.
- Unity's documented ignored paths are excluded consistently by direct browse and external import and never receive imported-asset Undo records.
- External import never exposes a half-copied importable top-level payload; staging cleanup and top-level commit/failure reporting are deterministic.
- Pending native observations are path-keyed, never invent GUIDs, never claim authoritative metadata, and promote atomically exactly once.
- Empty folders must remain visible.
- Folder and file Unity labels, concrete custom types, and real base types must remain searchable.
- Zep tags remain distinct from Unity labels.
- Search grammar and documented scopes remain unchanged unless product behavior is explicitly revised.
- Rooted split panes remain clamped and reconcile correctly across folder moves/deletes.
- Expanded folder paths rewrite correctly across moves.
- Unity Undo/Redo and panel payload history must never expose an unrelated older action after a partially-recorded mutation.
- Text/code creation retains the existing synchronous import-before-history boundary; deferred-create history is not part of this rewrite.
- Background snapshots may never overwrite a newer live mutation.
- Main-thread-only Unity APIs must never be called from workers.
- Undocumented Unity API thread safety is treated as main-thread-only.
- All background jobs must unsubscribe/terminate cleanly on reload/dispose.
- The lightweight session host must preserve window-closed Undo/event correctness without triggering global indexing/search/addressables work on reload.
- No UI styling or sidebar scroll ownership changes are implied by this rewrite.

## 12. Verification matrix

### 12.1 Automated behavioral tests

Add focused tests for:

- one local create produces one catalog delta and zero full refresh requests;
- create landing staging and shell reveal produce no controller refresh call;
- identical postprocessor acknowledgement is idempotent;
- authoritative metadata promotes/replaces a path-keyed pending observation exactly once;
- duplicate paths inside one postprocessor batch are coalesced;
- a file move updates old/new parent child lists and search postings;
- a folder move rewrites only its subtree and pane/expansion paths;
- folder delete removes empty and populated subtrees;
- type/tag/non-empty counters update incrementally;
- 1,000 additions in one batch remain incremental;
- panel bulk scope applies/publishes its own batch once; later Unity callbacks acknowledge idempotently;
- one external operation split across several postprocessor callbacks applies each concrete batch correctly and publishes at most once per editor turn without relying on callback count or guessed operation identity;
- background snapshot generation cannot overwrite a live add/delete/move;
- invalidating a worker scan never invokes a synchronous full rebuild on the main thread;
- a partial direct-folder observation cannot delete unrelated or newer catalog state;
- pending path entries promote once to authoritative GUID records and preserve explicit case semantics without fake identity;
- direct package browsing maps registry/git/local/embedded roots from one registered snapshot, excludes built-in module shells, preserves the last valid/incomplete state when Package Manager is unavailable, and ignores stale/unregistered PackageCache directories;
- text/script creation completes one synchronous targeted import before existing Undo history is recorded and survives reload without a new pending history state;
- just-created ping reveal and single-path tag changes request zero full refreshes/rebuilds;
- addressables discovery is absent from first useful paint and invalid reflection/package state fails closed;
- window-closed Undo after domain reload works while catalog/search/addressables prewarm remains deferred;
- explicit Refresh still performs authoritative recovery;
- unknown `projectChanged` schedules one background reconciliation, not repeated synchronous scans.

Upgrade shell/controller fakes so `Refresh`, projection publication, and rebuild calls are counted. A no-op fake is not enough.

### 12.2 Unity integration tests

Run on the newest available patches in each semantic lane in section 4.4 and record the exact versions:

- every creation recipe;
- folder create, case-only rename, move, duplicate, delete;
- external file and recursive directory import;
- external operating-system-hidden folders, attribute-hidden ordinary files, dot names, tilde suffixes, `cvs`, `.tmp`, source `.meta` sidecars, symlinks/reparse points, duplicate filenames, staging/copy/commit rollback, top-level partial-success reporting, and Auto Refresh on/off;
- imported `.asset` custom concrete type and base-type search;
- labels on files/folders, including escaped Unicode forms;
- code/asmdef/asmref creation and repeated reload;
- prefab, scene, material, ScriptableObject, ScriptedImporter assets;
- registry, git, embedded, and local packages, including the Phase-1E per-operation write matrix and stale/unregistered PackageCache contents;
- explicit Reimport All and package topology changes;
- `OnPostprocessAllAssets` delivered as one callback and multiple callbacks for one bulk operation.

### 12.3 Performance tests

For each fixture size, record median/P95/max and allocations for:

- window first useful paint, cold and warm;
- global catalog/search ready time, cold and warm;
- switch among shallow/deep/small/large folders;
- create folder;
- create text, script, material, `.asset`, scene;
- rename/move/duplicate/delete one file;
- rename/move/delete a folder containing 1, 100, and 10,000 descendants;
- external import of 1, 100, and 1,000 files;
- external import of nested directories;
- tag one and 1,000 assets;
- type a 10-character query one character at a time;
- broad query returning tens of thousands of results;
- scroll list/grid for 30 seconds;
- idle open and idle closed for 60 seconds;
- 20 repeated domain reloads;
- window-closed Undo after each of 20 repeated domain reloads, with catalog/search/addressables initialization counted separately;
- invalidated background scans and addressables discovery on large fixtures;
- compare the same interactive actions to Unity's built-in Project window.

Every mutation result must include the call counters from section 4. A fast timing with an accidental missed update is a failure.

### 12.4 Platform and safety verification

- macOS APFS, case-insensitive and case-sensitive if available;
- Windows NTFS;
- Linux case-sensitive filesystem;
- symlink/reparse-point safety;
- locked/read-only files;
- path length and Unicode filenames;
- disk-full/interrupted durable-state write simulation, including journal compaction only if a journal is selected;
- VCS checkout adding/deleting/moving hundreds of assets;
- editor focus regain with Auto Refresh enabled and disabled.

## 13. Reviewer checklist

The independent reviewing agent must edit this file directly and complete all items before handing it to the implementation agent.

- [x] Verify every **VERIFIED** call path against the current commit; the baseline is the full current HEAD SHA above.
- [x] Verify Unity API claims against versioned official documentation and narrow live 2022.3.62f3, 6000.0.64f1, and 6000.3.10f1 probes; distinguish probe patches, declared compatibility, current vendor support, and latest-patch release gates.
- [x] Confirm simple `CreateAsset` recipes do not require package-issued `SaveAssets`/`ImportAsset`: official examples plus all three live lanes support removal. Phase 1 still tests every shipped concrete recipe before deleting its calls.
- [x] Determine the single normal external-import strategy: preflight, ignore/exclude, same-volume ignored staging, atomic top-level commit, then short grouped targeted imports with `ImportRecursive`; no global refresh, and an exact-case compatibility fallback replaces rather than overlaps the strategy.
- [x] Validate text/script import and Undo/domain-reload design: reject deferred import/pending history as unmeasured over-architecture; retain one synchronous targeted import and the existing exact history boundary, with a separate-design stop condition if Phase 0 proves the API wait material.
- [x] Check whether a canonical catalog is simpler than finishing existing owners: the spec now prefers the smallest evolution after a measured architecture gate and permits focused derived owners.
- [x] Confirm folder-tree delta/lazy enumeration can preserve the single outer sidebar scroll contract; the non-scrolling nested `ListView` and outer `zep-sidebar-scroll` remain mandatory.
- [x] Explicitly mark Phase-0 numbers unavailable at review; the implementation checkpoint now records the baseline captured before behavior changes and the Phase-1 comparison.
- [x] Add missed hot paths, correctness invariants, compatibility/support concerns, and tests, including ping-created refresh, tag refresh, invalidated-scan synchronous fallback, addressables warmup, lifecycle/closed-panel Undo, package-root duplication, callback splitting, pending-entry generations/case, source `.meta`, ignores, staging, symlinks, and cross-volume local packages.
- [x] Mark rejected proposals and explain why, including deferred-create pending Undo, raw PackageCache authority, long asset-editing pauses, ordinary source `.meta` migration, and undocumented `.meta` authority.
- [x] Change status to “reviewed implementation specification” only after assigning remaining experiments and fail-closed outcomes to phases and gates.

## 14. Implementation-agent contract

The implementation prompt should require the agent to:

1. read `AGENTS.md`, `coding-standards.md`, this reviewed specification, relevant tests, and official Unity sources before edits;
2. treat the user's broad rewrite permission as authorization for the reviewed performance scope, not unrelated cleanup;
3. implement one phase at a time in the listed order;
4. capture the Phase-0 baseline before optimizing;
5. keep the package buildable and tests green after each phase;
6. update this document when measured evidence changes a decision;
7. report new abstractions and delete superseded ones rather than leaving parallel architectures;
8. preserve user-owned worktree changes;
9. create separate, reviewable commits per phase if the user authorizes commits;
10. stop and report when a Unity-version behavior, data-safety invariant, or Undo contract invalidates the planned fast path;
11. finish with the full verification matrix and before/after results, not only a successful compile.

## 15. Recommended implementation sequencing

One capable long-running agent can own the rewrite, but “implement the whole document in one patch” is not the recommended instruction. Use one primary implementation agent for continuity, with explicit checkpoints:

1. Phase 0 and Phase 1, then independent review of measured improvement.
2. Phase 2, then architecture/correctness review.
3. Phases 3 and 4, then domain-reload/startup review.
4. Phases 5 and 6, then performance and compatibility review.
5. Phase 7 release verification.

The primary agent should retain ownership across phases so it understands the catalog and Undo invariants. A second agent should review each checkpoint or at minimum the final diff and benchmark evidence. This is safer and more likely to be faster overall than several implementation agents changing the same ownership spine concurrently.

## 16. Definition of done

The rewrite is complete only when:

- ordinary operations perform no panel full scan/rebuild;
- visible local results no longer wait for global indexing;
- native and Unity boundaries match section 6 or have documented measured exceptions;
- package membership/physical mapping has one registered-root owner and every enabled writable-package operation passed its API/lane gate;
- one canonical membership owner applies every mutation atomically and derived indices consume its delta pipeline;
- duplicate operation/postprocessor notifications are idempotent acknowledgements;
- startup/first-paint critical paths contain no project-wide main-thread scan, and the lightweight reload host preserves closed-panel Undo/events without prewarming catalog/search/addressables;
- search warm/build no longer hitches the main thread;
- external import uses the one staged targeted-import strategy with deterministic cleanup/failure reporting;
- all correctness constraints and automated suites pass;
- release verification records newest-available patches in all three semantic lanes and does not mislabel the legacy 2022.3 compatibility floor as current vendor support;
- performance contracts are recorded on real large projects and compared with Unity's built-in Project window;
- the old synchronization architecture is removed rather than retained beside the new one.

“Feels faster on a small project” is not completion. The proof is stable visible latency, operation counts, project-size-independent local work, and preserved Unity asset correctness.
